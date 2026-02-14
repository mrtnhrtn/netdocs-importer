using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Linq;
using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.NetDocs;

/// <summary>
/// Plans and executes direct NetDocuments uploads, including destination-folder resolution and resumable transfer tracking.
/// </summary>
public sealed class NetDocumentsDirectUploadService : IDirectUploadService
{
    private static readonly Regex StatusCodeRegex = new(@"\((?<status>\d{3})\s", RegexOptions.Compiled);
    private static readonly Regex LegacyWorkspaceIdRegex = new(@"^\d{4}-\d{4}-\d{4}$", RegexOptions.Compiled);
    private const int DefaultMaxUploadConcurrency = 8;
    private const int MaxUploadConcurrency = 8;
    private const int DefaultMaxUploadAttempts = 4;
    private static readonly bool EnablePermissiveAmbiguousFolderListFallback =
        !string.Equals(
            Environment.GetEnvironmentVariable("ND_DIRECTUPLOAD_DISABLE_PERMISSIVE_AMBIGUOUS_FOLDER_LIST"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    private readonly NetDocumentsApiClient _apiClient;
    private readonly JobStore _jobStore;
    private readonly Dictionary<string, string> _workspaceListIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _folderListReliabilityCache = new(StringComparer.OrdinalIgnoreCase);
    private bool? _workspaceListSupported;
    private bool? _folderListSupported;
    private bool? _folderCreateSupported;

    /// <summary>
    /// Initializes the direct upload service.
    /// </summary>
    /// <param name="apiClient">Authenticated API client used for NetDocuments requests.</param>
    /// <param name="jobStore">Job store used for preflight inputs and transfer state persistence.</param>
    /// <exception cref="ArgumentNullException">Thrown when a dependency is null.</exception>
    public NetDocumentsDirectUploadService(NetDocumentsApiClient apiClient, JobStore jobStore)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    }

    private readonly record struct ChildLookupResult(string? ContainerId, bool QueryReliable);

    private readonly record struct FolderListResult(
        Dictionary<string, string> Children,
        bool QueryReliable,
        string TopLevelKeys,
        string ListNode);

    private readonly record struct FolderCreateResult(
        bool Success,
        string? CreatedContainerId,
        string RequestedName,
        bool DuplicateNameConflict);

    private readonly record struct WorkspaceFolderHydrationResult(
        bool Success,
        string Name,
        string ExtensionOrType,
        string FailureReason);

    private readonly record struct UploadWorkItem(
        UploadPlanFileEntry File,
        string TransferId,
        int Attempt);

    /// <summary>
    /// Builds a direct-upload plan for a job by resolving destination folders, validating profile requirements, and classifying skippable files.
    /// </summary>
    /// <param name="jobId">Identifier of the scanned job whose included files should be evaluated.</param>
    /// <param name="target">Confirmed NetDocuments target container for the upload.</param>
    /// <param name="context">Planning options, defaults, and feature switches used by the resolver.</param>
    /// <param name="cancellationToken">Token used to cancel planning work.</param>
    /// <returns>A plan containing folder/file actions, issues, and upload eligibility.</returns>
    /// <exception cref="ArgumentException">Thrown when required planning inputs are missing.</exception>
    public async Task<UploadPlanResult> BuildPlanAsync(
        string jobId,
        NdTargetSelection target,
        DirectUploadPlanContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        if (target is null || string.IsNullOrWhiteSpace(target.Id))
        {
            throw new ArgumentException("A confirmed NetDocuments target is required.", nameof(target));
        }

        var issues = new List<DirectUploadIssue>();
        await _jobStore.InitializeAsync(cancellationToken);

        var files = await _jobStore.GetIncludedFilesForJobAsync(jobId, cancellationToken);
        if (files.Count == 0)
        {
            issues.Add(new DirectUploadIssue(
                DirectUploadIssueSeverity.Warning,
                "NO_FILES",
                "No included files are available for direct upload."));

            return new UploadPlanResult
            {
                Folders = Array.Empty<UploadPlanFolderEntry>(),
                Files = Array.Empty<UploadPlanFileEntry>(),
                Issues = issues,
                PlannedFolderCreates = 0,
                CanUpload = false
            };
        }

        var attributes = string.IsNullOrWhiteSpace(context.CabinetId)
            ? Array.Empty<NetDocumentsAttributeRecord>()
            : await _jobStore.GetNetDocumentsAttributesAsync(context.CabinetId, cancellationToken);
        var requiredAttributes = attributes.Where(a => a.IsRequired).ToList();
        var attributeByName = attributes.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var attributeById = attributes
            .Where(a => !string.IsNullOrWhiteSpace(a.AttributeId))
            .GroupBy(a => a.AttributeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(a => a.AttributeId, StringComparer.OrdinalIgnoreCase);
        var attributeByNum = attributes.ToDictionary(
            a => a.AttributeNum.ToString(CultureInfo.InvariantCulture),
            a => a,
            StringComparer.OrdinalIgnoreCase);

        var folderPathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var folderChildrenCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var folderEntries = new Dictionary<string, UploadPlanFolderEntry>(StringComparer.OrdinalIgnoreCase);
        var fileEntries = new List<UploadPlanFileEntry>(files.Count);
        var resolvedFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _folderListReliabilityCache.Clear();

        var uniqueRelativeFolders = files
            .Select(f => GetRelativeFolderPath(f.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var createCount = 0;
        Trace.WriteLine(
            $"ND-DIRECT {(context.AllowCreateFolders ? "execution-plan" : "preflight")} start files={files.Count} uniqueFolders={uniqueRelativeFolders.Count} targetType={target.Type} targetId='{target.Id}' allowCreate={context.AllowCreateFolders}.");

        foreach (var relativeFolderPath in uniqueRelativeFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationId = await ResolveDestinationContainerAsync(
                target.Id,
                target.Type,
                context.CabinetId,
                relativeFolderPath,
                context.AllowCreateFolders,
                folderPathCache,
                folderChildrenCache,
                folderEntries,
                issues,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(destinationId))
            {
                resolvedFolders[relativeFolderPath] = destinationId;
            }
        }

        createCount = folderEntries.Values.Count(f => f.CreatedDuringPlanning);
        if (context.AllowCreateFolders)
        {
            Trace.WriteLine(
                $"ND-DIRECT execution-plan folders resolved={resolvedFolders.Count} created={createCount} issues={issues.Count}.");
        }
        else
        {
            Trace.WriteLine(
                $"ND-DIRECT preflight dry-run folders resolved={resolvedFolders.Count} planned-folder-creates={createCount} issues={issues.Count}.");
        }

        var zeroByteCount = 0;
        var missingFileCount = 0;
        var missingExtensionCount = 0;
        var skippedCount = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(file.FullPath))
            {
                missingFileCount++;
                skippedCount++;
                Trace.WriteLine(
                    $"ND-DIRECT preflight file skipped reason='missing-file' relativePath='{file.RelativePath}' fullPath='{file.FullPath}'.");
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Info,
                    "MISSING_FILE_SKIPPED",
                    "File not found on disk; skipped from upload.",
                    file.RelativePath));
                continue;
            }

            var sizeBytes = file.SizeBytes;
            if (sizeBytes <= 0)
            {
                try
                {
                    sizeBytes = new FileInfo(file.FullPath).Length;
                }
                catch
                {
                    // Ignore file metadata read failures and keep recorded size.
                }
            }

            if (sizeBytes <= 0)
            {
                zeroByteCount++;
                skippedCount++;
                Trace.WriteLine(
                    $"ND-DIRECT preflight file skipped reason='zero-byte' relativePath='{file.RelativePath}' fullPath='{file.FullPath}'.");
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Info,
                    "ZERO_BYTE_FILE_SKIPPED",
                    "File is zero bytes and was skipped from upload.",
                    file.RelativePath));
                continue;
            }

            var extension = Path.GetExtension(file.FullPath);
            if (string.IsNullOrWhiteSpace(extension) || string.Equals(extension, ".", StringComparison.Ordinal))
            {
                missingExtensionCount++;
                skippedCount++;
                Trace.WriteLine(
                    $"ND-DIRECT preflight file skipped reason='missing-extension' relativePath='{file.RelativePath}' fullPath='{file.FullPath}'.");
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Info,
                    "MISSING_EXTENSION_FILE_SKIPPED",
                    "File has no extension and was skipped from upload.",
                    file.RelativePath));
                continue;
            }

            var relativeFolderPath = GetRelativeFolderPath(file.RelativePath);
            if (!resolvedFolders.TryGetValue(relativeFolderPath, out var destinationId) || string.IsNullOrWhiteSpace(destinationId))
            {
                continue;
            }

            var profileValues = await BuildProfileValuesForFileAsync(
                file,
                context,
                attributeByName,
                attributeById,
                attributeByNum,
                cancellationToken);

            foreach (var required in requiredAttributes)
            {
                if (!profileValues.ContainsKey(required.Name))
                {
                    issues.Add(new DirectUploadIssue(
                        DirectUploadIssueSeverity.Error,
                        "REQUIRED_PROFILE_MISSING",
                        $"Required profile attribute '{required.Name}' is missing.",
                        file.RelativePath));
                }
            }

            if (context.RequireAcl && string.IsNullOrWhiteSpace(context.DefaultAcl))
            {
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Error,
                    "ACL_REQUIRED",
                    "ACL is required for direct upload but was not provided.",
                    file.RelativePath));
            }

            var useMultipartUpload =
                context.EnableMultipartUpload &&
                sizeBytes >= context.MultipartThresholdBytes &&
                sizeBytes <= context.MultipartMaxFileSizeBytes;

            fileEntries.Add(new UploadPlanFileEntry(
                file.FileId,
                file.RelativePath,
                file.FullPath,
                sizeBytes,
                destinationId,
                profileValues,
                context.DefaultAcl,
                useMultipartUpload));
        }

        Trace.WriteLine(
            $"ND-DIRECT {(context.AllowCreateFolders ? "execution-plan" : "preflight")} files requested={files.Count} planned={fileEntries.Count} skipped={skippedCount} zeroByte={zeroByteCount} missing={missingFileCount} missingExtension={missingExtensionCount} planned-folder-creates={createCount} issues={issues.Count}.");

        var canUpload = fileEntries.Count > 0 && issues.All(i => i.Severity != DirectUploadIssueSeverity.Error);
        return new UploadPlanResult
        {
            Folders = folderEntries.Values.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            Files = fileEntries,
            Issues = issues,
            TotalRequestedFiles = files.Count,
            PlannedFiles = fileEntries.Count,
            SkippedFiles = skippedCount,
            PlannedFolderCreates = createCount,
            CanUpload = canUpload
        };
    }

    /// <summary>
    /// Executes a prepared upload plan with adaptive concurrency, retry handling, and resumable transfer tracking.
    /// </summary>
    /// <param name="plan">Prepared plan generated by <see cref="BuildPlanAsync"/>.</param>
    /// <param name="context">Execution options and defaults.</param>
    /// <param name="progress">Optional progress sink that receives per-file completion updates.</param>
    /// <param name="cancellationToken">Token used to cancel the upload run.</param>
    /// <returns>Run summary and per-file outcomes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="plan"/> is null.</exception>
    public async Task<DirectUploadRunResult> UploadAsync(
        UploadPlanResult plan,
        DirectUploadPlanContext context,
        IProgress<DirectUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var files = plan.Files;
        var totalFiles = files.Count;
        if (totalFiles == 0)
        {
            return new DirectUploadRunResult
            {
                Files = Array.Empty<DirectUploadFileResult>(),
                TotalRequestedFiles = plan.TotalRequestedFiles,
                PlannedFiles = plan.PlannedFiles,
                SkippedFiles = plan.SkippedFiles,
                CreatedFolders = context.AllowCreateFolders ? plan.PlannedFolderCreates : 0,
                SucceededFiles = 0,
                FailedFiles = 0,
                ResumedFiles = 0
            };
        }

        var maxConcurrency = Math.Clamp(
            context.MaxConcurrency <= 0 ? DefaultMaxUploadConcurrency : context.MaxConcurrency,
            1,
            MaxUploadConcurrency);
        var maxAttempts = Math.Clamp(
            context.MaxRetryAttempts <= 0 ? DefaultMaxUploadAttempts : context.MaxRetryAttempts,
            1,
            8);
        var initialConcurrency = Math.Min(DefaultMaxUploadConcurrency, maxConcurrency);
        var throttle = new AdaptiveUploadController(1, maxConcurrency, initialConcurrency);

        var transferStates = !string.IsNullOrWhiteSpace(context.JobId)
            ? await _jobStore.GetTransferStatesByFileAsync(context.JobId, cancellationToken)
            : new Dictionary<string, TransferState>(StringComparer.OrdinalIgnoreCase);

        var resultsByFileId = new ConcurrentDictionary<string, DirectUploadFileResult>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<UploadWorkItem>(files.Count);
        var completed = 0;
        var resumedFiles = 0;

        foreach (var file in files)
        {
            if (transferStates.TryGetValue(file.FileId, out var state) &&
                string.Equals(state.Status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                resultsByFileId[file.FileId] = new DirectUploadFileResult(
                    file.RelativePath,
                    true,
                    200,
                    "Already uploaded in a previous run (resumed).");
                resumedFiles++;
                var resumedCompleted = Interlocked.Increment(ref completed);
                progress?.Report(new DirectUploadProgress(
                    resumedCompleted,
                    totalFiles,
                    file.RelativePath,
                    ComputePercent(resumedCompleted, totalFiles)));
                continue;
            }

            var transferId = transferStates.TryGetValue(file.FileId, out var existing)
                ? existing.TransferId
                : Guid.NewGuid().ToString("N");
            var attempt = transferStates.TryGetValue(file.FileId, out var existingState)
                ? Math.Clamp(existingState.Attempt + 1, 1, maxAttempts)
                : 1;
            pending.Add(new UploadWorkItem(file, transferId, attempt));
        }

        if (pending.Count > 0)
        {
            var channel = Channel.CreateBounded<UploadWorkItem>(new BoundedChannelOptions(maxConcurrency * 2)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

            var writer = channel.Writer;
            var reader = channel.Reader;

            var queueTask = Task.Run(async () =>
            {
                foreach (var item in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await UpsertQueuedTransferAsync(context, item, cancellationToken);
                    await writer.WriteAsync(item, cancellationToken);
                }

                writer.TryComplete();
            }, cancellationToken);

            var workers = Enumerable.Range(0, maxConcurrency)
                .Select(workerId => Task.Run(() =>
                    UploadWorkerLoopAsync(
                        workerId,
                        reader,
                        context,
                        maxAttempts,
                        totalFiles,
                        throttle,
                        resultsByFileId,
                        progress,
                        () => Interlocked.Increment(ref completed),
                        cancellationToken), cancellationToken))
                .ToArray();

            await Task.WhenAll(workers.Append(queueTask));
        }

        var ordered = files
            .Select(file => resultsByFileId.TryGetValue(file.FileId, out var result)
                ? result
                : new DirectUploadFileResult(file.RelativePath, false, 0, "Upload did not produce a result."))
            .ToList();

        return new DirectUploadRunResult
        {
            Files = ordered,
            TotalRequestedFiles = plan.TotalRequestedFiles,
            PlannedFiles = plan.PlannedFiles,
            SkippedFiles = plan.SkippedFiles,
            CreatedFolders = context.AllowCreateFolders ? plan.PlannedFolderCreates : 0,
            SucceededFiles = ordered.Count(r => r.Succeeded),
            FailedFiles = ordered.Count(r => !r.Succeeded),
            ResumedFiles = resumedFiles
        };
    }

    private async Task UploadWorkerLoopAsync(
        int workerId,
        ChannelReader<UploadWorkItem> reader,
        DirectUploadPlanContext context,
        int maxAttempts,
        int totalFiles,
        AdaptiveUploadController throttle,
        ConcurrentDictionary<string, DirectUploadFileResult> resultsByFileId,
        IProgress<DirectUploadProgress>? progress,
        Func<int> incrementCompleted,
        CancellationToken cancellationToken)
    {
        await foreach (var workItem in reader.ReadAllAsync(cancellationToken))
        {
            var attempt = workItem.Attempt;
            while (attempt <= maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await throttle.WaitForSlotAsync(workerId, cancellationToken);
                var startedUtc = DateTime.UtcNow;
                await UpdateRunningTransferAsync(context, workItem.TransferId, attempt, workerId, startedUtc, cancellationToken);

                var stopwatch = Stopwatch.StartNew();
                DirectUploadFileResult uploadResult;
                try
                {
                    uploadResult = await TryUploadFileAsync(workItem.File, context, cancellationToken);
                }
                catch (Exception ex)
                {
                    uploadResult = new DirectUploadFileResult(workItem.File.RelativePath, false, 0, ex.Message);
                }
                finally
                {
                    stopwatch.Stop();
                }

                var statusCode = uploadResult.HttpStatus;
                var succeeded = uploadResult.Succeeded;
                var retryAfter = throttle.RegisterOutcome(statusCode, succeeded);
                await UpdateFinishedTransferAsync(
                    context,
                    workItem.TransferId,
                    succeeded ? "Succeeded" : "Failed",
                    stopwatch.ElapsedMilliseconds,
                    uploadResult.Message,
                    statusCode <= 0 ? null : statusCode,
                    cancellationToken);

                if (succeeded)
                {
                    resultsByFileId[workItem.File.FileId] = uploadResult;
                    var completed = incrementCompleted();
                    progress?.Report(new DirectUploadProgress(
                        completed,
                        totalFiles,
                        workItem.File.RelativePath,
                        ComputePercent(completed, totalFiles)));
                    break;
                }

                var transientFailure = IsTransientUploadStatus(statusCode);
                if (!transientFailure || attempt >= maxAttempts)
                {
                    resultsByFileId[workItem.File.FileId] = uploadResult;
                    var completed = incrementCompleted();
                    progress?.Report(new DirectUploadProgress(
                        completed,
                        totalFiles,
                        workItem.File.RelativePath,
                        ComputePercent(completed, totalFiles)));
                    break;
                }

                attempt++;
                var retryItem = new UploadWorkItem(workItem.File, workItem.TransferId, attempt);
                await UpsertQueuedTransferAsync(context, retryItem, cancellationToken);
                var retryDelay = retryAfter > TimeSpan.Zero
                    ? retryAfter
                    : TimeSpan.FromMilliseconds(500 * attempt);
                Trace.WriteLine(
                    $"ND-DIRECT upload retry relativePath='{workItem.File.RelativePath}' attempt={attempt}/{maxAttempts} delayMs={retryDelay.TotalMilliseconds:F0} status={statusCode}.");
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    private async Task UpsertQueuedTransferAsync(
        DirectUploadPlanContext context,
        UploadWorkItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.JobId))
        {
            return;
        }

        await _jobStore.UpsertTransferQueuedAsync(
            new TransferRecord(
                item.TransferId,
                context.JobId,
                item.File.FileId,
                item.Attempt,
                "Queued",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            cancellationToken);
    }

    private async Task UpdateRunningTransferAsync(
        DirectUploadPlanContext context,
        string transferId,
        int attempt,
        int workerId,
        DateTime startedUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.JobId))
        {
            return;
        }

        await _jobStore.UpdateTransferRunningAsync(
            transferId,
            attempt,
            startedUtc,
            workerId,
            cancellationToken);
    }

    private async Task UpdateFinishedTransferAsync(
        DirectUploadPlanContext context,
        string transferId,
        string status,
        long durationMs,
        string? error,
        int? httpStatus,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.JobId))
        {
            return;
        }

        var snippet = string.IsNullOrWhiteSpace(error)
            ? null
            : (error.Length > 256 ? error[..256] : error);

        await _jobStore.UpdateTransferFinishedAsync(
            transferId,
            status,
            DateTime.UtcNow,
            durationMs,
            status == "Succeeded" ? null : error,
            httpStatus,
            snippet,
            null,
            cancellationToken);
    }

    private static bool IsTransientUploadStatus(int statusCode)
    {
        return statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    }

    private static double ComputePercent(int completed, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Round((double)completed / total * 100d, 2);
    }

    private async Task<DirectUploadFileResult> TryUploadFileAsync(
        UploadPlanFileEntry file,
        DirectUploadPlanContext context,
        CancellationToken cancellationToken)
    {
        var v1DocumentPath = BuildV1DocumentUploadPath(context.V1DocumentIndexPriority);
        var candidateEndpoints = new[]
        {
            (Path: v1DocumentPath, IncludeAction: true)
        };

        var failureMessages = new List<string>();

        foreach (var candidate in candidateEndpoints)
        {
            try
            {
                await using var stream = File.OpenRead(file.FullPath);
                using var multipart = new MultipartFormDataContent();

                if (candidate.IncludeAction)
                {
                    multipart.Add(new StringContent("upload", Encoding.UTF8), "action");
                }

                multipart.Add(new StringContent(Path.GetFileNameWithoutExtension(file.FullPath), Encoding.UTF8), "name");
                var extension = Path.GetExtension(file.FullPath).TrimStart('.');
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    multipart.Add(new StringContent(extension, Encoding.UTF8), "extension");
                }

                if (!string.IsNullOrWhiteSpace(context.CabinetId))
                {
                    multipart.Add(new StringContent(context.CabinetId, Encoding.UTF8), "cabinet");
                }

                multipart.Add(new StringContent(file.DestinationContainerId, Encoding.UTF8), "destination");

                if (file.ProfileValues.Count > 0)
                {
                    var profileJson = JsonSerializer.Serialize(file.ProfileValues);
                    multipart.Add(new StringContent(profileJson, Encoding.UTF8, "application/json"), "profile");
                }

                var streamContent = new StreamContent(stream);
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(streamContent, "file", Path.GetFileName(file.FullPath));

                if (!string.IsNullOrWhiteSpace(file.Acl))
                {
                    multipart.Add(new StringContent(file.Acl, Encoding.UTF8), "acl");
                }

                await _apiClient.PostAsync(candidate.Path, multipart, cancellationToken, retryOnThrottle: false);
                Trace.WriteLine(
                    $"ND-DIRECT upload success endpoint='{candidate.Path}' relativePath='{file.RelativePath}' destination='{file.DestinationContainerId}'.");
                return new DirectUploadFileResult(file.RelativePath, true, 200, "Uploaded");
            }
            catch (Exception ex)
            {
                var status = TryExtractStatusCode(ex);
                if (status is 400 or 404 or 405 or 415 or 500 or 501)
                {
                    failureMessages.Add($"{candidate.Path}:{status}");
                    Trace.WriteLine(
                        $"ND-DIRECT upload endpoint rejected endpoint='{candidate.Path}' relativePath='{file.RelativePath}' status={status} message='{SanitizeForTrace(ex.Message)}'.");
                    continue;
                }

                Trace.WriteLine(
                    $"ND-DIRECT upload failed endpoint='{candidate.Path}' relativePath='{file.RelativePath}' status={status ?? 0} message='{SanitizeForTrace(ex.Message)}'.");
                return new DirectUploadFileResult(file.RelativePath, false, status ?? 0, ex.Message);
            }
        }

        return new DirectUploadFileResult(
            file.RelativePath,
            false,
            0,
            failureMessages.Count > 0
                ? $"No supported upload endpoint accepted the request for this destination ({string.Join(",", failureMessages)})."
                : "No supported upload endpoint accepted the request for this destination.");
    }

    private static string BuildV1DocumentUploadPath(int? indexPriority)
    {
        if (!indexPriority.HasValue || indexPriority.Value <= 0)
        {
            return "/v1/Document";
        }

        return $"/v1/Document?indexpriority={indexPriority.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    private async Task<Dictionary<string, string>> BuildProfileValuesForFileAsync(
        FileRecord file,
        DirectUploadPlanContext context,
        IReadOnlyDictionary<string, NetDocumentsAttributeRecord> attributeByName,
        IReadOnlyDictionary<string, NetDocumentsAttributeRecord> attributeById,
        IReadOnlyDictionary<string, NetDocumentsAttributeRecord> attributeByNum,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profileDefault in context.EffectiveProfileDefaults.ValuesByAttributeId.Values)
        {
            var resolved = ResolveAttribute(profileDefault.AttributeId, profileDefault.AttributeName, attributeByName, attributeById, attributeByNum);
            var key = resolved?.Name ?? profileDefault.AttributeName ?? profileDefault.AttributeId;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(profileDefault.RawValue))
            {
                continue;
            }

            values[key] = profileDefault.RawValue;
        }

        if (!string.IsNullOrWhiteSpace(file.FolderId))
        {
            var payload = await _jobStore.GetEffectiveFolderProfilePayloadAsync(file.FolderId, cancellationToken);
            foreach (var entry in ProfilePayloadCodec.Deserialize(payload))
            {
                if (string.IsNullOrWhiteSpace(entry.Field))
                {
                    continue;
                }

                NetDocumentsAttributeRecord? resolved = null;
                if (entry.Mode == ProfileFieldMode.Code)
                {
                    resolved = ResolveAttribute(entry.Field, entry.Field, attributeByName, attributeById, attributeByNum);
                }
                else if (!attributeByName.TryGetValue(entry.Field, out resolved))
                {
                    resolved = ResolveAttribute(entry.Field, entry.Field, attributeByName, attributeById, attributeByNum);
                }

                var key = resolved?.Name ?? entry.Field;
                values[key] = entry.Value ?? string.Empty;
            }
        }

        return values;
    }

    private static NetDocumentsAttributeRecord? ResolveAttribute(
        string? attributeId,
        string? attributeName,
        IReadOnlyDictionary<string, NetDocumentsAttributeRecord> attributeByName,
        IReadOnlyDictionary<string, NetDocumentsAttributeRecord> attributeById,
        IReadOnlyDictionary<string, NetDocumentsAttributeRecord> attributeByNum)
    {
        if (!string.IsNullOrWhiteSpace(attributeId))
        {
            if (attributeById.TryGetValue(attributeId, out var byId))
            {
                return byId;
            }

            if (attributeByNum.TryGetValue(attributeId, out var byNum))
            {
                return byNum;
            }
        }

        if (!string.IsNullOrWhiteSpace(attributeName) &&
            attributeByName.TryGetValue(attributeName, out var byName))
        {
            return byName;
        }

        return null;
    }

    private async Task<string?> ResolveDestinationContainerAsync(
        string targetContainerId,
        NdTargetType targetType,
        string cabinetId,
        string relativeFolderPath,
        bool allowCreateFolders,
        IDictionary<string, string> folderPathCache,
        IDictionary<string, Dictionary<string, string>> folderChildrenCache,
        IDictionary<string, UploadPlanFolderEntry> folderEntries,
        ICollection<DirectUploadIssue> issues,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativeFolderPath))
        {
            return targetContainerId;
        }

        if (folderPathCache.TryGetValue(relativeFolderPath, out var cached))
        {
            return cached;
        }

        var currentContainerId = targetContainerId;
        var currentPath = string.Empty;
        var isFirstSegment = true;
        foreach (var segment in SplitPath(relativeFolderPath))
        {
            currentPath = string.IsNullOrWhiteSpace(currentPath) ? segment : $"{currentPath}/{segment}";
            if (folderPathCache.TryGetValue(currentPath, out var cachedSegment))
            {
                currentContainerId = cachedSegment;
                isFirstSegment = false;
                continue;
            }

            var lookup = await TryFindChildContainerByNameAsync(
                parentContainerId: currentContainerId,
                childName: segment,
                isWorkspaceRoot: isFirstSegment && targetType == NdTargetType.Workspace,
                folderChildrenCache: folderChildrenCache,
                issues: issues,
                cancellationToken: cancellationToken);
            var childContainerId = lookup.ContainerId;
            var created = false;

            if (string.IsNullOrWhiteSpace(childContainerId) && !lookup.QueryReliable)
            {
                Trace.WriteLine(
                    $"ND-DIRECT folder-create blocked reason='query-unreliable' parent='{currentContainerId}' segment='{segment}' path='{currentPath}'.");
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Error,
                    "FOLDER_ENUMERATION_UNRELIABLE",
                    $"Unable to verify existing folders for '{currentPath}'. Folder creation is blocked until folder listing queries return a recognized shape.",
                    currentPath));
                return null;
            }

            if (string.IsNullOrWhiteSpace(childContainerId) && !allowCreateFolders && lookup.QueryReliable)
            {
                created = true;
                var plannedName = NormalizeCreatedFolderName(segment);
                childContainerId = $"planned:{Uri.EscapeDataString(currentPath)}";
                if (!folderChildrenCache.TryGetValue(currentContainerId, out var cachedChildren))
                {
                    cachedChildren = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    folderChildrenCache[currentContainerId] = cachedChildren;
                }

                cachedChildren[NormalizeFolderName(plannedName)] = childContainerId;
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Info,
                    "FOLDER_CREATE_PLANNED",
                    $"Folder '{currentPath}' does not exist and would be created during upload execution.",
                    currentPath));
                Trace.WriteLine(
                    $"ND-DIRECT preflight dry-run planned-folder-create parent='{currentContainerId}' segment='{segment}' path='{currentPath}'.");
            }

            if (string.IsNullOrWhiteSpace(childContainerId) && allowCreateFolders && lookup.QueryReliable)
            {
                var createResult = await TryCreateChildFolderAsync(
                    parentContainerId: currentContainerId,
                    childName: segment,
                    cabinetId: cabinetId,
                    folderChildrenCache: folderChildrenCache,
                    cancellationToken: cancellationToken);
                if (createResult.Success)
                {
                    created = true;
                    if (!string.IsNullOrWhiteSpace(createResult.CreatedContainerId))
                    {
                        childContainerId = createResult.CreatedContainerId;
                        if (!folderChildrenCache.TryGetValue(currentContainerId, out var cachedChildren))
                        {
                            cachedChildren = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            folderChildrenCache[currentContainerId] = cachedChildren;
                        }

                        cachedChildren[NormalizeFolderName(createResult.RequestedName)] = createResult.CreatedContainerId;
                    }
                    else
                    {
                        // Force a fresh parent listing so we can discover the just-created folder by name.
                        folderChildrenCache.Remove(currentContainerId);
                        _folderListReliabilityCache.Remove(currentContainerId);
                        var refreshLookup = await TryFindChildContainerByNameAsync(
                            parentContainerId: currentContainerId,
                            childName: createResult.RequestedName,
                            isWorkspaceRoot: isFirstSegment && targetType == NdTargetType.Workspace,
                            folderChildrenCache: folderChildrenCache,
                            issues: issues,
                            cancellationToken: cancellationToken);
                        childContainerId = refreshLookup.ContainerId;
                    }
                }
                else if (createResult.DuplicateNameConflict)
                {
                    folderChildrenCache.Remove(currentContainerId);
                    _folderListReliabilityCache.Remove(currentContainerId);
                    var refreshLookup = await TryFindChildContainerByNameAsync(
                        parentContainerId: currentContainerId,
                        childName: createResult.RequestedName,
                        isWorkspaceRoot: isFirstSegment && targetType == NdTargetType.Workspace,
                        folderChildrenCache: folderChildrenCache,
                        issues: issues,
                        cancellationToken: cancellationToken);
                    childContainerId = refreshLookup.ContainerId;
                    if (!string.IsNullOrWhiteSpace(childContainerId))
                    {
                        Trace.WriteLine(
                            $"ND-DIRECT folder-create duplicate-resolved parent='{currentContainerId}' name='{createResult.RequestedName}' id='{childContainerId}'.");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(childContainerId))
            {
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Error,
                    "FOLDER_RESOLVE_FAILED",
                    $"Unable to resolve or create folder segment '{segment}' under '{currentPath}'.",
                    currentPath));
                return null;
            }

            currentContainerId = childContainerId;
            folderPathCache[currentPath] = childContainerId;
            folderEntries[currentPath] = new UploadPlanFolderEntry(currentPath, childContainerId, created);
            isFirstSegment = false;
        }

        return currentContainerId;
    }

    private async Task<ChildLookupResult> TryFindChildContainerByNameAsync(
        string parentContainerId,
        string childName,
        bool isWorkspaceRoot,
        IDictionary<string, Dictionary<string, string>> folderChildrenCache,
        ICollection<DirectUploadIssue> issues,
        CancellationToken cancellationToken)
    {
        if (parentContainerId.StartsWith("planned:", StringComparison.OrdinalIgnoreCase))
        {
            if (!folderChildrenCache.TryGetValue(parentContainerId, out var plannedChildren))
            {
                plannedChildren = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                folderChildrenCache[parentContainerId] = plannedChildren;
            }

            var plannedId = plannedChildren.TryGetValue(NormalizeFolderName(childName), out var cachedPlanned)
                ? cachedPlanned
                : null;

            if (!string.IsNullOrWhiteSpace(plannedId))
            {
                Trace.WriteLine(
                    $"ND-DIRECT folder-resolve matched parent='{parentContainerId}' child='{childName}' id='{plannedId}' source='planned-cache'.");
            }
            else
            {
                Trace.WriteLine(
                    $"ND-DIRECT folder-resolve parent='{parentContainerId}' child='{childName}' source='planned-cache' no-match.");
            }

            // Dry-run virtual parents are local plan artifacts and must not call server listing APIs.
            return new ChildLookupResult(plannedId, true);
        }

        if (folderChildrenCache.TryGetValue(parentContainerId, out var cachedMap))
        {
            var cachedReliability = _folderListReliabilityCache.TryGetValue(parentContainerId, out var reliable)
                ? reliable
                : true;
            var cachedId = cachedMap.TryGetValue(NormalizeFolderName(childName), out var cached) ? cached : null;
            if (!string.IsNullOrWhiteSpace(cachedId))
            {
                Trace.WriteLine(
                    $"ND-DIRECT folder-resolve matched parent='{parentContainerId}' child='{childName}' id='{cachedId}' source='cache'.");
            }
            return new ChildLookupResult(cachedId, cachedReliability);
        }

        var listResult = await LoadChildFoldersAsync(parentContainerId, isWorkspaceRoot, issues, cancellationToken);
        folderChildrenCache[parentContainerId] = listResult.Children;
        _folderListReliabilityCache[parentContainerId] = listResult.QueryReliable;
        var found = listResult.Children.TryGetValue(NormalizeFolderName(childName), out var foundId) ? foundId : null;
        if (!string.IsNullOrWhiteSpace(found))
        {
            Trace.WriteLine(
                $"ND-DIRECT folder-resolve matched parent='{parentContainerId}' child='{childName}' id='{found}' source='query'.");
        }
        return new ChildLookupResult(found, listResult.QueryReliable);
    }

    private async Task<FolderListResult> LoadChildFoldersAsync(
        string parentContainerId,
        bool isWorkspaceRoot,
        ICollection<DirectUploadIssue> issues,
        CancellationToken cancellationToken)
    {
        var children = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parentForPath = isWorkspaceRoot
            ? await ResolveWorkspaceListIdAsync(parentContainerId, cancellationToken)
            : parentContainerId;
        var escapedParent = Uri.EscapeDataString(parentForPath);
        var endpoint = isWorkspaceRoot
            ? $"/v1/Workspace/{escapedParent}"
            : $"/v1/Folder/{escapedParent}";
        var topLevelKeys = string.Empty;
        var listNode = string.Empty;

        if (isWorkspaceRoot && _workspaceListSupported == false)
        {
            return new FolderListResult(children, false, topLevelKeys, listNode);
        }

        if (!isWorkspaceRoot && _folderListSupported == false)
        {
            return new FolderListResult(children, false, topLevelKeys, listNode);
        }

        try
        {
            using var document = await _apiClient.GetJsonAsync(endpoint, cancellationToken);
            topLevelKeys = GetTopLevelKeys(document.RootElement);
            if (!TryEnumerateFolderItems(document.RootElement, out var items, out listNode))
            {
                if (isWorkspaceRoot)
                {
                    _workspaceListSupported = false;
                }
                else
                {
                    _folderListSupported = false;
                }

                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Error,
                    "FOLDER_LIST_SHAPE_UNRECOGNIZED",
                    $"Folder listing response shape for '{endpoint}' was not recognized. Creation is blocked to avoid duplicate or misplaced folders.",
                    parentContainerId));

                Trace.WriteLine(
                    $"ND-DIRECT folder-list unrecognized endpoint='{endpoint}' parent='{parentContainerId}' topKeys='{topLevelKeys}'.");
                return new FolderListResult(children, false, topLevelKeys, listNode);
            }

            var totalItems = 0;
            var hydrationCandidates = 0;
            var hydrationResolved = 0;
            var hydrationFailures = 0;
            foreach (var item in items)
            {
                totalItems++;
                var id = ReadString(item, "id", "containerId", "envId", "nev", "folderId");
                var name = ReadString(item, "name", "displayName", "title", "description");
                var extension = ReadString(item, "extension", "ext", "Ext", "type", "objectType", "fileType");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var resolvedName = name;
                var resolvedType = extension;
                if (ShouldHydrateFolderListEntry(id, extension) &&
                    (string.IsNullOrWhiteSpace(resolvedName) || !IsFolderItem(item, id, resolvedType)))
                {
                    hydrationCandidates++;
                    var hydrated = await TryHydrateWorkspaceFolderAsync(id, cancellationToken);
                    if (hydrated.Success)
                    {
                        hydrationResolved++;
                        if (string.IsNullOrWhiteSpace(resolvedName))
                        {
                            resolvedName = hydrated.Name;
                        }

                        if (string.IsNullOrWhiteSpace(resolvedType))
                        {
                            resolvedType = hydrated.ExtensionOrType;
                        }
                    }
                    else
                    {
                        hydrationFailures++;
                        Trace.WriteLine(
                            $"ND-DIRECT folder-list hydration-failed endpoint='{endpoint}' parent='{parentContainerId}' id='{id}' reason='{hydrated.FailureReason}'.");
                    }
                }

                if (string.IsNullOrWhiteSpace(resolvedName))
                {
                    continue;
                }

                if (!IsFolderItem(item, id, resolvedType))
                {
                    continue;
                }

                children[NormalizeFolderName(resolvedName)] = id;
            }

            if (isWorkspaceRoot)
            {
                Trace.WriteLine(
                    $"ND-DIRECT workspace-list raw={totalItems} hydrated={hydrationResolved} hydrationFailures={hydrationFailures} endpoint='{endpoint}' parent='{parentContainerId}'.");
                if (hydrationCandidates > 0 && hydrationFailures > 0)
                {
                    issues.Add(new DirectUploadIssue(
                        DirectUploadIssueSeverity.Error,
                        "FOLDER_LIST_HYDRATION_FAILED",
                        $"Workspace folder listing for '{parentContainerId}' could not be reliably hydrated from container info. Folder creation is blocked to avoid duplicates.",
                        parentContainerId));
                    return new FolderListResult(children, false, topLevelKeys, listNode);
                }
            }
            else
            {
                Trace.WriteLine(
                    $"ND-DIRECT folder-list raw={totalItems} hydrated={hydrationResolved} hydrationFailures={hydrationFailures} endpoint='{endpoint}' parent='{parentContainerId}'.");
                if (hydrationCandidates > 0 && hydrationFailures > 0)
                {
                    issues.Add(new DirectUploadIssue(
                        DirectUploadIssueSeverity.Error,
                        "FOLDER_LIST_HYDRATION_FAILED",
                        $"Folder listing for '{parentContainerId}' could not be reliably hydrated from container info. Folder creation is blocked to avoid duplicates.",
                        parentContainerId));
                    return new FolderListResult(children, false, topLevelKeys, listNode);
                }
            }

            if (totalItems > 0 && children.Count == 0)
            {
                var sample = items[0];
                var sampleKeys = sample.ValueKind == JsonValueKind.Object
                    ? string.Join(",", sample.EnumerateObject().Select(p => p.Name).Take(20))
                    : sample.ValueKind.ToString();
                var sampleId = ReadString(sample, "id", "containerId", "envId", "nev", "folderId");
                var sampleName = ReadString(sample, "name", "displayName", "title", "description");
                var sampleExt = ReadString(sample, "extension", "ext", "Ext", "type", "objectType", "fileType");
                Trace.WriteLine(
                    $"ND-DIRECT folder-list filtered-all endpoint='{endpoint}' parent='{parentContainerId}' listNode='{listNode}' topKeys='{topLevelKeys}' sampleKeys='{sampleKeys}' sampleId='{sampleId}' sampleName='{sampleName}' sampleType='{sampleExt}'.");

                if (children.Count == 0)
                {
                    if (isWorkspaceRoot)
                    {
                        _workspaceListSupported = true;
                    }
                    else
                    {
                        _folderListSupported = true;
                    }

                    if (EnablePermissiveAmbiguousFolderListFallback)
                    {
                        issues.Add(new DirectUploadIssue(
                            DirectUploadIssueSeverity.Warning,
                            "FOLDER_LIST_AMBIGUOUS_PERMISSIVE",
                            $"Folder listing for '{endpoint}' returned items but none could be matched as folders under '{parentContainerId}'. Proceeding with permissive fallback and optimistic folder-create planning.",
                            parentContainerId));

                        Trace.WriteLine(
                            $"ND-DIRECT folder-list permissive-fallback endpoint='{endpoint}' parent='{parentContainerId}' reason='filtered-all'.");

                        return new FolderListResult(children, true, topLevelKeys, listNode);
                    }

                    issues.Add(new DirectUploadIssue(
                        DirectUploadIssueSeverity.Error,
                        "FOLDER_LIST_AMBIGUOUS",
                        $"Folder listing for '{endpoint}' returned items but none could be matched as folders under '{parentContainerId}'. Creation is blocked to avoid duplicates.",
                        parentContainerId));

                    return new FolderListResult(children, false, topLevelKeys, listNode);
                }
            }

            if (isWorkspaceRoot)
            {
                _workspaceListSupported = true;
            }
            else
            {
                _folderListSupported = true;
            }

            Trace.WriteLine(
                $"ND-DIRECT folder-list success endpoint='{endpoint}' parent='{parentContainerId}' listNode='{listNode}' topKeys='{topLevelKeys}' count={children.Count}.");
        }
        catch (Exception ex)
        {
            var status = TryExtractStatusCode(ex) ?? 0;
            if (isWorkspaceRoot)
            {
                _workspaceListSupported = false;
            }
            else
            {
                _folderListSupported = false;
            }

            issues.Add(new DirectUploadIssue(
                DirectUploadIssueSeverity.Error,
                "FOLDER_LIST_FAILED",
                $"Unable to list {(isWorkspaceRoot ? "workspace" : "folder")} children for '{parentContainerId}' (status {status}).",
                parentContainerId));

            Trace.WriteLine(
                $"ND-DIRECT folder-list failed endpoint='{endpoint}' parent='{parentContainerId}' status={status} message='{SanitizeForTrace(ex.Message)}'.");

            return new FolderListResult(children, false, topLevelKeys, listNode);
        }

        return new FolderListResult(children, true, topLevelKeys, listNode);
    }

    private async Task<string> ResolveWorkspaceListIdAsync(string targetWorkspaceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetWorkspaceId))
        {
            return targetWorkspaceId;
        }

        if (_workspaceListIdCache.TryGetValue(targetWorkspaceId, out var cached))
        {
            return cached;
        }

        var resolved = targetWorkspaceId;
        if (targetWorkspaceId.StartsWith(":", StringComparison.Ordinal))
        {
            try
            {
                var encoded = EncodeContainerIdForPath(targetWorkspaceId);
                using var document = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/info", cancellationToken);
                if (TryExtractLegacyWorkspaceId(document.RootElement, out var legacy))
                {
                    resolved = legacy;
                    Trace.WriteLine(
                        $"ND-DIRECT workspace-id normalized target='{targetWorkspaceId}' workspace='{legacy}'.");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"ND-DIRECT workspace-id normalize failed target='{targetWorkspaceId}' message='{SanitizeForTrace(ex.Message)}'.");
            }
        }

        _workspaceListIdCache[targetWorkspaceId] = resolved;
        return resolved;
    }

    private async Task<FolderCreateResult> TryCreateChildFolderAsync(
        string parentContainerId,
        string childName,
        string cabinetId,
        IDictionary<string, Dictionary<string, string>> folderChildrenCache,
        CancellationToken cancellationToken)
    {
        if (_folderCreateSupported == false)
        {
            return new FolderCreateResult(false, null, NormalizeCreatedFolderName(childName), false);
        }

        var requestedName = NormalizeCreatedFolderName(childName);
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = requestedName,
            ["parent"] = parentContainerId
        };
        if (!string.IsNullOrWhiteSpace(cabinetId))
        {
            payload["cabinet"] = cabinetId;
        }

        try
        {
            using var content = new FormUrlEncodedContent(payload);
            using var response = await _apiClient.PostJsonAsync("/v1/Folder", content, cancellationToken);
            _folderCreateSupported = true;
            folderChildrenCache.Remove(parentContainerId);
            var createdId = response is null ? null : TryExtractCreatedContainerId(response.RootElement);
            Trace.WriteLine(
                $"ND-DIRECT folder-create success endpoint='/v1/Folder' parent='{parentContainerId}' name='{requestedName}' id='{createdId ?? string.Empty}'.");
            return new FolderCreateResult(true, createdId, requestedName, false);
        }
        catch (Exception ex)
        {
            var status = TryExtractStatusCode(ex) ?? 0;
            _folderCreateSupported = false;
            var duplicateNameConflict = status == 400 &&
                                        ex.Message.IndexOf("already contains a folder with this name", StringComparison.OrdinalIgnoreCase) >= 0;
            Trace.WriteLine(
                $"ND-DIRECT folder-create failed endpoint='/v1/Folder' parent='{parentContainerId}' name='{requestedName}' status={status} message='{SanitizeForTrace(ex.Message)}'.");
            return new FolderCreateResult(false, null, requestedName, duplicateNameConflict);
        }
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        return path
            .Replace("\\", "/", StringComparison.Ordinal)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string GetRelativeFolderPath(string relativeFilePath)
    {
        if (string.IsNullOrWhiteSpace(relativeFilePath))
        {
            return string.Empty;
        }

        var normalized = relativeFilePath.Replace("\\", "/", StringComparison.Ordinal);
        var index = normalized.LastIndexOf('/');
        if (index <= 0)
        {
            return string.Empty;
        }

        return normalized[..index];
    }

    private static bool TryEnumerateFolderItems(JsonElement root, out List<JsonElement> items, out string listNode)
    {
        items = new List<JsonElement>();
        listNode = string.Empty;

        if (root.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(root.EnumerateArray());
            listNode = "$";
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryFindArrayNode(root, "$", 0, out var arrayNode, out listNode))
        {
            items.AddRange(arrayNode.EnumerateArray());
            return true;
        }

        return false;
    }

    private static bool TryFindArrayNode(
        JsonElement node,
        string nodePath,
        int depth,
        out JsonElement arrayNode,
        out string arrayNodePath)
    {
        static string BuildPath(string basePath, string child)
        {
            return basePath == "$" ? $"$.{child}" : $"{basePath}.{child}";
        }

        if (depth > 4 || node.ValueKind != JsonValueKind.Object)
        {
            arrayNode = default;
            arrayNodePath = string.Empty;
            return false;
        }

        var preferredKeys = new[]
        {
            "standardList", "data", "items", "results", "value", "children", "containers",
            "folders", "folderList", "subContainers", "subcontainers"
        };

        foreach (var key in preferredKeys)
        {
            if (!TryGetPropertyIgnoreCase(node, key, out var value))
            {
                continue;
            }

            var childPath = BuildPath(nodePath, key);
            if (value.ValueKind == JsonValueKind.Array && IsLikelyContainerArray(value))
            {
                arrayNode = value;
                arrayNodePath = childPath;
                return true;
            }

            if (value.ValueKind == JsonValueKind.Object &&
                TryFindArrayNode(value, childPath, depth + 1, out arrayNode, out arrayNodePath))
            {
                return true;
            }
        }

        foreach (var property in node.EnumerateObject())
        {
            var childPath = BuildPath(nodePath, property.Name);
            if (property.Value.ValueKind == JsonValueKind.Array && IsLikelyContainerArray(property.Value))
            {
                arrayNode = property.Value;
                arrayNodePath = childPath;
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryFindArrayNode(property.Value, childPath, depth + 1, out arrayNode, out arrayNodePath))
            {
                return true;
            }
        }

        arrayNode = default;
        arrayNodePath = string.Empty;
        return false;
    }

    private static bool IsLikelyContainerArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var hasAny = false;
        foreach (var element in value.EnumerateArray())
        {
            hasAny = true;
            if (element.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        return !hasAny;
    }

    private static string GetTopLevelKeys(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root.ValueKind.ToString();
        }

        var keys = root.EnumerateObject().Select(p => p.Name).Take(16);
        return string.Join(",", keys);
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }

        return string.Empty;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string EncodeContainerIdForPath(string containerId)
    {
        var trimmed = containerId.Trim();
        if (trimmed.StartsWith(":", StringComparison.Ordinal))
        {
            var segments = trimmed.Split(':');
            var encodedSegments = new List<string>(segments.Length);
            foreach (var segment in segments)
            {
                if (segment.Length == 0)
                {
                    encodedSegments.Add(string.Empty);
                    continue;
                }

                var decoded = Uri.UnescapeDataString(segment);
                encodedSegments.Add(Uri.EscapeDataString(decoded));
            }

            return string.Join(":", encodedSegments);
        }

        return Uri.EscapeDataString(trimmed);
    }

    private static int? TryExtractStatusCode(Exception ex)
    {
        if (ex is null)
        {
            return null;
        }

        var text = ex.Message ?? string.Empty;
        var match = StatusCodeRegex.Match(text);
        if (match.Success &&
            int.TryParse(match.Groups["status"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string NormalizeCreatedFolderName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var first = trimmed[0];
        if (!char.IsLetter(first))
        {
            return trimmed;
        }

        return char.ToUpperInvariant(first) + trimmed[1..];
    }

    private static string? TryExtractCreatedContainerId(JsonElement root)
    {
        var direct = ReadString(root, "id", "containerId", "envId", "nev", "folderId");
        if (IsLikelyContainerId(direct))
        {
            return direct;
        }

        if (TryFindContainerIdRecursive(root, preferFolderId: true, out var preferred))
        {
            return preferred;
        }

        if (TryFindContainerIdRecursive(root, preferFolderId: false, out var fallback))
        {
            return fallback;
        }

        return null;
    }

    private static bool TryFindContainerIdRecursive(JsonElement node, bool preferFolderId, out string containerId)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var candidate = ReadString(node, "id", "containerId", "envId", "nev", "folderId");
            if (IsLikelyContainerId(candidate))
            {
                if (!preferFolderId || IsLikelyFolderId(candidate))
                {
                    containerId = candidate;
                    return true;
                }
            }

            foreach (var property in node.EnumerateObject())
            {
                if (TryFindContainerIdRecursive(property.Value, preferFolderId, out containerId))
                {
                    return true;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (TryFindContainerIdRecursive(item, preferFolderId, out containerId))
                {
                    return true;
                }
            }
        }

        containerId = string.Empty;
        return false;
    }

    private static bool IsLikelyFolderId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.IndexOf("^F", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("^C", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("/f/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf(":f:", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsLikelyContainerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        if (value.StartsWith(":", StringComparison.Ordinal) || value.Contains("^", StringComparison.Ordinal))
        {
            return true;
        }

        if (LegacyWorkspaceIdRegex.IsMatch(value))
        {
            return true;
        }

        if (Guid.TryParse(value, out _))
        {
            return true;
        }

        return value.Count(c => c == '-') >= 2 && value.Length >= 10;
    }

    private static string NormalizeFolderName(string value)
    {
        return value.Trim();
    }

    private async Task<WorkspaceFolderHydrationResult> TryHydrateWorkspaceFolderAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            var encoded = EncodeContainerIdForPath(containerId);
            using var document = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/info", cancellationToken);
            var root = document.RootElement;
            var name = ReadString(root, "name", "displayName", "title", "description");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = TryFindStringRecursive(root, "name", "displayName", "title", "description") ?? string.Empty;
            }

            var extension = ReadString(root, "extension", "ext", "Ext", "type", "objectType", "fileType");
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = TryFindStringRecursive(root, "extension", "ext", "Ext", "type", "objectType", "fileType") ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(extension) && IsLikelyFolderId(containerId))
            {
                extension = "ndfld";
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return new WorkspaceFolderHydrationResult(false, string.Empty, string.Empty, "missing-name");
            }

            return new WorkspaceFolderHydrationResult(true, name, extension, string.Empty);
        }
        catch (Exception ex)
        {
            return new WorkspaceFolderHydrationResult(false, string.Empty, string.Empty, SanitizeForTrace(ex.Message));
        }
    }

    private static bool ShouldHydrateFolderListEntry(string id, string type)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var normalizedType = type.Trim().ToLowerInvariant();
        if (normalizedType is "fil" or "fld" or "folder" or "ndfld" || string.IsNullOrWhiteSpace(normalizedType))
        {
            return true;
        }

        return IsLikelyFolderId(id);
    }

    private static string? TryFindStringRecursive(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (TryGetPropertyIgnoreCase(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = TryFindStringRecursive(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = TryFindStringRecursive(item, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool IsFolderItem(JsonElement item, string id, string extensionOrType)
    {
        var normalized = extensionOrType.Trim().ToLowerInvariant();
        if (normalized is "ndfld" or "fld" or "folder")
        {
            return true;
        }

        if (TryGetPropertyIgnoreCase(item, "isFolder", out var isFolder) &&
            isFolder.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        // Envelope ids for folders/collabspaces usually include '^F...' or '^C...'.
        if (id.IndexOf("^F", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("^C", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private static string SanitizeForTrace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Length > 240 ? text[..240] : text;
    }

    private static bool TryExtractLegacyWorkspaceId(JsonElement root, out string workspaceId)
    {
        static string? FindLegacy(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString() ?? string.Empty;
                        if (LegacyWorkspaceIdRegex.IsMatch(value))
                        {
                            return value;
                        }
                    }

                    var nested = FindLegacy(property.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindLegacy(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            return null;
        }

        var found = FindLegacy(root);
        workspaceId = found ?? string.Empty;
        return !string.IsNullOrWhiteSpace(found);
    }
}
