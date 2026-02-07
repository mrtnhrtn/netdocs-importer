
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using NetDocsImporter.Core;

namespace NetDocsImporter.App;

public sealed partial class MainViewModel
{
    private const string UnsupportedTargetReason = "Only Workspace, Workspace Filter, or Folder are supported as upload destinations in this version.";

    private readonly ObservableCollection<NetDocumentsTargetContainerView> _netDocumentsTargetContainers = new();
    private readonly ObservableCollection<NetDocumentsTargetItemView> _recentTargets = new();
    private readonly ObservableCollection<NetDocumentsTargetItemView> _favoriteTargets = new();
    private readonly ObservableCollection<NetDocumentsWorkspaceSearchResultView> _workspaceSearchResults = new();
    private readonly ObservableCollection<NetDocumentsBrowseNodeView> _browseRootNodes = new();
    private readonly ObservableCollection<NetDocumentsTargetProfileAttributeView> _targetProfileAttributes = new();
    private readonly ObservableCollection<NetDocumentsEffectiveDefaultView> _effectiveProfileDefaultsRows = new();
    private readonly Dictionary<string, NdTargetProfileSnapshot> _targetProfileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locallyUnpinnedFavoriteKeys = new(StringComparer.OrdinalIgnoreCase);

    private List<NdTargetRecentItem> _localRecentTargets = new();
    private List<NdTargetFavoriteItem> _localFavoriteTargets = new();
    private NdTargetSelection? _selectedNetDocumentsTarget;
    private NetDocumentsTargetItemView? _selectedRecentTarget;
    private NetDocumentsTargetItemView? _selectedFavoriteTarget;
    private NetDocumentsWorkspaceSearchResultView? _selectedWorkspaceSearchResult;
    private NetDocumentsBrowseNodeView? _selectedBrowseNode;
    private string _selectedNetDocumentsTargetId = string.Empty;
    private string _selectedNetDocumentsTargetName = string.Empty;
    private string _selectedNetDocumentsTargetPath = string.Empty;
    private string _selectedNetDocumentsTargetTypeDisplay = "Not selected";
    private bool _selectedNetDocumentsTargetSupported;
    private string _targetProfileMetadataStatus = "No target confirmed yet.";
    private bool _isTargetBrowserBusy;
    private string _targetBrowserMessage = string.Empty;
    private NdTargetBrowserTab _selectedTargetBrowserTab = NdTargetBrowserTab.Recent;
    private string _workspaceSearchText = string.Empty;
    private EffectiveProfileDefaults _effectiveProfileDefaults = EffectiveProfileDefaults.Empty;

    public ObservableCollection<NetDocumentsTargetContainerView> NetDocumentsTargetContainers => _netDocumentsTargetContainers;

    public ObservableCollection<NetDocumentsTargetItemView> RecentTargets => _recentTargets;

    public ObservableCollection<NetDocumentsTargetItemView> FavoriteTargets => _favoriteTargets;

    public ObservableCollection<NetDocumentsWorkspaceSearchResultView> WorkspaceSearchResults => _workspaceSearchResults;

    public ObservableCollection<NetDocumentsBrowseNodeView> BrowseRootNodes => _browseRootNodes;

    public ObservableCollection<NetDocumentsTargetProfileAttributeView> TargetProfileAttributes => _targetProfileAttributes;

    public ObservableCollection<NetDocumentsEffectiveDefaultView> EffectiveProfileDefaultsRows => _effectiveProfileDefaultsRows;

    public NetDocumentsTargetItemView? SelectedRecentTarget
    {
        get => _selectedRecentTarget;
        set => SetField(ref _selectedRecentTarget, value);
    }

    public NetDocumentsTargetItemView? SelectedFavoriteTarget
    {
        get => _selectedFavoriteTarget;
        set => SetField(ref _selectedFavoriteTarget, value);
    }

    public NetDocumentsWorkspaceSearchResultView? SelectedWorkspaceSearchResult
    {
        get => _selectedWorkspaceSearchResult;
        set => SetField(ref _selectedWorkspaceSearchResult, value);
    }

    public NetDocumentsBrowseNodeView? SelectedBrowseNode
    {
        get => _selectedBrowseNode;
        set
        {
            if (SetField(ref _selectedBrowseNode, value))
            {
                OnPropertyChanged(nameof(CanUseSelectedBrowseNode));
            }
        }
    }

    public string SelectedNetDocumentsTargetId
    {
        get => _selectedNetDocumentsTargetId;
        set
        {
            if (!SetField(ref _selectedNetDocumentsTargetId, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
            OnPropertyChanged(nameof(CanContinueToReviewScope));
            QueueSettingsSave();
        }
    }

    public string SelectedNetDocumentsTargetName
    {
        get => _selectedNetDocumentsTargetName;
        private set
        {
            if (SetField(ref _selectedNetDocumentsTargetName, value))
            {
                OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
                OnPropertyChanged(nameof(CanContinueToReviewScope));
            }
        }
    }

    public string SelectedNetDocumentsTargetPath
    {
        get => _selectedNetDocumentsTargetPath;
        private set => SetField(ref _selectedNetDocumentsTargetPath, value);
    }

    public string SelectedNetDocumentsTargetTypeDisplay
    {
        get => _selectedNetDocumentsTargetTypeDisplay;
        private set
        {
            if (SetField(ref _selectedNetDocumentsTargetTypeDisplay, value))
            {
                OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
                OnPropertyChanged(nameof(CanContinueToReviewScope));
            }
        }
    }

    public string TargetProfileMetadataStatus
    {
        get => _targetProfileMetadataStatus;
        private set => SetField(ref _targetProfileMetadataStatus, value);
    }

    public bool IsTargetBrowserBusy
    {
        get => _isTargetBrowserBusy;
        private set => SetField(ref _isTargetBrowserBusy, value);
    }

    public string TargetBrowserMessage
    {
        get => _targetBrowserMessage;
        private set => SetField(ref _targetBrowserMessage, value);
    }

    public NdTargetBrowserTab SelectedTargetBrowserTab
    {
        get => _selectedTargetBrowserTab;
        set => SetField(ref _selectedTargetBrowserTab, value);
    }

    public string WorkspaceSearchText
    {
        get => _workspaceSearchText;
        set => SetField(ref _workspaceSearchText, value);
    }

    public EffectiveProfileDefaults EffectiveProfileDefaults
    {
        get => _effectiveProfileDefaults;
        private set
        {
            if (SetField(ref _effectiveProfileDefaults, value))
            {
                OnPropertyChanged(nameof(CanContinueToReviewScope));
            }
        }
    }

    public bool CanSelectSourceFolder => IsNetDocumentsConnected;

    public bool CanPickNetDocumentsTarget =>
        IsNetDocumentsConnected &&
        !string.IsNullOrWhiteSpace(SelectedNetDocumentsRepositoryId) &&
        !string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId);

    public bool CanConfirmNetDocumentsTarget =>
        CanPickNetDocumentsTarget &&
        !string.IsNullOrWhiteSpace(SelectedNetDocumentsTargetId) &&
        _selectedNetDocumentsTargetSupported;

    public bool CanContinueToReviewScope =>
        CanConfirmNetDocumentsTarget &&
        !string.IsNullOrWhiteSpace(CurrentJobId);

    public bool CanUseSelectedBrowseNode => SelectedBrowseNode is not null;

    public bool IsSelectedTargetFavorite
    {
        get
        {
            if (_selectedNetDocumentsTarget is null)
            {
                return false;
            }

            var targetKey = NdTargetBrowserLogic.BuildTargetKey(_selectedNetDocumentsTarget);
            return _localFavoriteTargets.Any(f =>
                string.Equals(NdTargetBrowserLogic.BuildTargetKey(f.Selection), targetKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task LoadNetDocumentsTargetContainersAsync()
    {
        if (!CanPickNetDocumentsTarget)
        {
            UpdateOnUi(() =>
            {
                _netDocumentsTargetContainers.Clear();
                _recentTargets.Clear();
                _favoriteTargets.Clear();
                _workspaceSearchResults.Clear();
                _browseRootNodes.Clear();
            });
            return;
        }

        IsTargetBrowserBusy = true;
        try
        {
            await LoadTargetBrowserAsync();

            var sync = RequireSyncService();
            var supportedTargets = await sync.GetSupportedTargetContainersAsync(SelectedNetDocumentsCabinetId);
            var mapped = supportedTargets
                .Select(t => new NetDocumentsTargetContainerView(t.Id, t.Name, t.Type.ToString(), t.ParentWorkspaceId))
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            UpdateOnUi(() =>
            {
                _netDocumentsTargetContainers.Clear();
                foreach (var target in mapped)
                {
                    _netDocumentsTargetContainers.Add(target);
                }
            });

            if (_selectedNetDocumentsTarget is not null)
            {
                var path = await sync.ResolveTargetPathAsync(SelectedNetDocumentsCabinetId, _selectedNetDocumentsTarget.Id);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    SelectedNetDocumentsTargetPath = path;
                }
            }
        }
        catch (Exception ex)
        {
            TargetBrowserMessage = $"Target browser refresh failed: {ex.Message}";
            StatusText = TargetBrowserMessage;
        }
        finally
        {
            IsTargetBrowserBusy = false;
        }
    }

    public async Task LoadTargetBrowserAsync()
    {
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        await RefreshRecentTargetsAsync();
        await RefreshFavoriteTargetsAsync();

        if (_browseRootNodes.Count == 0)
        {
            await LoadBrowseRootsAsync();
        }
    }

    public async Task RefreshRecentTargetsAsync()
    {
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        IReadOnlyList<NdTargetRecentItem> serverItems = Array.Empty<NdTargetRecentItem>();
        try
        {
            serverItems = await RequireSyncService().GetRecentTargetsAsync(SelectedNetDocumentsCabinetId);
            Trace.WriteLine($"NetDocuments target browser: recent source=server count={serverItems.Count}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NetDocuments target browser: recent source=fallback-local reason={ex.Message}");
        }

        var merged = NdTargetBrowserLogic.MergeRecentTargets(serverItems, _localRecentTargets);
        UpdateOnUi(() =>
        {
            _recentTargets.Clear();
            foreach (var item in merged)
            {
                _recentTargets.Add(NetDocumentsTargetItemView.FromRecent(item));
            }
        });
    }

    public async Task RefreshFavoriteTargetsAsync()
    {
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        IReadOnlyList<NdTargetFavoriteItem> serverItems = Array.Empty<NdTargetFavoriteItem>();
        try
        {
            serverItems = await RequireSyncService().GetFavoriteTargetsAsync(SelectedNetDocumentsCabinetId);
            Trace.WriteLine($"NetDocuments target browser: favorites source=server count={serverItems.Count}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NetDocuments target browser: favorites source=fallback-local reason={ex.Message}");
        }

        var merged = NdTargetBrowserLogic.MergeFavoriteTargets(serverItems, _localFavoriteTargets)
            .Where(item => !_locallyUnpinnedFavoriteKeys.Contains(NdTargetBrowserLogic.BuildTargetKey(item.Selection)))
            .ToList();

        UpdateOnUi(() =>
        {
            _favoriteTargets.Clear();
            foreach (var item in merged)
            {
                _favoriteTargets.Add(NetDocumentsTargetItemView.FromFavorite(item));
            }
        });
    }

    public async Task SearchWorkspacesAsync()
    {
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        var query = WorkspaceSearchText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            TargetBrowserMessage = "Enter workspace search text.";
            return;
        }

        try
        {
            var results = await RequireSyncService().SearchWorkspacesAsync(SelectedNetDocumentsCabinetId, query, 50);
            var mapped = results
                .OrderBy(r => r.WorkspaceName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new NetDocumentsWorkspaceSearchResultView(r))
                .ToList();

            UpdateOnUi(() =>
            {
                _workspaceSearchResults.Clear();
                foreach (var result in mapped)
                {
                    _workspaceSearchResults.Add(result);
                }
            });

            TargetBrowserMessage = mapped.Count == 0 ? "No workspaces found." : $"Workspace matches: {mapped.Count}.";
            Trace.WriteLine($"NetDocuments target browser: workspace search query='{query}' count={mapped.Count}");
            QueueSettingsSave();
        }
        catch (Exception ex)
        {
            TargetBrowserMessage = $"Workspace search failed: {ex.Message}";
            StatusText = TargetBrowserMessage;
        }
    }

    public async Task LoadSelectedWorkspaceAsync()
    {
        if (SelectedWorkspaceSearchResult is null)
        {
            TargetBrowserMessage = "Select a workspace result first.";
            return;
        }

        await LoadWorkspaceRootAsync(SelectedWorkspaceSearchResult.WorkspaceId);
    }

    public async Task LoadWorkspaceRootAsync(string workspaceId)
    {
        if (!CanPickNetDocumentsTarget || string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        try
        {
            var children = await RequireSyncService().GetContainerChildrenAsync(SelectedNetDocumentsCabinetId, workspaceId: workspaceId);
            var mapped = children.Select(child => new NetDocumentsBrowseNodeView(child)).ToList();
            UpdateOnUi(() =>
            {
                _browseRootNodes.Clear();
                foreach (var node in mapped)
                {
                    _browseRootNodes.Add(node);
                }
                SelectedTargetBrowserTab = NdTargetBrowserTab.Browse;
            });

            Trace.WriteLine($"NetDocuments target browser: loaded workspace root id={workspaceId} children={mapped.Count}");
            TargetBrowserMessage = mapped.Count == 0
                ? "No children found for selected workspace."
                : $"Loaded {mapped.Count} children for workspace.";
        }
        catch (Exception ex)
        {
            TargetBrowserMessage = $"Failed loading workspace tree: {ex.Message}";
            StatusText = TargetBrowserMessage;
        }
    }

    public async Task LoadBrowseRootsAsync()
    {
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        try
        {
            var roots = await RequireSyncService().GetContainerChildrenAsync(SelectedNetDocumentsCabinetId);
            var mapped = roots.Select(node => new NetDocumentsBrowseNodeView(node)).ToList();
            UpdateOnUi(() =>
            {
                _browseRootNodes.Clear();
                foreach (var node in mapped)
                {
                    _browseRootNodes.Add(node);
                }
            });

            Trace.WriteLine($"NetDocuments target browser: browse roots loaded count={mapped.Count}");
        }
        catch (Exception ex)
        {
            TargetBrowserMessage = $"Browse root load failed: {ex.Message}";
            StatusText = TargetBrowserMessage;
        }
    }

    public async Task ExpandBrowseNodeAsync(NetDocumentsBrowseNodeView? node)
    {
        if (node is null || !node.HasChildren || node.ChildrenLoadState == NdChildrenLoadState.Loaded)
        {
            return;
        }

        if (node.ChildrenLoadState == NdChildrenLoadState.Loading)
        {
            return;
        }

        node.ChildrenLoadState = NdChildrenLoadState.Loading;
        try
        {
            var children = await RequireSyncService().GetContainerChildrenAsync(SelectedNetDocumentsCabinetId, parentContainerId: node.Id);
            var mapped = children.Select(child => new NetDocumentsBrowseNodeView(child)).ToList();
            node.ReplaceChildren(mapped);
            node.ChildrenLoadState = NdChildrenLoadState.Loaded;
            Trace.WriteLine($"NetDocuments target browser: expanded node id={node.Id} children={mapped.Count}");
        }
        catch (Exception ex)
        {
            node.ChildrenLoadState = NdChildrenLoadState.Failed;
            TargetBrowserMessage = $"Failed loading children for '{node.Name}': {ex.Message}";
            StatusText = TargetBrowserMessage;
        }
    }

    public async Task SelectTargetFromRecentAsync()
    {
        if (SelectedRecentTarget is null)
        {
            TargetBrowserMessage = "Select a recent target first.";
            return;
        }

        await SetSelectedTargetAsync(SelectedRecentTarget.Selection, SelectedRecentTarget.PathDisplay);
    }

    public async Task SelectTargetFromFavoriteAsync()
    {
        if (SelectedFavoriteTarget is null)
        {
            TargetBrowserMessage = "Select a favorite target first.";
            return;
        }

        await SetSelectedTargetAsync(SelectedFavoriteTarget.Selection, SelectedFavoriteTarget.PathDisplay);
    }

    public async Task SelectWorkspaceAsTargetAsync()
    {
        if (SelectedWorkspaceSearchResult is null)
        {
            TargetBrowserMessage = "Select a workspace result first.";
            return;
        }

        var selection = new NdTargetSelection
        {
            Type = NdTargetType.Workspace,
            Id = SelectedWorkspaceSearchResult.WorkspaceId,
            Name = SelectedWorkspaceSearchResult.WorkspaceName,
            ParentWorkspaceId = SelectedWorkspaceSearchResult.WorkspaceId
        };

        await SetSelectedTargetAsync(selection, SelectedWorkspaceSearchResult.DisplayPath);
    }

    public async Task SelectTargetFromBrowseNodeAsync()
    {
        if (SelectedBrowseNode is null)
        {
            TargetBrowserMessage = "Select a browse node first.";
            return;
        }

        if (!SelectedBrowseNode.IsSelectable || SelectedBrowseNode.SupportedType is null)
        {
            Trace.WriteLine($"NetDocuments target browser: unsupported node selection attempted id={SelectedBrowseNode.Id} type={SelectedBrowseNode.TypeRaw}");
            TargetBrowserMessage = string.IsNullOrWhiteSpace(SelectedBrowseNode.UnsupportedReason)
                ? UnsupportedTargetReason
                : SelectedBrowseNode.UnsupportedReason;
            StatusText = TargetBrowserMessage;
            return;
        }

        var selection = new NdTargetSelection
        {
            Type = SelectedBrowseNode.SupportedType.Value,
            Id = SelectedBrowseNode.Id,
            Name = SelectedBrowseNode.Name,
            ParentWorkspaceId = SelectedBrowseNode.ParentWorkspaceId
        };

        await SetSelectedTargetAsync(selection, SelectedBrowseNode.PathDisplay);
    }

    public async Task ToggleFavoriteForSelectedTargetAsync()
    {
        if (_selectedNetDocumentsTarget is null)
        {
            TargetBrowserMessage = "Select a target first.";
            return;
        }

        var targetKey = NdTargetBrowserLogic.BuildTargetKey(_selectedNetDocumentsTarget);
        var existing = _localFavoriteTargets.FirstOrDefault(item =>
            string.Equals(NdTargetBrowserLogic.BuildTargetKey(item.Selection), targetKey, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            _localFavoriteTargets.Add(new NdTargetFavoriteItem
            {
                Selection = CloneSelection(_selectedNetDocumentsTarget),
                PinnedUtc = DateTime.UtcNow,
                Source = NdTargetSource.Local
            });
            _locallyUnpinnedFavoriteKeys.Remove(targetKey);
            TargetBrowserMessage = "Added selected target to favorites.";
        }
        else
        {
            _localFavoriteTargets.Remove(existing);
            _locallyUnpinnedFavoriteKeys.Add(targetKey);
            TargetBrowserMessage = "Removed selected target from favorites.";
        }

        OnPropertyChanged(nameof(IsSelectedTargetFavorite));
        await RefreshFavoriteTargetsAsync();
        QueueSettingsSave();
    }

    public async Task ConfirmNetDocumentsTargetAsync()
    {
        if (!CanPickNetDocumentsTarget)
        {
            StatusText = "Connect and choose repository/cabinet before confirming a target.";
            return;
        }

        if (_selectedNetDocumentsTarget is null || !_selectedNetDocumentsTargetSupported)
        {
            StatusText = UnsupportedTargetReason;
            return;
        }

        if (!string.IsNullOrWhiteSpace(CurrentJobId) && !string.IsNullOrWhiteSpace(SelectedNetDocumentsRepositoryId))
        {
            await EnsureCurrentJobRepositoryAsync(SelectedNetDocumentsRepositoryId);
        }

        try
        {
            await SyncSelectedTargetProfileSnapshotAsync();
            AddSelectedTargetToLocalRecents();
            await RefreshRecentTargetsAsync();
            QueueSettingsSave();
            await RefreshReviewScopeNetDocumentsAsync();
            OnPropertyChanged(nameof(CanContinueToReviewScope));

            StatusText = $"NetDocuments target confirmed: {SelectedNetDocumentsTargetName} ({SelectedNetDocumentsTargetTypeDisplay}).";
            Trace.WriteLine($"NetDocuments target confirmed type={SelectedNetDocumentsTargetTypeDisplay}, id={SelectedNetDocumentsTargetId}, name={SelectedNetDocumentsTargetName}, path={SelectedNetDocumentsTargetPath}");
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to confirm target: {ex.Message}";
        }
    }

    public Task ContinueToReviewScopeAsync()
    {
        if (!CanContinueToReviewScope)
        {
            StatusText = "Confirm a valid NetDocuments target first.";
            return Task.CompletedTask;
        }

        SetCurrentStep(StepKey.ReviewScope);
        return RefreshReviewScopeNetDocumentsAsync();
    }

    private async Task SetSelectedTargetAsync(NdTargetSelection selection, string? knownPath = null)
    {
        _selectedNetDocumentsTarget = CloneSelection(selection);
        _selectedNetDocumentsTargetSupported = true;

        SelectedNetDocumentsTargetId = selection.Id;
        SelectedNetDocumentsTargetName = string.IsNullOrWhiteSpace(selection.Name) ? selection.Id : selection.Name;
        SelectedNetDocumentsTargetTypeDisplay = selection.Type switch
        {
            NdTargetType.Workspace => "Workspace",
            NdTargetType.WorkspaceFilter => "Workspace Filter",
            NdTargetType.Folder => "Folder",
            _ => selection.Type.ToString()
        };

        if (string.IsNullOrWhiteSpace(knownPath) && CanPickNetDocumentsTarget)
        {
            try
            {
                knownPath = await RequireSyncService().ResolveTargetPathAsync(SelectedNetDocumentsCabinetId, selection.Id);
            }
            catch
            {
                knownPath = selection.Name;
            }
        }

        SelectedNetDocumentsTargetPath = string.IsNullOrWhiteSpace(knownPath) ? selection.Name : knownPath;
        TargetBrowserMessage = $"Selected target: {SelectedNetDocumentsTargetName}.";
        OnPropertyChanged(nameof(IsSelectedTargetFavorite));
        OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
        OnPropertyChanged(nameof(CanContinueToReviewScope));
        QueueSettingsSave();
    }

    private async Task SyncSelectedTargetProfileSnapshotAsync()
    {
        if (_selectedNetDocumentsTarget is null)
        {
            EffectiveProfileDefaults = EffectiveProfileDefaults.Empty;
            UpdateProfileViewCollections(Array.Empty<NetDocumentsTargetProfileAttributeView>(), Array.Empty<NetDocumentsEffectiveDefaultView>());
            TargetProfileMetadataStatus = "No target selected.";
            return;
        }

        var cacheKey = BuildTargetSnapshotCacheKey(_selectedNetDocumentsTarget);
        if (!_targetProfileCache.TryGetValue(cacheKey, out var snapshot))
        {
            snapshot = await RequireSyncService().GetTargetProfileSnapshotAsync(
                SelectedNetDocumentsCabinetId,
                SelectedNetDocumentsRepositoryId,
                _selectedNetDocumentsTarget);
            _targetProfileCache[cacheKey] = snapshot;
        }

        EffectiveProfileDefaults = snapshot.EffectiveDefaults ?? EffectiveProfileDefaults.Empty;

        var defaultsByAttribute = EffectiveProfileDefaults.ValuesByAttributeId;
        var attributeRows = snapshot.Attributes
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(attribute =>
            {
                defaultsByAttribute.TryGetValue(attribute.AttributeId, out var value);
                return new NetDocumentsTargetProfileAttributeView(
                    attribute.Name,
                    attribute.DataType,
                    attribute.IsRequired,
                    value?.DisplayValue ?? string.Empty,
                    value?.RawValue ?? string.Empty);
            })
            .ToList();

        var defaultRows = defaultsByAttribute.Values
            .OrderBy(v => v.AttributeName, StringComparer.OrdinalIgnoreCase)
            .Select(v => new NetDocumentsEffectiveDefaultView(v.AttributeName, v.DisplayValue, v.RawValue))
            .ToList();

        UpdateProfileViewCollections(attributeRows, defaultRows);
        TargetProfileMetadataStatus =
            $"Synced {attributeRows.Count} profile attributes and {defaultRows.Count} inherited defaults at {snapshot.SyncedUtc.ToLocalTime():g}.";

        Trace.WriteLine($"NetDocuments profile metadata synced attributes={attributeRows.Count} defaults={defaultRows.Count}");
    }

    private void UpdateProfileViewCollections(
        IReadOnlyList<NetDocumentsTargetProfileAttributeView> attributeRows,
        IReadOnlyList<NetDocumentsEffectiveDefaultView> defaultRows)
    {
        UpdateOnUi(() =>
        {
            _targetProfileAttributes.Clear();
            foreach (var row in attributeRows)
            {
                _targetProfileAttributes.Add(row);
            }

            _effectiveProfileDefaultsRows.Clear();
            foreach (var row in defaultRows)
            {
                _effectiveProfileDefaultsRows.Add(row);
            }
        });
    }

    private void AddSelectedTargetToLocalRecents()
    {
        if (_selectedNetDocumentsTarget is null)
        {
            return;
        }

        var key = NdTargetBrowserLogic.BuildTargetKey(_selectedNetDocumentsTarget);
        _localRecentTargets = _localRecentTargets
            .Where(item => !string.Equals(NdTargetBrowserLogic.BuildTargetKey(item.Selection), key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _localRecentTargets.Insert(0, new NdTargetRecentItem
        {
            Selection = CloneSelection(_selectedNetDocumentsTarget),
            LastUsedUtc = DateTime.UtcNow,
            Source = NdTargetSource.Local
        });

        if (_localRecentTargets.Count > 30)
        {
            _localRecentTargets = _localRecentTargets.Take(30).ToList();
        }
    }

    private string BuildTargetSnapshotCacheKey(NdTargetSelection target)
    {
        return $"{SelectedNetDocumentsRepositoryId}:{SelectedNetDocumentsCabinetId}:{NdTargetBrowserLogic.BuildTargetKey(target)}";
    }

    private void RestoreTargetSelectionFromSettings(NetDocumentsConnectionSettings settings)
    {
        _localRecentTargets = NdTargetBrowserLogic.DeserializeRecentTargets(settings.RecentTargetsJson).ToList();
        _localFavoriteTargets = NdTargetBrowserLogic.DeserializeFavoriteTargets(settings.FavoriteTargetsJson).ToList();
        WorkspaceSearchText = settings.LastWorkspaceQuery ?? string.Empty;

        EffectiveProfileDefaults = EffectiveProfileDefaults.FromJson(settings.EffectiveProfileDefaultsJson);
        var defaultRows = EffectiveProfileDefaults.ValuesByAttributeId.Values
            .OrderBy(v => v.AttributeName, StringComparer.OrdinalIgnoreCase)
            .Select(v => new NetDocumentsEffectiveDefaultView(v.AttributeName, v.DisplayValue, v.RawValue))
            .ToList();
        UpdateProfileViewCollections(Array.Empty<NetDocumentsTargetProfileAttributeView>(), defaultRows);

        if (!Enum.TryParse<NdTargetType>(settings.SelectedTargetType, ignoreCase: true, out var targetType) ||
            string.IsNullOrWhiteSpace(settings.SelectedTargetId))
        {
            _selectedNetDocumentsTarget = null;
            SelectedNetDocumentsTargetId = string.Empty;
            SelectedNetDocumentsTargetName = string.Empty;
            SelectedNetDocumentsTargetTypeDisplay = "Not selected";
            SelectedNetDocumentsTargetPath = string.Empty;
            _selectedNetDocumentsTargetSupported = false;
            return;
        }

        _selectedNetDocumentsTarget = new NdTargetSelection
        {
            Type = targetType,
            Id = settings.SelectedTargetId,
            Name = settings.SelectedTargetName ?? settings.SelectedTargetId,
            ParentWorkspaceId = settings.SelectedTargetParentWorkspaceId
        };

        SelectedNetDocumentsTargetId = _selectedNetDocumentsTarget.Id;
        SelectedNetDocumentsTargetName = _selectedNetDocumentsTarget.Name;
        SelectedNetDocumentsTargetTypeDisplay = _selectedNetDocumentsTarget.Type switch
        {
            NdTargetType.Workspace => "Workspace",
            NdTargetType.WorkspaceFilter => "Workspace Filter",
            NdTargetType.Folder => "Folder",
            _ => _selectedNetDocumentsTarget.Type.ToString()
        };
        SelectedNetDocumentsTargetPath = _selectedNetDocumentsTarget.Name;
        _selectedNetDocumentsTargetSupported = true;
        OnPropertyChanged(nameof(IsSelectedTargetFavorite));
    }

    private void SaveTargetSelectionToSettings(NetDocumentsConnectionSettings settings)
    {
        settings.RecentTargetsJson = NdTargetBrowserLogic.SerializeRecentTargets(_localRecentTargets);
        settings.FavoriteTargetsJson = NdTargetBrowserLogic.SerializeFavoriteTargets(_localFavoriteTargets);
        settings.LastWorkspaceQuery = WorkspaceSearchText ?? string.Empty;

        if (_selectedNetDocumentsTarget is null)
        {
            settings.SelectedTargetType = string.Empty;
            settings.SelectedTargetId = string.Empty;
            settings.SelectedTargetName = string.Empty;
            settings.SelectedTargetParentWorkspaceId = string.Empty;
            settings.EffectiveProfileDefaultsJson = string.Empty;
            return;
        }

        settings.SelectedTargetType = _selectedNetDocumentsTarget.Type.ToString();
        settings.SelectedTargetId = _selectedNetDocumentsTarget.Id;
        settings.SelectedTargetName = _selectedNetDocumentsTarget.Name;
        settings.SelectedTargetParentWorkspaceId = _selectedNetDocumentsTarget.ParentWorkspaceId ?? string.Empty;
        settings.EffectiveProfileDefaultsJson = EffectiveProfileDefaults.ToJson();
    }

    private static NdTargetSelection CloneSelection(NdTargetSelection selection)
    {
        return new NdTargetSelection
        {
            Type = selection.Type,
            Id = selection.Id,
            Name = selection.Name,
            ParentWorkspaceId = selection.ParentWorkspaceId
        };
    }
}

public sealed class NetDocumentsTargetContainerView
{
    public NetDocumentsTargetContainerView(string id, string name, string type, string? parentWorkspaceId)
    {
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        Type = type;
        ParentWorkspaceId = parentWorkspaceId;
    }

    public string Id { get; }

    public string Name { get; }

    public string Type { get; }

    public string? ParentWorkspaceId { get; }

    public string DisplayName => $"{Name} ({Type})";
}

public sealed class NetDocumentsTargetItemView
{
    private NetDocumentsTargetItemView(NdTargetSelection selection, string sourceDisplay, DateTime timestampUtc)
    {
        Selection = selection;
        SourceDisplay = sourceDisplay;
        TimestampDisplay = timestampUtc.ToLocalTime().ToString("g");
    }

    public NdTargetSelection Selection { get; }

    public string SourceDisplay { get; }

    public string TimestampDisplay { get; }

    public string Name => Selection.Name;

    public string TypeDisplay => Selection.Type switch
    {
        NdTargetType.Workspace => "Workspace",
        NdTargetType.WorkspaceFilter => "Workspace Filter",
        NdTargetType.Folder => "Folder",
        _ => Selection.Type.ToString()
    };

    public string PathDisplay => string.IsNullOrWhiteSpace(Selection.ParentWorkspaceId)
        ? Selection.Name
        : $"{Selection.ParentWorkspaceId} / {Selection.Name}";

    public static NetDocumentsTargetItemView FromRecent(NdTargetRecentItem item)
    {
        return new NetDocumentsTargetItemView(item.Selection, item.Source.ToString(), item.LastUsedUtc);
    }

    public static NetDocumentsTargetItemView FromFavorite(NdTargetFavoriteItem item)
    {
        return new NetDocumentsTargetItemView(item.Selection, item.Source.ToString(), item.PinnedUtc);
    }
}

public sealed class NetDocumentsWorkspaceSearchResultView
{
    public NetDocumentsWorkspaceSearchResultView(NdWorkspaceSearchResult result)
    {
        WorkspaceId = result.WorkspaceId;
        WorkspaceName = string.IsNullOrWhiteSpace(result.WorkspaceName) ? result.WorkspaceId : result.WorkspaceName;
        RepositoryId = result.RepositoryId;
        CabinetId = result.CabinetId ?? string.Empty;
    }

    public string WorkspaceId { get; }

    public string WorkspaceName { get; }

    public string RepositoryId { get; }

    public string CabinetId { get; }

    public string DisplayPath => string.IsNullOrWhiteSpace(CabinetId)
        ? WorkspaceName
        : $"{CabinetId} / {WorkspaceName}";
}

public sealed class NetDocumentsBrowseNodeView
{
    public NetDocumentsBrowseNodeView(NdContainerNode node)
    {
        Id = node.Id;
        Name = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;
        TypeRaw = node.TypeRaw;
        ParentId = node.ParentId;
        ParentWorkspaceId = node.ParentWorkspaceId;
        PathDisplay = node.PathDisplay;
        SupportedType = node.SupportedType;
        IsSelectable = node.IsSelectable;
        UnsupportedReason = node.UnsupportedReason;
        HasChildren = node.HasChildren;
        ChildrenLoadState = node.ChildrenLoadState;

        if (HasChildren)
        {
            Children.Add(new NetDocumentsBrowseNodeView());
        }
    }

    private NetDocumentsBrowseNodeView()
    {
        IsPlaceholder = true;
        Name = "Loading...";
        Id = "__placeholder";
        TypeRaw = string.Empty;
        ParentId = string.Empty;
        PathDisplay = string.Empty;
        UnsupportedReason = string.Empty;
    }

    public string Id { get; }

    public string Name { get; }

    public string TypeRaw { get; }

    public string ParentId { get; }

    public string? ParentWorkspaceId { get; }

    public string PathDisplay { get; }

    public NdTargetType? SupportedType { get; }

    public bool IsSelectable { get; }

    public string UnsupportedReason { get; }

    public bool HasChildren { get; }

    public bool IsPlaceholder { get; }

    public ObservableCollection<NetDocumentsBrowseNodeView> Children { get; } = new();

    public NdChildrenLoadState ChildrenLoadState { get; set; }

    public string TypeDisplay
    {
        get
        {
            if (SupportedType.HasValue)
            {
                return SupportedType.Value switch
                {
                    NdTargetType.Workspace => "Workspace",
                    NdTargetType.WorkspaceFilter => "Workspace Filter",
                    NdTargetType.Folder => "Folder",
                    _ => SupportedType.Value.ToString()
                };
            }

            return string.IsNullOrWhiteSpace(TypeRaw) ? "Unknown" : TypeRaw;
        }
    }

    public string DisplayName => IsSelectable ? $"{Name} ({TypeDisplay})" : $"{Name} ({TypeDisplay}, unsupported)";

    public void ReplaceChildren(IReadOnlyList<NetDocumentsBrowseNodeView> nodes)
    {
        Children.Clear();
        foreach (var node in nodes)
        {
            Children.Add(node);
        }
    }
}

public sealed class NetDocumentsTargetProfileAttributeView
{
    public NetDocumentsTargetProfileAttributeView(string name, string dataType, bool isRequired, string valueDisplay, string valueRaw)
    {
        Name = name;
        DataType = string.IsNullOrWhiteSpace(dataType) ? "text" : dataType;
        IsRequired = isRequired;
        ValueDisplay = valueDisplay;
        ValueRaw = valueRaw;
    }

    public string Name { get; }

    public string DataType { get; }

    public bool IsRequired { get; }

    public string RequiredDisplay => IsRequired ? "Yes" : "No";

    public string ValueDisplay { get; }

    public string ValueRaw { get; }
}

public sealed class NetDocumentsEffectiveDefaultView
{
    public NetDocumentsEffectiveDefaultView(string attributeName, string displayValue, string rawValue)
    {
        AttributeName = attributeName;
        DisplayValue = displayValue;
        RawValue = rawValue;
    }

    public string AttributeName { get; }

    public string DisplayValue { get; }

    public string RawValue { get; }
}
