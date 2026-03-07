using System.Collections.ObjectModel;
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

    public bool HasExportPreflightWarnings => ExportPreflightWarnings.Count > 0;

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
        !_exportPlan.HasBlockingCoverageIssues &&
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
            UpdateExportPreflightProgress(2, "Preparing export preflight...");

            var sync = RequireSyncService();
            var resolver = new ExportPathResolver();

            var issues = new List<ExportPreflightIssueView>();
            var folderCount = 0;
            var filterCount = 0;
            var savedSearchCount = 0;
            var collabspaceCount = 0;
            var rootSelection = CloneSelection(_selectedNetDocumentsTarget);
            UpdateExportPreflightProgress(8, "Resolving export scope...");
            var scopeEnumeration = await sync.EnumerateExportScopesAsync(
                SelectedNetDocumentsCabinetId,
                rootSelection,
                includeWorkspaceFilters: ExportDownloadFiltersAsFolders);
            var scopes = scopeEnumeration.Scopes;
            UpdateExportPreflightProgress(
                20,
                $"Export scope resolved. {scopes.Count:N0} container(s) queued for planning.");

            foreach (var traversalIssue in scopeEnumeration.Issues)
            {
                issues.Add(new ExportPreflightIssueView(
                    "Error",
                    "SCOPE_ENUMERATION_FAILED",
                    traversalIssue.Message,
                    traversalIssue.ScopeName));
            }

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

            var plannedCandidates = new Dictionary<string, PlannedExportCandidate>(StringComparer.OrdinalIgnoreCase);
            var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniqueDocumentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateCandidateCount = 0;
            var totalScopes = Math.Max(1, scopes.Count);
            var processedScopes = 0;

            foreach (var scope in scopes)
            {
                processedScopes++;
                var scopeStartPercent = 20 + (((double)processedScopes - 1) / totalScopes * 55d);
                var scopeEndPercent = 20 + ((double)processedScopes / totalScopes * 55d);
                UpdateExportPreflightProgress(
                    scopeStartPercent,
                    $"Planning scope {processedScopes:N0}/{scopes.Count:N0}: {scope.Name}");

                var relativeDirectoryPath = resolver.ResolveRelativeDirectoryPath(scope.PathSegments);
                if (!string.IsNullOrWhiteSpace(relativeDirectoryPath))
                {
                    folderPaths.Add(relativeDirectoryPath);
                }

                IReadOnlyList<NdExportDocument> documents;
                try
                {
                    documents = await sync.EnumerateContainerDocumentsAsync(
                        SelectedNetDocumentsCabinetId,
                        scope,
                        customAttributeIds: null);
                }
                catch (Exception ex)
                {
                    issues.Add(new ExportPreflightIssueView(
                        "Warning",
                        "DOCUMENT_ENUMERATION_FAILED",
                        ex.Message,
                        scope.Name));
                    UpdateExportPreflightProgress(
                        scopeEndPercent,
                        $"Skipped scope {processedScopes:N0}/{scopes.Count:N0}: {scope.Name}");
                    continue;
                }

                var documentsProcessedInScope = 0;
                void ReportScopeDocumentProgress()
                {
                    documentsProcessedInScope++;
                    if (documentsProcessedInScope % 25 != 0 && documentsProcessedInScope != documents.Count)
                    {
                        return;
                    }

                    var scopePercent = scopeStartPercent +
                                       ((double)documentsProcessedInScope / Math.Max(1, documents.Count) *
                                        (scopeEndPercent - scopeStartPercent));
                    UpdateExportPreflightProgress(
                        scopePercent,
                        $"Planning scope {processedScopes:N0}/{scopes.Count:N0}: {scope.Name} " +
                        $"({documentsProcessedInScope:N0}/{documents.Count:N0} documents)");
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
                            AddOrUpdatePlannedCandidate(
                                plannedCandidates,
                                scope,
                                document,
                                version: null,
                                dedupedBaseMetadata,
                                ref duplicateCandidateCount);
                            ReportScopeDocumentProgress();
                            continue;
                        }

                        foreach (var version in versions)
                        {
                            AddOrUpdatePlannedCandidate(
                                plannedCandidates,
                                scope,
                                document,
                                version,
                                dedupedBaseMetadata,
                                ref duplicateCandidateCount);
                        }

                        ReportScopeDocumentProgress();
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

                    AddOrUpdatePlannedCandidate(
                        plannedCandidates,
                        scope,
                        document,
                        officialVersion,
                        dedupedBaseMetadata,
                        ref duplicateCandidateCount);
                    ReportScopeDocumentProgress();
                }

                if (documents.Count == 0)
                {
                    UpdateExportPreflightProgress(
                        scopeEndPercent,
                        $"Planning scope {processedScopes:N0}/{scopes.Count:N0}: {scope.Name} (no documents)");
                }
            }

            UpdateExportPreflightProgress(82, "Assembling export plan...");
            var exportItems = new List<ExportItem>(plannedCandidates.Count);
            var usedRelativePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assembledItems = 0;
            var totalCandidates = Math.Max(1, plannedCandidates.Count);
            foreach (var candidate in plannedCandidates.Values)
            {
                exportItems.Add(BuildExportItem(
                    resolver,
                    candidate.Scope,
                    candidate.Document,
                    candidate.Version,
                    candidate.BaseMetadata,
                    candidate.SourceReferences,
                    usedRelativePaths));

                assembledItems++;
                if (assembledItems % 200 == 0 || assembledItems == plannedCandidates.Count)
                {
                    var assemblePercent = 82 + ((double)assembledItems / totalCandidates * 13d);
                    UpdateExportPreflightProgress(
                        assemblePercent,
                        $"Assembling export plan... {assembledItems:N0}/{plannedCandidates.Count:N0} item(s)");
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

            if (ExportIncludeCustomAttributes)
            {
                warnings.Add("Custom attributes are deferred from preflight and will be added during a later export phase.");
            }

            if (duplicateCandidateCount > 0)
            {
                warnings.Add($"Suppressed {duplicateCandidateCount:N0} duplicate document hit(s) across overlapping scopes; canonical folder/workspace paths were kept.");
            }

            if (scopeEnumeration.IsPartial)
            {
                warnings.Add(
                    $"Export preflight is incomplete because {scopeEnumeration.Issues.Count:N0} child container enumeration failure(s) were encountered. Resolve the reported issues and refresh preflight before running export.");
            }

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
                FolderPaths = folderPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                DocumentCount = uniqueDocumentIds.Count,
                VersionCount = exportItems.Count,
                EstimatedBytes = exportItems.Sum(item => item.SizeBytes ?? 0),
                Warnings = warnings,
                HasBlockingCoverageIssues = scopeEnumeration.IsPartial
            };

            UpdateExportPreflightProgress(97, "Finalizing export preflight...");
            SetExportPlan(plan);
            UpdateOnUi(() =>
            {
                ExportPreflightWarnings.Clear();
                foreach (var warning in plan.Warnings)
                {
                    ExportPreflightWarnings.Add(new ExportPreflightWarningView(warning));
                }

                _exportPreflightIssues.Clear();
                foreach (var issue in issues)
                {
                    _exportPreflightIssues.Add(issue);
                }
            });

            ExportSummary =
                $"Scope discovered: folders={folderCount:N0}, filters={filterCount:N0}, saved searches={savedSearchCount:N0}, collabspaces={collabspaceCount:N0}, containers={scopes.Count:N0}. " +
                $"Documents={plan.DocumentCount:N0}, versions={plan.VersionCount:N0}, estimated={FormatBytes(plan.EstimatedBytes)}.";
            var finalStatus = plan.HasBlockingCoverageIssues
                ? $"Export preflight incomplete. {scopeEnumeration.Issues.Count} blocking coverage issue(s) found."
                : $"Export preflight ready. {plan.Warnings.Count} warning(s).";
            UpdateExportPreflightProgress(100, finalStatus);
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
            ExportStatus = _exportPlan is { HasBlockingCoverageIssues: true }
                ? "Run export is unavailable. Export preflight is incomplete; resolve reported coverage issues and refresh preflight."
                : "Run export is unavailable. Refresh preflight and select an export destination first.";
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
            var createdFolders = 0;

            foreach (var folderPath in _exportPlan.FolderPaths)
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    continue;
                }

                var absoluteFolderPath = Path.Combine(ExportDestinationRootPath, folderPath);
                if (!Directory.Exists(absoluteFolderPath))
                {
                    Directory.CreateDirectory(absoluteFolderPath);
                    createdFolders++;
                }
            }

            activeRunMarkerPath = await _completedJobLogStore.WriteActiveRunAsync(new DirectUploadActiveRunMarker
            {
                JobId = runJobId,
                StartedUtc = runStartedUtc,
                RunType = "Export",
                TargetDisplay = SelectedNetDocumentsTargetName ?? string.Empty,
                TotalRequestedFiles = _exportPlan.Items.Count,
                PlannedFiles = _exportPlan.Items.Count,
                SkippedFiles = 0,
                PlannedFolderCreates = _exportPlan.FolderPaths.Count
            }, _exportCancellation.Token);

            var artifactId = $"{runStartedUtc:yyyyMMddTHHmmssfff}-{runJobId}";

            var manifestPath = await writer.WriteManifestAsync(
                ExportDestinationRootPath,
                artifactId,
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
                        SourceReferences = CloneSourceReferences(item.SourceReferences),
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
                        SourceReferences = CloneSourceReferences(item.SourceReferences),
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
                artifactId,
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
                CreatedFolders = createdFolders,
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
        UpdateOnUi(() =>
        {
            ExportPreflightWarnings.Clear();
            _exportPreflightIssues.Clear();
        });
        ExportSummary = "No preflight has been run yet.";
        ExportStatus = reason;

        if (!refreshPreflight || !IsExportMode || IsExportBusy || _selectedNetDocumentsTarget is null || !_selectedNetDocumentsTargetSupported)
        {
            return;
        }

        _ = RefreshExportPreflightAsync();
    }

    private void UpdateExportPreflightProgress(double percent, string status)
    {
        ExportProgressPercent = Math.Clamp(percent, 0, 100);
        OnPropertyChanged(nameof(ExportProgressPercentDisplay));
        ExportStatus = status;
    }

    private static ExportItem BuildExportItem(
        ExportPathResolver resolver,
        NdExportScope scope,
        NdExportDocument document,
        NdExportDocumentVersion? version,
        IReadOnlyList<ExportMetadataField> baseMetadata,
        IReadOnlyList<PlannedSourceReference> plannedSourceReferences,
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

        var extension = ResolveExportFileExtension(version, baseMetadata);
        var stableId = $"{scope.ContainerId}:{document.DocumentId}:{versionId ?? "official"}";
        var relativePath = resolver.ResolveRelativePath(scope.PathSegments, fileName, stableId, extension);
        if (usedRelativePaths.TryGetValue(relativePath, out var existingStableId) &&
            !string.Equals(existingStableId, stableId, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = resolver.ResolveCollision(relativePath, stableId);
        }
        usedRelativePaths[relativePath] = stableId;

        var sourcePath = BuildSourcePath(scope, document.DocumentId, versionId);
        var canonicalReferenceMatched = false;
        var sourceReferences = new List<ExportSourceReference>();
        foreach (var reference in plannedSourceReferences)
        {
            var isCanonical = !canonicalReferenceMatched &&
                string.Equals(reference.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reference.ScopeKind, scope.Kind.ToString(), StringComparison.OrdinalIgnoreCase);
            if (isCanonical)
            {
                canonicalReferenceMatched = true;
            }

            sourceReferences.Add(new ExportSourceReference
            {
                SourcePath = reference.SourcePath,
                ScopeKind = reference.ScopeKind,
                Disposition = isCanonical ? "Exported" : "SkippedDuplicate",
                Reason = isCanonical
                    ? "Chosen as the canonical export surface for this document/version."
                    : $"Skipped because this document/version was already planned from a preferred {scope.Kind.ToString().ToLowerInvariant()} or folder/workspace surface."
            });
        }

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
            SourceReferences = sourceReferences,
            MetadataFields = metadata
                .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList()
        };
    }

    private static void AddOrUpdatePlannedCandidate(
        IDictionary<string, PlannedExportCandidate> candidates,
        NdExportScope scope,
        NdExportDocument document,
        NdExportDocumentVersion? version,
        IReadOnlyList<ExportMetadataField> baseMetadata,
        ref int duplicateCandidateCount)
    {
        var key = BuildExportIdentityKey(document.DocumentId, version?.VersionId);
        if (candidates.TryGetValue(key, out var existing))
        {
            duplicateCandidateCount++;
            existing.AddSourceReference(scope, document.DocumentId, version?.VersionId);
            if (!NdExportScopePreference.IsPreferredCanonicalScope(scope, existing.Scope))
            {
                return;
            }

            existing.Scope = scope;
            existing.Document = document;
            existing.Version = version;
            existing.BaseMetadata = baseMetadata;
            return;
        }

        candidates[key] = new PlannedExportCandidate(scope, document, version, baseMetadata);
    }

    private static string BuildExportIdentityKey(string documentId, string? versionId)
    {
        return $"{documentId}:{versionId ?? "official"}";
    }

    private static string BuildSourcePath(NdExportScope scope, string documentId, string? versionId)
    {
        return string.IsNullOrWhiteSpace(versionId)
            ? $"{scope.ContainerId}/{documentId}"
            : $"{scope.ContainerId}/{documentId}/{versionId}";
    }

    private static List<ExportSourceReference> CloneSourceReferences(IReadOnlyList<ExportSourceReference> sourceReferences)
    {
        return sourceReferences
            .Select(reference => new ExportSourceReference
            {
                SourcePath = reference.SourcePath,
                ScopeKind = reference.ScopeKind,
                Disposition = reference.Disposition,
                Reason = reference.Reason
            })
            .ToList();
    }

    private static string ResolveExportFileExtension(
        NdExportDocumentVersion? version,
        IReadOnlyList<ExportMetadataField> baseMetadata)
    {
        if (version is not null)
        {
            foreach (var attribute in version.Attributes)
            {
                var extension = ResolveExtensionFromField(attribute.Name, attribute.Value);
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    return extension;
                }
            }
        }

        foreach (var field in baseMetadata)
        {
            var extension = ResolveExtensionFromField(field.Name, field.Value);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return extension;
            }
        }

        return string.Empty;
    }

    private static string ResolveExtensionFromField(string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!name.EndsWith(".extension", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".ext", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var normalized = value.Trim().TrimStart('.');
        return string.Equals(normalized, "ndfld", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
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

internal sealed class PlannedExportCandidate(
    NdExportScope scope,
    NdExportDocument document,
    NdExportDocumentVersion? version,
    IReadOnlyList<ExportMetadataField> baseMetadata)
{
    public NdExportScope Scope { get; set; } = scope;

    public NdExportDocument Document { get; set; } = document;

    public NdExportDocumentVersion? Version { get; set; } = version;

    public IReadOnlyList<ExportMetadataField> BaseMetadata { get; set; } = baseMetadata;

    public List<PlannedSourceReference> SourceReferences { get; } =
    [
        new PlannedSourceReference(
            string.IsNullOrWhiteSpace(version?.VersionId)
                ? $"{scope.ContainerId}/{document.DocumentId}"
                : $"{scope.ContainerId}/{document.DocumentId}/{version.VersionId}",
            scope.Kind.ToString())
    ];

    public void AddSourceReference(NdExportScope scope, string documentId, string? versionId)
    {
        var sourcePath = string.IsNullOrWhiteSpace(versionId)
            ? $"{scope.ContainerId}/{documentId}"
            : $"{scope.ContainerId}/{documentId}/{versionId}";
        if (SourceReferences.Any(reference =>
            string.Equals(reference.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reference.ScopeKind, scope.Kind.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SourceReferences.Add(new PlannedSourceReference(sourcePath, scope.Kind.ToString()));
    }
}

internal sealed record PlannedSourceReference(string SourcePath, string ScopeKind);

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

public sealed class ExportPreflightWarningView
{
    public ExportPreflightWarningView(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
