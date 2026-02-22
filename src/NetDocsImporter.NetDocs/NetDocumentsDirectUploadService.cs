using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
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
    private const int DefaultV1DocumentIndexPriority = 250;
    private static readonly Regex StatusCodeRegex = new(@"\((?<status>\d{3})\s", RegexOptions.Compiled);
    private static readonly Regex LegacyWorkspaceIdRegex = new(@"^\d{4}-\d{4}-\d{4}$", RegexOptions.Compiled);
    private const int DefaultMaxUploadConcurrency = 8;
    private const int MaxUploadConcurrency = 8;
    private const int DefaultMaxUploadAttempts = 4;
    private static readonly HashSet<string> BlockedUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe",
        "com",
        "bat",
        "js",
        "vbs",
        "pif",
        "cmd",
        "dll",
        "ocx",
        "pwl"
    };
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
        bool DuplicateNameConflict,
        int FailureStatusCode,
        string FailureReason);

    private readonly record struct WorkspaceFolderHydrationResult(
        bool Success,
        string Name,
        string ExtensionOrType,
        string FailureReason);

    private readonly record struct UploadWorkItem(
        UploadPlanFileEntry File,
        string TransferId,
        int Attempt);
    private readonly record struct UploadProfilePayload(
        string ProfileJson,
        string CustomAttributesJson,
        IReadOnlyList<int> NumericAttributeIds);

    private readonly record struct MultipartInitiatePayload(
        string UploadId);

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

        var effectiveTarget = target;
        if (IsSavedSearchTargetSelection(target))
        {
            var resolvedTarget = await TryResolveSavedSearchUploadTargetAsync(target, cancellationToken);
            if (resolvedTarget is null)
            {
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Error,
                    "SAVED_SEARCH_UPLOAD_SCOPE_UNRESOLVED",
                    "Saved Search targets are metadata-only and cannot be used directly for upload. Select a Folder, Workspace, or Workspace Filter target, or choose a Saved Search whose parent scope can be resolved."));
                return new UploadPlanResult
                {
                    Folders = Array.Empty<UploadPlanFolderEntry>(),
                    Files = Array.Empty<UploadPlanFileEntry>(),
                    Issues = issues,
                    TotalRequestedFiles = files.Count,
                    PlannedFiles = 0,
                    SkippedFiles = 0,
                    PlannedFolderCreates = 0,
                    CanUpload = false
                };
            }

            effectiveTarget = resolvedTarget;
            var resolvedTypeDisplay = NdTargetBrowserLogic.ResolveTypeDisplay(
                resolvedTarget.Type,
                resolvedTarget.Id,
                resolvedTarget.Extension);
            issues.Add(new DirectUploadIssue(
                DirectUploadIssueSeverity.Warning,
                "SAVED_SEARCH_SCOPE_INFERRED",
                $"Saved Search target '{target.Name}' is metadata-only for upload. Destination was resolved to '{resolvedTarget.Name}' ({resolvedTypeDisplay})."));
            Trace.WriteLine(
                $"ND-DIRECT saved-search target remapped sourceId='{target.Id}' sourceName='{target.Name}' destinationId='{resolvedTarget.Id}' destinationType={resolvedTarget.Type}.");
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
        _workspaceListSupported = null;
        _folderListSupported = null;
        _folderCreateSupported = null;

        var uniqueRelativeFolders = files
            .Select(f => GetRelativeFolderPath(f.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var createCount = 0;
        Trace.WriteLine(
            $"ND-DIRECT {(context.AllowCreateFolders ? "execution-plan" : "preflight")} start files={files.Count} uniqueFolders={uniqueRelativeFolders.Count} targetType={effectiveTarget.Type} targetId='{effectiveTarget.Id}' allowCreate={context.AllowCreateFolders}.");
        if (effectiveTarget.Type == NdTargetType.WorkspaceFilter)
        {
            issues.Add(new DirectUploadIssue(
                DirectUploadIssueSeverity.Warning,
                "FILTER_FLAT_UPLOAD",
                "Workspace Filter targets cannot contain child folders. Source folder hierarchy will not be created; files will upload directly to the selected target and inherit target/workspace profile values."));
        }

        foreach (var relativeFolderPath in uniqueRelativeFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationId = await ResolveDestinationContainerAsync(
                effectiveTarget.Id,
                effectiveTarget.Type,
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

            var extensionToken = extension.TrimStart('.');
            if (BlockedUploadExtensions.Contains(extensionToken))
            {
                skippedCount++;
                Trace.WriteLine(
                    $"ND-DIRECT preflight file blocked reason='blocked-extension' relativePath='{file.RelativePath}' extension='{extensionToken}'.");
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Error,
                    "BLOCKED_FILE_EXTENSION",
                    $"File type '.{extensionToken}' is blocked by NetDocuments security policy and cannot be imported.",
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

            var forceMultipartForTesting =
                context.ForceMultipartUploadForTesting &&
                context.MultipartTestPayloadBytes > 0;
            var qualifiesForMultipart =
                forceMultipartForTesting ||
                sizeBytes >= context.MultipartThresholdBytes;
            var useMultipartUpload =
                context.EnableMultipartUpload &&
                qualifiesForMultipart &&
                sizeBytes <= context.MultipartMaxFileSizeBytes;

            if (forceMultipartForTesting && context.EnableMultipartUpload)
            {
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Info,
                    "MULTIPART_FORCED_DEV_TEST",
                    $"Developer multipart test mode is active; this file will use v2 multipart upload with a synthetic payload of {context.MultipartTestPayloadBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
                    file.RelativePath));
            }
            else if (sizeBytes >= context.MultipartThresholdBytes && context.EnableMultipartUpload)
            {
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Info,
                    "MULTIPART_REQUIRED",
                    "File size meets the multipart threshold and will use v2 multipart upload.",
                    file.RelativePath));
            }
            else if (sizeBytes >= context.MultipartThresholdBytes && !context.EnableMultipartUpload)
            {
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Error,
                    "MULTIPART_DISABLED_FOR_LARGE_FILE",
                    "File size meets the multipart threshold, but multipart upload is disabled.",
                    file.RelativePath));
            }

            if (sizeBytes > context.MultipartMaxFileSizeBytes)
            {
                issues.Add(new DirectUploadIssue(
                    DirectUploadIssueSeverity.Error,
                    "MULTIPART_MAX_SIZE_EXCEEDED",
                    $"File size exceeds the multipart maximum size ({context.MultipartMaxFileSizeBytes.ToString(CultureInfo.InvariantCulture)} bytes).",
                    file.RelativePath));
            }

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
        if (file.UseMultipartUpload)
        {
            return await TryUploadFileMultipartAsync(file, context, cancellationToken);
        }

        var v1Timeout = context.V1UploadRequestTimeout <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(30)
            : context.V1UploadRequestTimeout;
        return await TryUploadFileV1Async(file, context, v1Timeout, cancellationToken);
    }

    private async Task<DirectUploadFileResult> TryUploadFileV1Async(
        UploadPlanFileEntry file,
        DirectUploadPlanContext context,
        TimeSpan? requestTimeout,
        CancellationToken cancellationToken)
    {
        var v1DocumentPath = BuildV1DocumentUploadPath(context.V1DocumentIndexPriority);
        var v1DocumentEndpoint = BuildUploadEndpointPath(
            context,
            v1DocumentPath,
            "v1-document-upload");
        var candidateEndpoints = new[]
        {
            (Path: v1DocumentEndpoint, IncludeAction: true)
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

                multipart.Add(new StringContent("standardAttributes", Encoding.UTF8), "return");

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
                    var profilePayload = BuildUploadProfilePayload(file.ProfileValues);
                    // v1/Document profiling expects a "profile" form value and failOnError=true for strict validation.
                    multipart.Add(new StringContent(profilePayload.ProfileJson, Encoding.UTF8), "profile");
                    multipart.Add(new StringContent("true", Encoding.UTF8), "partialProfiling");
                    multipart.Add(new StringContent("true", Encoding.UTF8), "failOnError");
                    Trace.WriteLine(
                        $"ND-DIRECT upload request endpoint='{candidate.Path}' relativePath='{file.RelativePath}' profileKeys={file.ProfileValues.Count} customAttributeCount={profilePayload.NumericAttributeIds.Count} customAttributeIds='{string.Join(",", profilePayload.NumericAttributeIds)}' profileFallbackMap={profilePayload.NumericAttributeIds.Count == 0} partialProfiling=true failOnError=true.");
                }
                else
                {
                    Trace.WriteLine(
                        $"ND-DIRECT upload request endpoint='{candidate.Path}' relativePath='{file.RelativePath}' profileKeys=0 customAttributeCount=0 customAttributeIds='' profileFallbackMap=False partialProfiling=false failOnError=false.");
                }

                var streamContent = new StreamContent(stream);
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(streamContent, "file", Path.GetFileName(file.FullPath));

                if (!string.IsNullOrWhiteSpace(file.Acl))
                {
                    multipart.Add(new StringContent(file.Acl, Encoding.UTF8), "acl");
                }

                if (context.AddToRecents.HasValue)
                {
                    multipart.Add(
                        new StringContent(context.AddToRecents.Value ? "true" : "false", Encoding.UTF8),
                        "addToRecents");
                }

                var response = await _apiClient.PostForStringAsync(
                    candidate.Path,
                    multipart,
                    cancellationToken,
                    retryOnThrottle: false,
                    requestTimeout: requestTimeout);
                var documentId = TryExtractDocumentId(response);
                Trace.WriteLine(
                    $"ND-DIRECT upload success endpoint='{candidate.Path}' relativePath='{file.RelativePath}' destination='{file.DestinationContainerId}' documentId='{documentId}' addToRecents={context.AddToRecents?.ToString() ?? "default"}.");
                return new DirectUploadFileResult(
                    file.RelativePath,
                    true,
                    200,
                    "Uploaded",
                    string.IsNullOrWhiteSpace(documentId) ? null : documentId);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                var status = TryExtractHttpStatus(ex, cancellationToken);
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

    private async Task<DirectUploadFileResult> TryUploadFileMultipartAsync(
        UploadPlanFileEntry file,
        DirectUploadPlanContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var documentId = await CreateMultipartDocumentShellAsync(file, context, cancellationToken);
            if (string.IsNullOrWhiteSpace(documentId))
            {
                return new DirectUploadFileResult(
                    file.RelativePath,
                    false,
                    0,
                    "Multipart upload failed: unable to determine created document id.");
            }

            var uploadPayloadSizeBytes = ResolveMultipartPayloadSize(file, context);
            var initiateResult = await InitiateMultipartUploadAsync(file, documentId, uploadPayloadSizeBytes, context, cancellationToken);
            if (string.IsNullOrWhiteSpace(initiateResult.UploadId))
            {
                return new DirectUploadFileResult(
                    file.RelativePath,
                    false,
                    0,
                    "Multipart initiate failed: missing uploadId in response.");
            }

            var uploadId = initiateResult.UploadId;
            var partChecksums = new List<(int PartNum, string Checksum)>();
            using var fullChecksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunkSize = NormalizeMultipartChunkSize(context.MultipartChunkSizeBytes);
            var fileLength = uploadPayloadSizeBytes;
            using var stream = CreateMultipartPayloadStream(file, context);
            var buffer = new byte[chunkSize];
            var partNum = 1;

            if (context.ForceMultipartUploadForTesting && context.MultipartTestPayloadBytes > 0)
            {
                Trace.WriteLine(
                    $"ND-DIRECT multipart dev-test mode relativePath='{file.RelativePath}' sourcePath='{file.FullPath}' simulatedPayloadBytes={fileLength}.");
            }

            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken);
                if (bytesRead <= 0)
                {
                    break;
                }

                var partBytes = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, partBytes, 0, bytesRead);
                fullChecksum.AppendData(partBytes);

                var partChecksum = Convert.ToHexString(SHA256.HashData(partBytes)).ToLowerInvariant();
                var isLast = stream.Position >= fileLength;
                var partUpload = await UploadMultipartPartWithRetryAsync(
                    file,
                    uploadId,
                    partNum,
                    partBytes,
                    partChecksum,
                    isLast,
                    context,
                    cancellationToken);
                if (!partUpload.Succeeded)
                {
                    return partUpload;
                }

                partChecksums.Add((partNum, partChecksum));
                partNum++;
            }

            var fullChecksumValue = Convert.ToHexString(fullChecksum.GetHashAndReset()).ToLowerInvariant();
            var completeResult = await CompleteMultipartUploadAsync(
                file,
                uploadId,
                partChecksums,
                fullChecksumValue,
                context,
                cancellationToken);
            if (!completeResult.Succeeded)
            {
                return completeResult with
                {
                    DocumentId = documentId
                };
            }

            Trace.WriteLine(
                $"ND-DIRECT multipart upload success relativePath='{file.RelativePath}' uploadId='{uploadId}' parts={partChecksums.Count} documentId='{documentId}'.");
            return new DirectUploadFileResult(
                file.RelativePath,
                true,
                200,
                "Uploaded",
                documentId);
        }
        catch (Exception ex)
        {
            var status = TryExtractStatusCode(ex);
            return new DirectUploadFileResult(
                file.RelativePath,
                false,
                status ?? 0,
                $"Multipart upload failed: {ex.Message}");
        }
    }

    private async Task<string> CreateMultipartDocumentShellAsync(
        UploadPlanFileEntry file,
        DirectUploadPlanContext context,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FullPath).TrimStart('.');
        using var body = new MultipartFormDataContent
        {
            { new StringContent("create", Encoding.UTF8), "action" },
            { new StringContent(Path.GetFileNameWithoutExtension(file.FullPath), Encoding.UTF8), "name" }
        };

        if (!string.IsNullOrWhiteSpace(extension))
        {
            body.Add(new StringContent(extension, Encoding.UTF8), "extension");
        }

        if (!string.IsNullOrWhiteSpace(file.DestinationContainerId))
        {
            body.Add(new StringContent(file.DestinationContainerId, Encoding.UTF8), "destination");
        }
        else if (!string.IsNullOrWhiteSpace(context.CabinetId))
        {
            body.Add(new StringContent(context.CabinetId, Encoding.UTF8), "cabinet");
        }

        if (file.ProfileValues.Count > 0)
        {
            var profilePayload = BuildUploadProfilePayload(file.ProfileValues);
            body.Add(new StringContent(profilePayload.ProfileJson, Encoding.UTF8), "profile");
            body.Add(new StringContent("true", Encoding.UTF8), "partialProfiling");
            body.Add(new StringContent("true", Encoding.UTF8), "failOnError");
        }

        body.Add(new StringContent("standardAttributes", Encoding.UTF8), "return");
        if (context.AddToRecents.HasValue)
        {
            body.Add(
                new StringContent(context.AddToRecents.Value ? "true" : "false", Encoding.UTF8),
                "addToRecents");
        }

        try
        {
            Trace.WriteLine(
                $"ND-DIRECT multipart create start relativePath='{file.RelativePath}' destination='{file.DestinationContainerId}'.");
            var createPath = BuildUploadEndpointPath(context, "/v1/Document", "multipart-create");
            var response = await _apiClient.PostForStringAsync(
                createPath,
                body,
                cancellationToken,
                retryOnThrottle: false,
                requestTimeout: context.MultipartPartTimeout);
            var documentId = TryExtractDocumentId(response);
            Trace.WriteLine(
                $"ND-DIRECT multipart create success relativePath='{file.RelativePath}' documentId='{documentId}'.");
            return documentId;
        }
        catch (Exception ex)
        {
            var status = TryExtractStatusCode(ex) ?? 0;
            Trace.WriteLine(
                $"ND-DIRECT multipart create failed relativePath='{file.RelativePath}' status={status} message='{SanitizeForTrace(ex.Message)}'.");
            throw new InvalidOperationException($"Multipart create failed: {ex.Message}", ex);
        }
    }

    private async Task<MultipartInitiatePayload> InitiateMultipartUploadAsync(
        UploadPlanFileEntry file,
        string documentId,
        long totalSizeBytes,
        DirectUploadPlanContext context,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FullPath).TrimStart('.');
        var body = new MultipartFormDataContent
        {
            { new StringContent(totalSizeBytes.ToString(CultureInfo.InvariantCulture), Encoding.UTF8), "totalSize" },
            { new StringContent("upload", Encoding.UTF8), "action" },
            { new StringContent(extension, Encoding.UTF8), "extension" }
        };

        var initiatePath = $"/v2/document/{Uri.EscapeDataString(documentId)}/1/initiate";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Checksum-Algorithm"] = "SHA256"
        };

        try
        {
            var initiateEndpoint = BuildUploadEndpointPath(context, initiatePath, "multipart-initiate");
            Trace.WriteLine(
                $"ND-DIRECT multipart initiate start relativePath='{file.RelativePath}' path='{initiatePath}'.");
            var response = await _apiClient.PostForStringAsync(
                initiateEndpoint,
                body,
                cancellationToken,
                retryOnThrottle: false,
                requestHeaders: headers,
                requestTimeout: context.MultipartPartTimeout);
            var uploadId = TryExtractUploadId(response);
            Trace.WriteLine(
                $"ND-DIRECT multipart initiate success relativePath='{file.RelativePath}' path='{initiatePath}' uploadId='{uploadId}'.");
            return new MultipartInitiatePayload(uploadId);
        }
        catch (Exception ex)
        {
            var status = TryExtractStatusCode(ex) ?? 0;
            Trace.WriteLine(
                $"ND-DIRECT multipart initiate failed relativePath='{file.RelativePath}' path='{initiatePath}' status={status} message='{SanitizeForTrace(ex.Message)}'.");
            throw new InvalidOperationException($"Multipart initiate failed: {ex.Message}", ex);
        }
    }

    private async Task<DirectUploadFileResult> UploadMultipartPartWithRetryAsync(
        UploadPlanFileEntry file,
        string uploadId,
        int partNum,
        byte[] partBytes,
        string checksum,
        bool isLast,
        DirectUploadPlanContext context,
        CancellationToken cancellationToken)
    {
        var path = $"/v2/document/upload/{Uri.EscapeDataString(uploadId)}/part/{partNum}";
        var maxAttempts = Math.Max(1, context.MultipartPartMaxRetryAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var endpoint = BuildUploadEndpointPath(context, path, "multipart-part");
                using var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent(isLast ? "true" : "false", Encoding.UTF8), "isLast");
                var partContent = new ByteArrayContent(partBytes);
                partContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(partContent, "file", $"part-{partNum:D5}.bin");

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Checksum-Algorithm"] = "SHA256",
                    ["Content-Checksum"] = checksum
                };

                Trace.WriteLine(
                    $"ND-DIRECT multipart part start relativePath='{file.RelativePath}' uploadId='{uploadId}' part={partNum} attempt={attempt}/{maxAttempts} isLast={isLast}.");
                await _apiClient.PutForStringAsync(
                    endpoint,
                    multipart,
                    cancellationToken,
                    retryOnThrottle: false,
                    requestHeaders: headers,
                    requestTimeout: context.MultipartPartTimeout);
                Trace.WriteLine(
                    $"ND-DIRECT multipart part success relativePath='{file.RelativePath}' uploadId='{uploadId}' part={partNum} attempt={attempt}/{maxAttempts}.");
                return new DirectUploadFileResult(file.RelativePath, true, 200, "Uploaded");
            }
            catch (Exception ex)
            {
                var status = TryExtractStatusCode(ex) ?? 0;
                var transient = IsTransientUploadStatus(status);
                Trace.WriteLine(
                    $"ND-DIRECT multipart part failed relativePath='{file.RelativePath}' uploadId='{uploadId}' part={partNum} attempt={attempt}/{maxAttempts} status={status} transient={transient} message='{SanitizeForTrace(ex.Message)}'.");
                if (!transient || attempt >= maxAttempts)
                {
                    return new DirectUploadFileResult(
                        file.RelativePath,
                        false,
                        status,
                        $"Multipart part {partNum} failed: {ex.Message}");
                }

                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                Trace.WriteLine(
                    $"ND-DIRECT multipart part retry relativePath='{file.RelativePath}' uploadId='{uploadId}' part={partNum} nextAttempt={attempt + 1}/{maxAttempts} delayMs={delay.TotalMilliseconds:F0}.");
                await Task.Delay(delay, cancellationToken);
            }
        }

        return new DirectUploadFileResult(
            file.RelativePath,
            false,
            0,
            $"Multipart part {partNum} failed.");
    }

    private async Task<DirectUploadFileResult> CompleteMultipartUploadAsync(
        UploadPlanFileEntry file,
        string uploadId,
        IReadOnlyList<(int PartNum, string Checksum)> partChecksums,
        string fullChecksum,
        DirectUploadPlanContext context,
        CancellationToken cancellationToken)
    {
        var path = $"/v2/document/complete/{Uri.EscapeDataString(uploadId)}";
        using var form = new MultipartFormDataContent();
        var payload = partChecksums
            .Select(p => new { partNum = p.PartNum, checksum = p.Checksum })
            .ToList();
        var payloadJson = JsonSerializer.Serialize(payload);
        using var payloadContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        form.Add(payloadContent, "partsChecksums");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Checksum"] = fullChecksum
        };

        try
        {
            var completeEndpoint = BuildUploadEndpointPath(context, path, "multipart-complete");
            Trace.WriteLine(
                $"ND-DIRECT multipart complete start relativePath='{file.RelativePath}' uploadId='{uploadId}' parts={partChecksums.Count}.");
            await _apiClient.PostForStringAsync(
                completeEndpoint,
                form,
                cancellationToken,
                retryOnThrottle: false,
                requestHeaders: headers,
                requestTimeout: context.MultipartPartTimeout);
            Trace.WriteLine(
                $"ND-DIRECT multipart complete success relativePath='{file.RelativePath}' uploadId='{uploadId}' parts={partChecksums.Count}.");
            return new DirectUploadFileResult(file.RelativePath, true, 200, "Uploaded");
        }
        catch (Exception ex)
        {
            var status = TryExtractStatusCode(ex) ?? 0;
            Trace.WriteLine(
                $"ND-DIRECT multipart complete failed relativePath='{file.RelativePath}' uploadId='{uploadId}' status={status} message='{SanitizeForTrace(ex.Message)}'.");
            return new DirectUploadFileResult(
                file.RelativePath,
                false,
                status,
                $"Multipart complete failed: {ex.Message}");
        }
    }

    private static int NormalizeMultipartChunkSize(long configuredChunkSize)
    {
        const int defaultChunkSize = 100 * 1024 * 1024;
        var normalized = configuredChunkSize <= 0 ? defaultChunkSize : configuredChunkSize;
        if (normalized > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)normalized;
    }

    private static long ResolveMultipartPayloadSize(UploadPlanFileEntry file, DirectUploadPlanContext context)
    {
        if (context.ForceMultipartUploadForTesting && context.MultipartTestPayloadBytes > 0)
        {
            return context.MultipartTestPayloadBytes;
        }

        return new FileInfo(file.FullPath).Length;
    }

    private static Stream CreateMultipartPayloadStream(UploadPlanFileEntry file, DirectUploadPlanContext context)
    {
        if (context.ForceMultipartUploadForTesting && context.MultipartTestPayloadBytes > 0)
        {
            var payloadLength = context.MultipartTestPayloadBytes;
            var payload = new byte[payloadLength];
            var seed = SHA256.HashData(Encoding.UTF8.GetBytes(file.RelativePath));
            for (var i = 0; i < payloadLength; i++)
            {
                payload[i] = seed[i % seed.Length];
            }

            return new MemoryStream(payload, writable: false);
        }

        return File.OpenRead(file.FullPath);
    }

    private static string TryExtractUploadId(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return string.Empty;
        }

        try
        {
            using var json = JsonDocument.Parse(responseContent);
            if (TryReadUploadIdFromJsonElement(json.RootElement, out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
            // Ignore parse failures and use text fallback below.
        }

        var trimmed = responseContent.Trim().Trim('"');
        return trimmed;
    }

    private static string TryExtractDocumentId(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return string.Empty;
        }

        try
        {
            using var json = JsonDocument.Parse(responseContent);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("standardAttributes", out var standardAttributes))
            {
                var standardId = ReadString(standardAttributes, "id", "Id");
                if (!string.IsNullOrWhiteSpace(standardId))
                {
                    return standardId;
                }
            }

            var rootId = ReadString(root, "id", "Id", "documentId", "DocumentId");
            if (!string.IsNullOrWhiteSpace(rootId))
            {
                return rootId;
            }

            if (root.ValueKind == JsonValueKind.String)
            {
                var stringValue = root.GetString();
                return string.IsNullOrWhiteSpace(stringValue) ? string.Empty : stringValue;
            }

            return string.Empty;
        }
        catch
        {
            // Ignore parse failures and use text fallback below.
        }

        return responseContent.Trim().Trim('"');
    }

    private static bool TryReadUploadIdFromJsonElement(JsonElement element, out string uploadId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "uploadId", "UploadId", "id", "Id" })
            {
                if (element.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    uploadId = value.GetString()!;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryReadUploadIdFromJsonElement(property.Value, out uploadId))
                {
                    return true;
                }
            }
        }

        uploadId = string.Empty;
        return false;
    }

    private static string BuildV1DocumentUploadPath(int? indexPriority)
    {
        var effectiveIndexPriority = (!indexPriority.HasValue || indexPriority.Value <= 0)
            ? DefaultV1DocumentIndexPriority
            : indexPriority.Value;
        return $"/v1/Document?indexpriority={effectiveIndexPriority.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildUploadEndpointPath(
        DirectUploadPlanContext context,
        string relativePathOrPathWithQuery,
        string operation)
    {
        if (TryBuildUploadBaseUri(context.ApiBaseUrl, out var uploadBaseUri, out var reason))
        {
            var endpoint = BuildUploadEndpointUrl(uploadBaseUri, relativePathOrPathWithQuery);
            Trace.WriteLine(
                $"ND-DIRECT upload host route operation='{operation}' apiBaseUrl='{context.ApiBaseUrl}' resolvedBase='{uploadBaseUri}' reason='{reason}' endpoint='{endpoint}'.");
            return endpoint;
        }

        Trace.WriteLine(
            $"ND-DIRECT upload host route operation='{operation}' apiBaseUrl='{context.ApiBaseUrl}' reason='{reason}' fallback='api-client-base' relativePath='{relativePathOrPathWithQuery}'.");
        return relativePathOrPathWithQuery;
    }

    private static bool TryBuildUploadBaseUri(
        string apiBaseUrl,
        out Uri uploadBaseUri,
        out string reason)
    {
        uploadBaseUri = default!;
        reason = "api-base-missing";
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var parsedApiBaseUri))
        {
            reason = "api-base-invalid";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsedApiBaseUri.Host))
        {
            reason = "api-host-missing";
            return false;
        }

        var builder = new UriBuilder(parsedApiBaseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = "/"
        };

        if (parsedApiBaseUri.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase))
        {
            builder.Host = "upload." + parsedApiBaseUri.Host["api.".Length..];
            uploadBaseUri = builder.Uri;
            reason = "derived-upload-host";
            return true;
        }

        uploadBaseUri = builder.Uri;
        reason = "fallback-api-host";
        return true;
    }

    private static string BuildUploadEndpointUrl(Uri uploadBaseUri, string relativePathOrPathWithQuery)
    {
        if (Uri.TryCreate(relativePathOrPathWithQuery, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (string.IsNullOrWhiteSpace(relativePathOrPathWithQuery))
        {
            return uploadBaseUri.ToString();
        }

        var normalized = relativePathOrPathWithQuery.StartsWith("/", StringComparison.Ordinal)
            ? relativePathOrPathWithQuery
            : "/" + relativePathOrPathWithQuery;
        return new Uri(uploadBaseUri, normalized).ToString();
    }

    private static UploadProfilePayload BuildUploadProfilePayload(IReadOnlyDictionary<string, string> profileValues)
    {
        var customAttributes = new List<Dictionary<string, object>>();
        var seenAttributeIds = new HashSet<int>();

        foreach (var pair in profileValues)
        {
            if (!int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attributeId))
            {
                continue;
            }

            if (!seenAttributeIds.Add(attributeId))
            {
                continue;
            }

            customAttributes.Add(new Dictionary<string, object>
            {
                ["id"] = attributeId,
                ["value"] = pair.Value
            });
        }

        if (customAttributes.Count == 0)
        {
            var fallbackJson = JsonSerializer.Serialize(profileValues);
            return new UploadProfilePayload(fallbackJson, fallbackJson, Array.Empty<int>());
        }

        var profilePayload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["customAttributes"] = customAttributes
        };
        var profileJson = JsonSerializer.Serialize(profilePayload);
        var customAttributesJson = JsonSerializer.Serialize(customAttributes);
        return new UploadProfilePayload(
            profileJson,
            customAttributesJson,
            customAttributes
                .Select(item => item.TryGetValue("id", out var idValue) && idValue is int id ? id : 0)
                .Where(id => id > 0)
                .ToList());
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
            if (string.IsNullOrWhiteSpace(profileDefault.RawValue))
            {
                continue;
            }

            AddResolvedProfileValue(
                values,
                resolved,
                profileDefault.AttributeName ?? profileDefault.AttributeId,
                profileDefault.RawValue);
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

                AddResolvedProfileValue(
                    values,
                    resolved,
                    entry.Field,
                    entry.Value ?? string.Empty);
            }
        }

        return values;
    }

    private static void AddResolvedProfileValue(
        IDictionary<string, string> values,
        NetDocumentsAttributeRecord? resolved,
        string? fallbackKey,
        string value)
    {
        if (resolved is not null)
        {
            if (!string.IsNullOrWhiteSpace(resolved.Name))
            {
                values[resolved.Name] = value;
            }

            if (!string.IsNullOrWhiteSpace(resolved.AttributeId))
            {
                values[resolved.AttributeId] = value;
            }

            values[resolved.AttributeNum.ToString(CultureInfo.InvariantCulture)] = value;
        }

        if (!string.IsNullOrWhiteSpace(fallbackKey))
        {
            values[fallbackKey] = value;
        }
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

    private static bool IsSavedSearchTargetSelection(NdTargetSelection target)
    {
        return target.Type == NdTargetType.WorkspaceFilter &&
               NdTargetBrowserLogic.IsSavedSearchTarget(target.Id, target.Extension);
    }

    private async Task<NdTargetSelection?> TryResolveSavedSearchUploadTargetAsync(
        NdTargetSelection savedSearchTarget,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        AddSavedSearchScopeCandidate(candidates, savedSearchTarget.ParentWorkspaceId);

        try
        {
            var encoded = EncodeContainerIdForPath(savedSearchTarget.Id);
            using var document = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/info", cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                TryGetPropertyIgnoreCase(root, "data", out var dataNode) &&
                dataNode.ValueKind == JsonValueKind.Object)
            {
                root = dataNode;
            }

            foreach (var key in new[]
                     {
                         "parentContainerId", "parentId", "workspaceId", "parentWorkspaceId", "workspace",
                         "scopeId", "locationId", "folderId"
                     })
            {
                AddSavedSearchScopeCandidate(candidates, ReadString(root, key));
            }

            if (TryGetPropertyIgnoreCase(root, "ancestors", out var ancestorsNode))
            {
                CollectSavedSearchScopeCandidatesFromJson(ancestorsNode, candidates);
            }

            CollectSavedSearchScopeCandidatesFromJson(root, candidates);
        }
        catch
        {
            // Continue with ancestry and identifier-based fallback.
        }

        try
        {
            var encoded = EncodeContainerIdForPath(savedSearchTarget.Id);
            using var ancestryDocument = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/ancestry", cancellationToken);
            var ancestryRoot = ancestryDocument.RootElement;
            if (ancestryRoot.ValueKind == JsonValueKind.Array)
            {
                var ancestryItems = ancestryRoot.EnumerateArray().ToList();
                for (var index = ancestryItems.Count - 1; index >= 0; index--)
                {
                    var item = ancestryItems[index];
                    AddSavedSearchScopeCandidate(candidates, ReadString(item, "id", "containerId", "envId", "environmentId", "workspaceId", "folderId"));
                }
            }
        }
        catch
        {
            // Continue with available candidates.
        }

        foreach (var candidateId in candidates)
        {
            if (string.Equals(candidateId, savedSearchTarget.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = await TryResolveUploadScopeCandidateAsync(candidateId, savedSearchTarget.SourceFlow, cancellationToken);
            if (resolved is null)
            {
                continue;
            }

            if (resolved.Type == NdTargetType.WorkspaceFilter &&
                NdTargetBrowserLogic.IsSavedSearchTarget(resolved.Id, resolved.Extension))
            {
                continue;
            }

            return resolved;
        }

        return null;
    }

    private async Task<NdTargetSelection?> TryResolveUploadScopeCandidateAsync(
        string candidateId,
        NdTargetSourceFlow sourceFlow,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            return null;
        }

        var effectiveId = candidateId.Trim();
        var effectiveName = effectiveId;
        var effectiveExtension = string.Empty;
        var resolvedType = InferUploadTargetTypeFromIdentifier(effectiveId);

        try
        {
            var encoded = EncodeContainerIdForPath(candidateId);
            using var document = await _apiClient.GetJsonAsync($"/v2/container/{encoded}/info", cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                TryGetPropertyIgnoreCase(root, "data", out var dataNode) &&
                dataNode.ValueKind == JsonValueKind.Object)
            {
                root = dataNode;
            }

            var resolvedId = ReadString(root, "id", "containerId", "envId", "environmentId", "workspaceId", "folderId");
            if (!string.IsNullOrWhiteSpace(resolvedId))
            {
                effectiveId = resolvedId;
            }

            effectiveExtension = ReadString(root, "extension", "ext", "Ext");
            var rawType = string.IsNullOrWhiteSpace(effectiveExtension)
                ? ReadString(root, "type", "containerType", "kind", "objectType", "fileType")
                : effectiveExtension;
            var hasWorkspaceIdHint = TryGetPropertyIgnoreCase(root, "workspaceId", out _);
            resolvedType = NdTargetBrowserLogic.NormalizeSupportedType(rawType, hasWorkspaceIdHint) ??
                           InferUploadTargetTypeFromIdentifier(effectiveId) ??
                           resolvedType;

            var resolvedName = ReadString(root, "name", "displayName", "title", "description");
            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                effectiveName = resolvedName;
            }
        }
        catch
        {
            // Keep identifier-based inference fallback.
        }

        if (resolvedType is null)
        {
            return null;
        }

        if (resolvedType == NdTargetType.WorkspaceFilter &&
            NdTargetBrowserLogic.IsSavedSearchTarget(effectiveId, effectiveExtension))
        {
            return null;
        }

        return new NdTargetSelection
        {
            Type = resolvedType.Value,
            Id = effectiveId,
            Name = string.IsNullOrWhiteSpace(effectiveName) ? effectiveId : effectiveName,
            ParentWorkspaceId = null,
            Extension = effectiveExtension,
            SourceFlow = sourceFlow
        };
    }

    private static NdTargetType? InferUploadTargetTypeFromIdentifier(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return null;
        }

        var candidate = containerId.Trim();
        if (NdTargetBrowserLogic.IsSavedSearchIdentifier(candidate))
        {
            return NdTargetType.WorkspaceFilter;
        }

        if (IsLikelyFolderId(candidate))
        {
            return NdTargetType.Folder;
        }

        if (candidate.Contains("^W", StringComparison.OrdinalIgnoreCase) ||
            LegacyWorkspaceIdRegex.IsMatch(candidate))
        {
            return NdTargetType.Workspace;
        }

        return null;
    }

    private static void AddSavedSearchScopeCandidate(ICollection<string> candidates, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        if (candidates.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private static void CollectSavedSearchScopeCandidatesFromJson(JsonElement node, ICollection<string> candidates, int depth = 0)
    {
        if (depth > 6)
        {
            return;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var key = property.Name;
                    if (key.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("containerId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("envId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("environmentId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("workspaceId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("parentWorkspaceId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("parentContainerId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("parentId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("folderId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("workspace", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("scopeId", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("locationId", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSavedSearchScopeCandidate(candidates, property.Value.GetString());
                    }
                }

                CollectSavedSearchScopeCandidatesFromJson(property.Value, candidates, depth + 1);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                CollectSavedSearchScopeCandidatesFromJson(item, candidates, depth + 1);
            }
        }
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

        if (targetType == NdTargetType.WorkspaceFilter)
        {
            folderPathCache[relativeFolderPath] = targetContainerId;
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
                cabinetId: cabinetId,
                folderChildrenCache: folderChildrenCache,
                issues: issues,
                cancellationToken: cancellationToken);
            var childContainerId = lookup.ContainerId;
            var created = false;
            var createFailureIssueAdded = false;

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
                var createParentContainerId = currentContainerId;
                if (isFirstSegment && targetType == NdTargetType.Workspace)
                {
                    createParentContainerId = await ResolveWorkspaceListIdAsync(currentContainerId, cancellationToken);
                }

                var createResult = await TryCreateChildFolderAsync(
                    parentContainerId: createParentContainerId,
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
                            cabinetId: cabinetId,
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
                        cabinetId: cabinetId,
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
                else if (createResult.FailureStatusCode > 0)
                {
                    createFailureIssueAdded = true;
                    if (createResult.FailureStatusCode == 403)
                    {
                        issues.Add(new DirectUploadIssue(
                            DirectUploadIssueSeverity.Error,
                            "FOLDER_CREATE_FORBIDDEN",
                            $"Cannot create folder '{currentPath}' because this account does not have rights to create subfolders under '{createParentContainerId}'.",
                            currentPath));
                    }
                    else
                    {
                        issues.Add(new DirectUploadIssue(
                            DirectUploadIssueSeverity.Error,
                            "FOLDER_CREATE_FAILED",
                            $"Folder creation failed for '{currentPath}' under '{createParentContainerId}' (HTTP {createResult.FailureStatusCode}).",
                            currentPath));
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(childContainerId))
            {
                if (createFailureIssueAdded)
                {
                    return null;
                }

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
        string cabinetId,
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

        var listResult = await LoadChildFoldersAsync(parentContainerId, isWorkspaceRoot, cabinetId, issues, cancellationToken);
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
        string cabinetId,
        ICollection<DirectUploadIssue> issues,
        CancellationToken cancellationToken)
    {
        var children = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!isWorkspaceRoot &&
            NdTargetBrowserLogic.IsCollabspaceIdentifier(parentContainerId) &&
            !string.IsNullOrWhiteSpace(cabinetId))
        {
            return await LoadCollabspaceChildFoldersAsync(cabinetId, parentContainerId, issues, cancellationToken);
        }

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

    private async Task<FolderListResult> LoadCollabspaceChildFoldersAsync(
        string cabinetId,
        string parentContainerId,
        ICollection<DirectUploadIssue> issues,
        CancellationToken cancellationToken)
    {
        var children = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var escapedCabinet = Uri.EscapeDataString(cabinetId);
        var filter = Uri.EscapeDataString("extension eq 'ndfld'");
        var candidateContainers = new List<string>();
        AddCandidateContainerId(candidateContainers, parentContainerId);
        AddCandidateContainerId(candidateContainers, TrimContainerIdVersionSuffix(parentContainerId));

        foreach (var containerCandidate in candidateContainers)
        {
            var escapedContainer = Uri.EscapeDataString(containerCandidate);
            var encodedContainerPath = EncodeContainerIdForPath(containerCandidate);
            var endpoints = new[]
            {
                $"/v2/container/{encodedContainerPath}/sub?recursive=false&max=200&listflags=FoldersOnly,ValidateWorkspaces",
                $"/v2/container/{encodedContainerPath}?top=200&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces",
                $"/v2/search/{escapedCabinet}?container={escapedContainer}&top=200&filter={filter}&filtertype=IncludeOnly&listflags=FoldersOnly,ValidateWorkspaces",
            };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    using var document = await _apiClient.GetJsonAsync(endpoint, cancellationToken);
                    var items = EnumerateSearchItems(document.RootElement);
                    foreach (var item in items)
                    {
                        var id = ReadString(item, "id", "containerId", "envId", "nev", "folderId");
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            id = TryFindStringRecursive(item, "id", "containerId", "envId", "nev", "folderId") ?? string.Empty;
                        }

                        var name = ReadString(item, "name", "displayName", "title", "description");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = TryFindStringRecursive(item, "name", "displayName", "title", "description") ?? string.Empty;
                        }

                        var extension = ReadString(item, "extension", "ext", "Ext", "type", "objectType", "fileType");
                        if (string.IsNullOrWhiteSpace(extension))
                        {
                            extension = TryFindStringRecursive(item, "extension", "ext", "Ext", "type", "objectType", "fileType") ?? string.Empty;
                        }

                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        if (!IsFolderItem(item, id, extension))
                        {
                            continue;
                        }

                        children[NormalizeFolderName(name)] = id;
                    }

                    Trace.WriteLine(
                        $"ND-DIRECT collabspace-list success endpoint='{endpoint}' parent='{parentContainerId}' count={children.Count}.");
                    return new FolderListResult(children, true, "search", "$.items");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"ND-DIRECT collabspace-list failed endpoint='{endpoint}' parent='{parentContainerId}' message='{SanitizeForTrace(ex.Message)}'.");
                }
            }
        }

        issues.Add(new DirectUploadIssue(
            DirectUploadIssueSeverity.Error,
            "FOLDER_LIST_FAILED",
            $"Unable to list collabspace children for '{parentContainerId}'.",
            parentContainerId));
        return new FolderListResult(children, false, string.Empty, string.Empty);
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
            return new FolderCreateResult(false, null, NormalizeCreatedFolderName(childName), false, 0, string.Empty);
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
            return new FolderCreateResult(true, createdId, requestedName, false, 0, string.Empty);
        }
        catch (Exception ex)
        {
            var status = TryExtractStatusCode(ex) ?? 0;
            _folderCreateSupported = status is not (404 or 405 or 415 or 501);
            var duplicateNameConflict = status == 400 &&
                                        ex.Message.IndexOf("already contains a folder with this name", StringComparison.OrdinalIgnoreCase) >= 0;
            Trace.WriteLine(
                $"ND-DIRECT folder-create failed endpoint='/v1/Folder' parent='{parentContainerId}' name='{requestedName}' status={status} message='{SanitizeForTrace(ex.Message)}'.");
            return new FolderCreateResult(false, null, requestedName, duplicateNameConflict, status, SanitizeForTrace(ex.Message));
        }
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        return path
            .Replace("\\", "/", StringComparison.Ordinal)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void AddCandidateContainerId(ICollection<string> values, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        if (!values.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(candidate);
        }
    }

    private static string? TrimContainerIdVersionSuffix(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return null;
        }

        var trimmed = containerId.Trim();
        var pipeIndex = trimmed.IndexOf('|');
        if (pipeIndex <= 0)
        {
            return null;
        }

        return trimmed[..pipeIndex];
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

    private static int? TryExtractHttpStatus(Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return 408;
        }

        return TryExtractStatusCode(ex);
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

    private static IEnumerable<JsonElement> EnumerateSearchItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[]
                     {
                         "items", "results", "data", "documents", "records", "value", "list",
                         "standardList", "customList", "locations", "rows", "searchResults", "hits"
                     })
            {
                if (!TryGetPropertyIgnoreCase(root, name, out var child))
                {
                    continue;
                }

                if (child.ValueKind == JsonValueKind.Array)
                {
                    return child.EnumerateArray();
                }

                if (child.ValueKind == JsonValueKind.Object)
                {
                    var nested = EnumerateSearchItems(child).ToList();
                    if (nested.Count > 0)
                    {
                        return nested;
                    }
                }
            }
        }

        return Array.Empty<JsonElement>();
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
