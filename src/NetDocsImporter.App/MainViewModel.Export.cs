using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using NetDocsImporter.Core;

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

            var queue = new Queue<NdTargetSelection>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var issues = new List<ExportPreflightIssueView>();
            var containerItems = new List<ExportItem>();
            var folderCount = 0;
            var filterCount = 0;
            var savedSearchCount = 0;
            var collabspaceCount = 0;
            var containerTraversalFailures = 0;
            var discovered = 0;

            queue.Enqueue(CloneSelection(_selectedNetDocumentsTarget));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var key = NdTargetBrowserLogic.BuildTargetKey(current);
                if (!visited.Add(key))
                {
                    continue;
                }

                discovered++;

                var currentIsSavedSearch = NdTargetBrowserLogic.IsSavedSearchTarget(current.Id, current.Extension);
                if (current.Type == NdTargetType.WorkspaceFilter)
                {
                    if (currentIsSavedSearch)
                    {
                        savedSearchCount++;
                    }
                    else
                    {
                        filterCount++;
                    }
                }
                else if (current.Type == NdTargetType.Folder)
                {
                    if (NdTargetBrowserLogic.IsCollabspaceIdentifier(current.Id))
                    {
                        collabspaceCount++;
                    }
                    else
                    {
                        folderCount++;
                    }
                }

                var sourceSegments = new List<string>();
                if (!string.IsNullOrWhiteSpace(SelectedNetDocumentsTargetName))
                {
                    sourceSegments.Add(SelectedNetDocumentsTargetName);
                }
                if (!string.IsNullOrWhiteSpace(current.Name) &&
                    !string.Equals(current.Name, SelectedNetDocumentsTargetName, StringComparison.OrdinalIgnoreCase))
                {
                    sourceSegments.Add(current.Name);
                }

                var extensionHint = currentIsSavedSearch ? ".saved-search" : ".container";
                var localRelativePath = resolver.ResolveRelativePath(
                    sourceSegments,
                    current.Name + extensionHint,
                    current.Id);
                containerItems.Add(new ExportItem
                {
                    DocumentId = current.Id,
                    VersionId = null,
                    SourcePath = current.Id,
                    LocalPath = localRelativePath,
                    SizeBytes = null
                });

                if (current.Type == NdTargetType.WorkspaceFilter)
                {
                    if (!ExportDownloadFiltersAsFolders && !currentIsSavedSearch)
                    {
                        issues.Add(new ExportPreflightIssueView(
                            "Warning",
                            "FILTER_NOT_DOWNLOADED",
                            "Workspace filter is excluded because 'Download filters as folders' is disabled.",
                            current.Name));
                    }

                    continue;
                }

                try
                {
                    var children = await sync.GetContainerChildrenAsync(
                        SelectedNetDocumentsCabinetId,
                        parentContainerId: current.Id,
                        preferredType: current.Type);

                    foreach (var child in children)
                    {
                        if (child.SupportedType is null)
                        {
                            continue;
                        }

                        queue.Enqueue(new NdTargetSelection
                        {
                            Type = child.SupportedType.Value,
                            Id = child.Id,
                            Name = string.IsNullOrWhiteSpace(child.Name) ? child.Id : child.Name,
                            ParentWorkspaceId = child.ParentWorkspaceId,
                            Extension = child.Extension,
                            SourceFlow = NdTargetSourceFlow.Browse
                        });
                    }
                }
                catch (Exception ex)
                {
                    containerTraversalFailures++;
                    issues.Add(new ExportPreflightIssueView(
                        "Warning",
                        "CHILD_ENUMERATION_FAILED",
                        ex.Message,
                        current.Name));
                }
            }

            var warnings = new List<string>();
            if (containerTraversalFailures > 0)
            {
                warnings.Add($"Failed to enumerate children for {containerTraversalFailures.ToString("N0", CultureInfo.CurrentCulture)} container(s).");
            }

            if (string.IsNullOrWhiteSpace(ExportDestinationRootPath))
            {
                warnings.Add("Destination folder is not selected.");
            }

            warnings.Add("Document binary download execution is not wired yet; this preflight validates container scope and topology.");

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
                Items = containerItems,
                DocumentCount = 0,
                VersionCount = 0,
                EstimatedBytes = 0,
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
                $"Scope discovered: folders={folderCount:N0}, filters={filterCount:N0}, saved searches={savedSearchCount:N0}, collabspaces={collabspaceCount:N0}, containers={discovered:N0}.";
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
                        Error = string.Empty
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
