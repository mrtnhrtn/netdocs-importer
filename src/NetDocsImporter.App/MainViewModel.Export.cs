using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using NetDocsImporter.Core;
using NetDocsImporter.Data;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.App;

public sealed partial class MainViewModel
{
    private readonly ObservableCollection<ExportPreflightIssueView> _exportPreflightIssues = new();
    private ExportPlan? _exportPlan;
    private string _exportPlanTargetKey = string.Empty;
    private string _exportPlanRepositoryId = string.Empty;
    private string _exportPlanCabinetId = string.Empty;
    private bool _isExportBusy;
    private string _exportStatus = "Export mode ready. Select and confirm a NetDocuments target, then refresh preflight.";
    private double _exportProgressPercent;
    private string _exportSummary = "No preflight has been run yet.";
    private string? _lastExportManifestPath;
    private CancellationTokenSource? _exportCancellation;

    public IReadOnlyList<ExportMetadataFormat> ExportMetadataFormatOptions { get; } = Enum.GetValues<ExportMetadataFormat>();

    public ObservableCollection<ExportPreflightIssueView> ExportPreflightIssues => _exportPreflightIssues;

    public bool IsExportBusy
    {
        get => _isExportBusy;
        private set
        {
            if (SetField(ref _isExportBusy, value))
            {
                OnPropertyChanged(nameof(CanRefreshExportPreflight));
                OnPropertyChanged(nameof(CanRunExport));
                OnPropertyChanged(nameof(CanCancelExport));
            }
        }
    }

    public string ExportStatus
    {
        get => _exportStatus;
        private set => SetField(ref _exportStatus, value);
    }

    public double ExportProgressPercent
    {
        get => _exportProgressPercent;
        private set => SetField(ref _exportProgressPercent, value);
    }

    public string ExportProgressPercentDisplay => $"{ExportProgressPercent:0.##}%";

    public string ExportSummary
    {
        get => _exportSummary;
        private set => SetField(ref _exportSummary, value);
    }

    public bool CanRefreshExportPreflight =>
        IsExportMode &&
        !IsExportBusy &&
        IsNetDocumentsConnected &&
        CanConfirmNetDocumentsTarget;

    public bool CanRunExport =>
        IsExportMode &&
        !IsExportBusy &&
        _exportPlan is not null &&
        IsExportPlanAlignedWithCurrentContext() &&
        _exportPlan.Items.Count > 0 &&
        !string.IsNullOrWhiteSpace(ExportDestinationRootPath);

    public bool CanCancelExport =>
        IsExportBusy &&
        _exportCancellation is not null &&
        !_exportCancellation.IsCancellationRequested;

    public bool CanOpenLastExportManifest =>
        !string.IsNullOrWhiteSpace(_lastExportManifestPath) &&
        File.Exists(_lastExportManifestPath);

    public async Task RefreshExportPreflightAsync()
    {
        if (!IsExportMode)
        {
            return;
        }

        if (!CanConfirmNetDocumentsTarget || _selectedNetDocumentsTarget is null)
        {
            ExportStatus = "Select and confirm a NetDocuments target before running export preflight.";
            SetExportPlan(null);
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId))
        {
            ExportStatus = "Select a cabinet before running export preflight.";
            SetExportPlan(null);
            return;
        }

        try
        {
            IsExportBusy = true;
            ExportProgressPercent = 0;
            OnPropertyChanged(nameof(ExportProgressPercentDisplay));
            ExportStatus = "Building export preflight plan...";

            var sync = RequireSyncService();
            var resolver = new ExportPathResolver();

            var issues = new List<ExportPreflightIssueView>();
            var folderCount = 0;
            var filterCount = 0;
            var savedSearchCount = 0;
            var collabspaceCount = 0;
            var rootSelection = CloneSelection(_selectedNetDocumentsTarget);
            var scopes = await sync.EnumerateExportScopesAsync(
                SelectedNetDocumentsCabinetId,
                rootSelection,
                includeWorkspaceFilters: ExportDownloadFiltersAsFolders);

            foreach (var scope in scopes)
            {
                switch (scope.Kind)
                {
                    case NdExportScopeKind.WorkspaceFilter:
                        filterCount++;
                        break;
                    case NdExportScopeKind.SavedSearch:
                        savedSearchCount++;
                        break;
                    case NdExportScopeKind.Collabspace:
                        collabspaceCount++;
                        break;
                    case NdExportScopeKind.Folder:
                        folderCount++;
                        break;
                }
            }

            List<string>? customAttributeIds = null;
            if (ExportIncludeCustomAttributes)
            {
                var syncedAttributes = await sync.GetSyncedAttributesAsync(SelectedNetDocumentsCabinetId);
                customAttributeIds = ResolveExportCustomAttributeIds(syncedAttributes);
            }

            var exportItems = new List<ExportItem>();
            var usedRelativePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var uniqueDocumentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var scope in scopes)
            {
                IReadOnlyList<NdExportDocument> documents;
                try
                {
                    documents = await sync.EnumerateContainerDocumentsAsync(
                        SelectedNetDocumentsCabinetId,
                        scope,
                        customAttributeIds);
                }
                catch (Exception ex)
                {
                    issues.Add(new ExportPreflightIssueView(
                        "Warning",
                        "DOCUMENT_ENUMERATION_FAILED",
                        ex.Message,
                        scope.Name));
                    continue;
                }

                foreach (var document in documents)
                {
                    uniqueDocumentIds.Add(document.DocumentId);

                    var baseMetadata = new List<ExportMetadataField>();
                    baseMetadata.AddRange(document.StandardAttributes.Select(a => new ExportMetadataField
                    {
                        Name = a.Name,
                        Value = a.Value
                    }));
                    baseMetadata.AddRange(document.CustomAttributes.Select(a => new ExportMetadataField
                    {
                        Name = a.Name,
                        Value = a.Value
                    }));
                    var dedupedBaseMetadata = baseMetadata
                        .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.Last())
                        .ToList();

                    if (ExportAllVersions)
                    {
                        IReadOnlyList<NdExportDocumentVersion> versions;
                        if (document.VersionHints.Count > 0)
                        {
                            versions = document.VersionHints;
                        }
                        else
                        {
                            try
                            {
                                versions = await sync.EnumerateDocumentVersionsAsync(document.DocumentId);
                            }
                            catch (Exception ex)
                            {
                                issues.Add(new ExportPreflightIssueView(
                                    "Warning",
                                    "VERSION_ENUMERATION_FAILED",
                                    ex.Message,
                                    document.DocumentId));
                                versions = Array.Empty<NdExportDocumentVersion>();
                            }
                        }

                        if (versions.Count == 0)
                        {
                            var fallback = BuildExportItem(
                                resolver,
                                scope,
                                document,
                                version: null,
                                dedupedBaseMetadata,
                                usedRelativePaths);
                            exportItems.Add(fallback);
                            continue;
                        }

                        foreach (var version in versions)
                        {
                            var item = BuildExportItem(
                                resolver,
                                scope,
                                document,
                                version,
                                dedupedBaseMetadata,
                                usedRelativePaths);
                            exportItems.Add(item);
                        }

                        continue;
                    }

                    var officialVersion = string.IsNullOrWhiteSpace(document.OfficialVersionId)
                        ? null
                        : new NdExportDocumentVersion
                        {
                            VersionId = document.OfficialVersionId,
                            FileName = document.FileName,
                            SizeBytes = document.SizeBytes
                        };

                    exportItems.Add(BuildExportItem(
                        resolver,
                        scope,
                        document,
                        officialVersion,
                        dedupedBaseMetadata,
                        usedRelativePaths));
                }
            }

            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(ExportDestinationRootPath))
            {
                warnings.Add("Destination folder is not selected.");
            }

            if (!ExportDownloadFiltersAsFolders)
            {
                warnings.Add("Workspace filters are excluded because 'Download filters as folders' is disabled.");
            }

            warnings.Add("Run export downloads binaries to disk and writes manifest/metadata artifacts.");

            var plan = new ExportPlan
            {
                Config = new ExportConfig
                {
                    SourceCabinetId = SelectedNetDocumentsCabinetId,
                    SourceTargetId = _selectedNetDocumentsTarget.Id,
                    SourceTargetType = _selectedNetDocumentsTarget.Type.ToString(),
                    DestinationRootPath = ExportDestinationRootPath,
                    AllVersions = ExportAllVersions,
                    MetadataFormat = ExportMetadataFormat,
                    Concurrency = Math.Clamp(MaxConcurrency, 1, 8)
                },
                Items = exportItems,
                DocumentCount = uniqueDocumentIds.Count,
                VersionCount = exportItems.Count,
                EstimatedBytes = exportItems.Sum(item => item.SizeBytes ?? 0),
                Warnings = warnings
            };

            SetExportPlan(plan);
            UpdateOnUi(() =>
            {
                _exportPreflightIssues.Clear();
                foreach (var issue in issues)
                {
                    _exportPreflightIssues.Add(issue);
                }
            });

            ExportSummary =
                $"Scope discovered: folders={folderCount:N0}, filters={filterCount:N0}, saved searches={savedSearchCount:N0}, collabspaces={collabspaceCount:N0}, containers={scopes.Count:N0}. " +
                $"Documents={plan.DocumentCount:N0}, versions={plan.VersionCount:N0}, estimated={FormatBytes(plan.EstimatedBytes)}.";
            ExportStatus = $"Export preflight ready. {plan.Warnings.Count} warning(s).";
            ExportProgressPercent = 100;
            OnPropertyChanged(nameof(ExportProgressPercentDisplay));
        }
        catch (Exception ex)
        {
            SetExportPlan(null);
            ExportStatus = $"Export preflight failed: {ex.Message}";
        }
        finally
        {
            IsExportBusy = false;
        }
    }

    public async Task RunExportAsync()
    {
        if (!CanRunExport || _exportPlan is null)
        {
            ExportStatus = "Run export is unavailable. Refresh preflight and select an export destination first.";
            return;
        }

        var runStartedUtc = DateTime.UtcNow;
        var runJobId = CurrentJobId ?? $"export-{runStartedUtc:yyyyMMddHHmmss}";
        var throttle = new ExportThrottleState();
        var maxAttemptsPerItem = 4;
        string? activeRunMarkerPath = null;

        try
        {
            IsExportBusy = true;
            _exportCancellation = new CancellationTokenSource();
            OnPropertyChanged(nameof(CanCancelExport));
            ExportProgressPercent = 0;
            OnPropertyChanged(nameof(ExportProgressPercentDisplay));
            ExportStatus = "Downloading export content...";

            Directory.CreateDirectory(ExportDestinationRootPath);
            var sync = RequireSyncService();
            var writer = new ExportOutputWriter();

            activeRunMarkerPath = await _completedJobLogStore.WriteActiveRunAsync(new DirectUploadActiveRunMarker
            {
                JobId = runJobId,
                StartedUtc = runStartedUtc,
                RunType = "Export",
                TargetDisplay = SelectedNetDocumentsTargetName ?? string.Empty,
                TotalRequestedFiles = _exportPlan.Items.Count,
                PlannedFiles = _exportPlan.Items.Count,
                SkippedFiles = 0,
                PlannedFolderCreates = 0
            }, _exportCancellation.Token);

            var manifestPath = await writer.WriteManifestAsync(
                ExportDestinationRootPath,
                _exportPlan.Items,
                _exportCancellation.Token);

            var metadataItems = new MetadataDumpItem[_exportPlan.Items.Count];
            var runLogLines = new List<string>();
            var succeeded = 0;
            var failed = 0;
            var completed = 0;
            var runLock = new object();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(_exportPlan.Config.Concurrency, 1, 8),
                CancellationToken = _exportCancellation.Token
            };

            await Parallel.ForEachAsync(Enumerable.Range(0, _exportPlan.Items.Count), options, async (index, cancellationToken) =>
            {
                var item = _exportPlan.Items[index];
                var destinationPath = Path.Combine(ExportDestinationRootPath, item.LocalPath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                var tempPath = destinationPath + ".part";
                NdBinaryDownloadResponse? finalResponse = null;
                for (var attempt = 1; attempt <= maxAttemptsPerItem; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await throttle.WaitAsync(cancellationToken);

                    finalResponse = await sync.DownloadDocumentBinaryForExportRunAsync(
                        item.DocumentId,
                        tempPath,
                        item.VersionId,
                        cancellationToken);

                    if (finalResponse.Succeeded)
                    {
                        break;
                    }

                    if (!IsRetriableStatus(finalResponse.StatusCode) || attempt == maxAttemptsPerItem)
                    {
                        break;
                    }

                    var delay = finalResponse.StatusCode == 429
                        ? finalResponse.RetryAfter ?? ComputeExponentialBackoff(attempt)
                        : ComputeExponentialBackoff(attempt);
                    throttle.PushDelay(delay);
                }

                var metadataFields = item.MetadataFields
                    .Select(field => new ExportMetadataField
                    {
                        Name = field.Name,
                        Value = field.Value
                    })
                    .ToList();

                if (finalResponse is not null && finalResponse.Succeeded)
                {
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }

                    File.Move(tempPath, destinationPath);
                    Interlocked.Increment(ref succeeded);
                    metadataItems[index] = new MetadataDumpItem
                    {
                        DocumentId = item.DocumentId,
                        VersionId = item.VersionId,
                        SourcePath = item.SourcePath,
                        LocalPath = item.LocalPath,
                        Status = "Succeeded",
                        Error = string.Empty,
                        MetadataFields = metadataFields
                    };
                }
                else
                {
                    DeleteIfExists(tempPath);
                    Interlocked.Increment(ref failed);
                    metadataItems[index] = new MetadataDumpItem
                    {
                        DocumentId = item.DocumentId,
                        VersionId = item.VersionId,
                        SourcePath = item.SourcePath,
                        LocalPath = item.LocalPath,
                        Status = "Failed",
                        Error = finalResponse?.ErrorMessage ?? "Unknown download error.",
                        MetadataFields = metadataFields
                    };
                }

                var completedNow = Interlocked.Increment(ref completed);
                var percent = Math.Round((double)completedNow / Math.Max(1, _exportPlan.Items.Count) * 100d, 2);
                UpdateOnUi(() =>
                {
                    ExportProgressPercent = percent;
                    OnPropertyChanged(nameof(ExportProgressPercentDisplay));
                    ExportStatus =
                        $"Export downloading {completedNow:N0}/{_exportPlan.Items.Count:N0} ({percent:0.##}%). " +
                        $"Succeeded={Volatile.Read(ref succeeded):N0}, failed={Volatile.Read(ref failed):N0}.";
                });

                lock (runLock)
                {
                    var status = finalResponse is { Succeeded: true } ? "OK" : "FAIL";
                    var statusCode = finalResponse?.StatusCode ?? 0;
                    var bytes = finalResponse?.BytesWritten ?? 0;
                    var error = finalResponse?.ErrorMessage ?? string.Empty;
                    runLogLines.Add(
                        $"[{status}] doc={item.DocumentId} ver={item.VersionId ?? "official"} status={statusCode} bytes={bytes} local='{item.LocalPath}' error='{error}'");
                }
            });

            var metadataPath = await writer.WriteMetadataAsync(
                ExportDestinationRootPath,
                new MetadataDump
                {
                    SourceCabinetId = _exportPlan.Config.SourceCabinetId,
                    SourceTargetId = _exportPlan.Config.SourceTargetId,
                    GeneratedUtc = DateTime.UtcNow,
                    Items = metadataItems.ToList()
                },
                ExportMetadataFormat,
                _exportCancellation.Token);

            var runLogPath = await WriteExportRunLogAsync(runJobId, runStartedUtc, runLogLines, succeeded, failed, _exportPlan.Items.Count, manifestPath, metadataPath);
            await _completedJobLogStore.WriteSummaryAsync(new CompletedJobRunSummary
            {
                JobId = runJobId,
                StartedUtc = runStartedUtc,
                RunType = "Export",
                Status = failed > 0 ? "Export Partial" : "Export",
                Summary = $"Exported {succeeded:N0}/{_exportPlan.Items.Count:N0} files. Failed={failed:N0}. Metadata={Path.GetFileName(metadataPath)}",
                RequestedFiles = _exportPlan.Items.Count,
                PlannedFiles = _exportPlan.Items.Count,
                UploadedFiles = succeeded,
                FailedFiles = failed,
                SkippedFiles = 0,
                ResumedFiles = 0,
                CreatedFolders = 0,
                ReportFileName = Path.GetFileName(metadataPath),
                RunLogFileName = Path.GetFileName(runLogPath)
            }, _exportCancellation.Token);

            if (!string.IsNullOrWhiteSpace(activeRunMarkerPath))
            {
                await _completedJobLogStore.DeleteActiveRunAsync(activeRunMarkerPath);
                activeRunMarkerPath = null;
            }

            _lastExportManifestPath = manifestPath;
            OnPropertyChanged(nameof(CanOpenLastExportManifest));
            ExportProgressPercent = 100;
            OnPropertyChanged(nameof(ExportProgressPercentDisplay));
            ExportStatus =
                $"Export complete. Succeeded {succeeded:N0}/{_exportPlan.Items.Count:N0}, failed {failed:N0}. " +
                $"Manifest: {Path.GetFileName(manifestPath)}, metadata: {Path.GetFileName(metadataPath)}.";
        }
        catch (OperationCanceledException) when (_exportCancellation is not null && _exportCancellation.IsCancellationRequested)
        {
            ExportStatus = "Export canceled by user.";
            if (!string.IsNullOrWhiteSpace(activeRunMarkerPath))
            {
                await _completedJobLogStore.DeleteActiveRunAsync(activeRunMarkerPath);
                activeRunMarkerPath = null;
            }
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export run failed: {ex.Message}";
        }
        finally
        {
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            OnPropertyChanged(nameof(CanCancelExport));
            IsExportBusy = false;
        }
    }

    public void CancelExport()
    {
        if (!CanCancelExport)
        {
            return;
        }

        _exportCancellation?.Cancel();
        ExportStatus = "Cancel requested. Waiting for export workers to stop...";
        OnPropertyChanged(nameof(CanCancelExport));
    }

    public void OpenLastExportManifest()
    {
        if (string.IsNullOrWhiteSpace(_lastExportManifestPath) || !File.Exists(_lastExportManifestPath))
        {
            ExportStatus = "No export manifest is available to open.";
            return;
        }

        OpenFile(_lastExportManifestPath);
    }

    private void SetExportPlan(ExportPlan? plan)
    {
        _exportPlan = plan;
        if (plan is null || _selectedNetDocumentsTarget is null)
        {
            _exportPlanTargetKey = string.Empty;
            _exportPlanRepositoryId = string.Empty;
            _exportPlanCabinetId = string.Empty;
        }
        else
        {
            _exportPlanTargetKey = NdTargetBrowserLogic.BuildTargetKey(_selectedNetDocumentsTarget);
            _exportPlanRepositoryId = SelectedNetDocumentsRepositoryId ?? string.Empty;
            _exportPlanCabinetId = SelectedNetDocumentsCabinetId ?? string.Empty;
        }

        OnPropertyChanged(nameof(CanRunExport));
    }

    private bool IsExportPlanAlignedWithCurrentContext()
    {
        if (_exportPlan is null || _selectedNetDocumentsTarget is null)
        {
            return false;
        }

        var currentTargetKey = NdTargetBrowserLogic.BuildTargetKey(_selectedNetDocumentsTarget);
        return string.Equals(_exportPlanTargetKey, currentTargetKey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_exportPlanRepositoryId, SelectedNetDocumentsRepositoryId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_exportPlanCabinetId, SelectedNetDocumentsCabinetId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private void HandleExportContextChanged(string reason, bool refreshPreflight)
    {
        SetExportPlan(null);
        UpdateOnUi(() => _exportPreflightIssues.Clear());
        ExportSummary = "No preflight has been run yet.";
        ExportStatus = reason;

        if (!refreshPreflight || !IsExportMode || IsExportBusy || _selectedNetDocumentsTarget is null || !_selectedNetDocumentsTargetSupported)
        {
            return;
        }

        _ = RefreshExportPreflightAsync();
    }

    private static ExportItem BuildExportItem(
        ExportPathResolver resolver,
        NdExportScope scope,
        NdExportDocument document,
        NdExportDocumentVersion? version,
        IReadOnlyList<ExportMetadataField> baseMetadata,
        IDictionary<string, string> usedRelativePaths)
    {
        var versionId = version?.VersionId;
        var fileName = version?.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = document.FileName;
        }
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = document.DocumentId;
        }

        var stableId = $"{scope.ContainerId}:{document.DocumentId}:{versionId ?? "official"}";
        var relativePath = resolver.ResolveRelativePath(scope.PathSegments, fileName, stableId);
        if (usedRelativePaths.TryGetValue(relativePath, out var existingStableId) &&
            !string.Equals(existingStableId, stableId, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = resolver.ResolveCollision(relativePath, stableId);
        }
        usedRelativePaths[relativePath] = stableId;

        var sourcePath = string.IsNullOrWhiteSpace(versionId)
            ? $"{scope.ContainerId}/{document.DocumentId}"
            : $"{scope.ContainerId}/{document.DocumentId}/{versionId}";

        var metadata = baseMetadata
            .Select(field => new ExportMetadataField
            {
                Name = field.Name,
                Value = field.Value
            })
            .ToList();
        if (version is not null)
        {
            metadata.AddRange(version.Attributes.Select(attribute => new ExportMetadataField
            {
                Name = $"version.{attribute.Name}",
                Value = attribute.Value
            }));
        }

        return new ExportItem
        {
            DocumentId = document.DocumentId,
            VersionId = versionId,
            SourcePath = sourcePath,
            LocalPath = relativePath,
            SizeBytes = version?.SizeBytes ?? document.SizeBytes,
            MetadataFields = metadata
                .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList()
        };
    }

    private static List<string> ResolveExportCustomAttributeIds(
        IReadOnlyList<NetDocumentsAttributeRecord> syncedAttributes)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (syncedAttributes is null || syncedAttributes.Count == 0)
        {
            return ids.ToList();
        }

        foreach (var attribute in syncedAttributes)
        {
            var numericId = attribute.AttributeNum > 0
                ? attribute.AttributeNum.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(attribute.AttributeId))
            {
                ids.Add(attribute.AttributeId);
            }

            if (!string.IsNullOrWhiteSpace(numericId))
            {
                ids.Add(numericId);
            }
        }

        return ids.ToList();
    }

    private async Task<string> WriteExportRunLogAsync(
        string runJobId,
        DateTime runStartedUtc,
        IReadOnlyList<string> lines,
        int succeeded,
        int failed,
        int total,
        string manifestPath,
        string metadataPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine("|                  NetDocs Export Run Log                   |");
        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine($" Started: {runStartedUtc.ToLocalTime():g}");
        builder.AppendLine($" Job Id: {runJobId}");
        builder.AppendLine($" Target: {SelectedNetDocumentsTargetName}");
        builder.AppendLine($" Planned: {total:N0}");
        builder.AppendLine($" Succeeded: {succeeded:N0}");
        builder.AppendLine($" Failed: {failed:N0}");
        builder.AppendLine($" Manifest: {manifestPath}");
        builder.AppendLine($" Metadata: {metadataPath}");
        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine("| Item Outcomes                                              |");
        builder.AppendLine("+------------------------------------------------------------+");
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        return await _completedJobLogStore.WriteRunLogAsync(runJobId, runStartedUtc, builder.ToString());
    }

    private static bool IsRetriableStatus(int statusCode)
    {
        return statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    }

    private static TimeSpan ComputeExponentialBackoff(int attempt)
    {
        var baseMs = Math.Min(1000 * (1 << Math.Clamp(attempt - 1, 0, 5)), 10000);
        var jitterMs = Random.Shared.Next(0, 350);
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }

    private static void DeleteIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

internal sealed class ExportThrottleState
{
    private long _delayUntilUtcTicks;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delayUntilTicks = Interlocked.Read(ref _delayUntilUtcTicks);
            if (delayUntilTicks <= 0)
            {
                return;
            }

            var delayUntilUtc = new DateTime(delayUntilTicks, DateTimeKind.Utc);
            var remaining = delayUntilUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var bounded = remaining > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : remaining;
            await Task.Delay(bounded, cancellationToken);
        }
    }

    public void PushDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var proposedTicks = DateTime.UtcNow.Add(delay).Ticks;
        while (true)
        {
            var currentTicks = Interlocked.Read(ref _delayUntilUtcTicks);
            if (currentTicks >= proposedTicks)
            {
                return;
            }

            var original = Interlocked.CompareExchange(ref _delayUntilUtcTicks, proposedTicks, currentTicks);
            if (original == currentTicks)
            {
                return;
            }
        }
    }
}

public sealed class ExportPreflightIssueView
{
    public ExportPreflightIssueView(string severity, string code, string message, string scope)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Scope = scope;
    }

    public string Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public string Scope { get; }
}
