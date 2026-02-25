using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using NetDocsImporter.Core;
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

    public bool CanCancelExport => false;

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

            var customAttributeIds = (await sync.GetSyncedAttributesAsync(SelectedNetDocumentsCabinetId))
                .Select(attribute => !string.IsNullOrWhiteSpace(attribute.AttributeId)
                    ? attribute.AttributeId
                    : attribute.AttributeNum.ToString(CultureInfo.InvariantCulture))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

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

            warnings.Add("Document binary download execution is not wired yet; this preflight validates enumeration and plan counts.");

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

        try
        {
            IsExportBusy = true;
            ExportProgressPercent = 0;
            OnPropertyChanged(nameof(ExportProgressPercentDisplay));
            ExportStatus = "Writing export manifest and metadata...";

            Directory.CreateDirectory(ExportDestinationRootPath);
            var writer = new ExportOutputWriter();
            var manifestPath = await writer.WriteManifestAsync(
                ExportDestinationRootPath,
                _exportPlan.Items);
            var metadataPath = await writer.WriteMetadataAsync(
                ExportDestinationRootPath,
                new MetadataDump
                {
                    SourceCabinetId = _exportPlan.Config.SourceCabinetId,
                    SourceTargetId = _exportPlan.Config.SourceTargetId,
                    GeneratedUtc = DateTime.UtcNow,
                    Items = _exportPlan.Items.Select(item => new MetadataDumpItem
                    {
                        DocumentId = item.DocumentId,
                        VersionId = item.VersionId,
                        SourcePath = item.SourcePath,
                        LocalPath = item.LocalPath,
                        Status = "Planned",
                        Error = string.Empty,
                        MetadataFields = item.MetadataFields
                    }).ToList()
                },
                ExportMetadataFormat);

            _lastExportManifestPath = manifestPath;
            OnPropertyChanged(nameof(CanOpenLastExportManifest));
            ExportProgressPercent = 100;
            OnPropertyChanged(nameof(ExportProgressPercentDisplay));
            ExportStatus =
                $"Export artifacts written. Manifest: {Path.GetFileName(manifestPath)}, metadata: {Path.GetFileName(metadataPath)}. " +
                "Document binary download execution will be wired in the next phase.";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export run failed: {ex.Message}";
        }
        finally
        {
            IsExportBusy = false;
        }
    }

    public void CancelExport()
    {
        ExportStatus = "Cancel is not available yet for export planning.";
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
