
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.App;

public sealed partial class MainViewModel
{
    private const string UnsupportedTargetReason = "Only Workspace, Workspace Filter, or Folder are supported as upload destinations in this version.";
    private const string CabinetRootNodeTypeRaw = "CabinetRoot";
    private const int WorkspaceSearchMaxResults = 8;
    private const int WorkspaceSearchMaxParentCandidates = 4;
    private const int WorkspaceSearchMaxChildCandidatesPerParent = 2;
    private const int WorkspaceSearchMaxResolveAttempts = 8;
    private static readonly TimeSpan WorkspaceCacheTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan BrowseChildrenCacheTtl = TimeSpan.FromMinutes(10);

    private readonly ObservableCollection<NetDocumentsTargetContainerView> _netDocumentsTargetContainers = new();
    private readonly ObservableCollection<NetDocumentsTargetItemView> _recentTargets = new();
    private readonly ObservableCollection<NetDocumentsTargetItemView> _favoriteTargets = new();
    private readonly ObservableCollection<NetDocumentsWorkspaceTargetResultView> _workspaceSearchTargets = new();
    private List<NetDocumentsWorkspaceTargetResultView> _workspaceTreeSearchTargets = new();
    private readonly ObservableCollection<NetDocumentsBrowseNodeView> _browseRootNodes = new();
    private readonly ObservableCollection<NetDocumentsTargetProfileAttributeView> _targetProfileAttributes = new();
    private readonly ObservableCollection<NetDocumentsEffectiveDefaultView> _effectiveProfileDefaultsRows = new();
    private readonly Dictionary<string, NdTargetProfileSnapshot> _targetProfileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locallyUnpinnedFavoriteKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NetDocumentsWorkspaceTargetResultView?> _workspaceLookupPairCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _workspaceLookupInvalidPairCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _browseExpansionScopeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly NdTargetChildrenMemoryCache _browseChildrenCache = new(BrowseChildrenCacheTtl);

    private List<NdTargetRecentItem> _localRecentTargets = new();
    private List<NdTargetFavoriteItem> _localFavoriteTargets = new();
    private NdTargetSelection? _selectedNetDocumentsTarget;
    private NetDocumentsTargetItemView? _selectedRecentTarget;
    private NetDocumentsTargetItemView? _selectedFavoriteTarget;
    private NetDocumentsWorkspaceTargetResultView? _selectedWorkspaceSearchTarget;
    private NetDocumentsBrowseNodeView? _selectedBrowseNode;
    private string _selectedNetDocumentsTargetId = string.Empty;
    private string _selectedNetDocumentsTargetName = string.Empty;
    private string _selectedNetDocumentsTargetPath = string.Empty;
    private string _selectedNetDocumentsTargetTypeDisplay = "Not selected";
    private bool _selectedNetDocumentsTargetSupported;
    private string _targetProfileMetadataStatus = "No target confirmed yet.";
    private bool _isTargetBrowserBusy;
    private bool _isLoadingRecentTargets;
    private bool _isLoadingFavoriteTargets;
    private string _targetBrowserMessage = string.Empty;
    private NdTargetBrowserTab _selectedTargetBrowserTab = NdTargetBrowserTab.Recent;
    private string _workspaceSearchText = string.Empty;
    private string _workspaceLookupStatus = string.Empty;
    private bool _isBrowseFilterPanelVisible;
    private bool _browseFilterShowCabFolders = true;
    private bool _browseFilterShowFolders = true;
    private bool _browseFilterShowFilters = true;
    private bool _browseFilterShowCollabspaces = true;
    private EffectiveProfileDefaults _effectiveProfileDefaults = EffectiveProfileDefaults.Empty;
    private WorkspaceLookupContext? _workspaceLookupContext;
    private CancellationTokenSource? _workspaceSearchCts;
    private bool _hasLoadedRecentTargets;
    private bool _hasLoadedFavoriteTargets;
    private bool _isAutoSelectingTarget;
    private CancellationTokenSource? _autoTargetSelectionCts;
    private long _targetBrowserContextVersion;
    private bool _isWorkspaceLookupAvailable = true;

    public ObservableCollection<NetDocumentsTargetContainerView> NetDocumentsTargetContainers => _netDocumentsTargetContainers;

    public ObservableCollection<NetDocumentsTargetItemView> RecentTargets => _recentTargets;

    public ObservableCollection<NetDocumentsTargetItemView> FavoriteTargets => _favoriteTargets;

    public ObservableCollection<NetDocumentsWorkspaceTargetResultView> WorkspaceSearchTargets => _workspaceSearchTargets;

    public ObservableCollection<NetDocumentsBrowseNodeView> BrowseRootNodes => _browseRootNodes;

    public ObservableCollection<NetDocumentsTargetProfileAttributeView> TargetProfileAttributes => _targetProfileAttributes;

    public ObservableCollection<NetDocumentsEffectiveDefaultView> EffectiveProfileDefaultsRows => _effectiveProfileDefaultsRows;

    public NetDocumentsTargetItemView? SelectedRecentTarget
    {
        get => _selectedRecentTarget;
        set
        {
            if (!SetField(ref _selectedRecentTarget, value))
            {
                return;
            }

            if (value is null || _isAutoSelectingTarget)
            {
                return;
            }

            _ = AutoCommitRecentTargetSelectionAsync(value);
        }
    }

    public NetDocumentsTargetItemView? SelectedFavoriteTarget
    {
        get => _selectedFavoriteTarget;
        set
        {
            if (!SetField(ref _selectedFavoriteTarget, value))
            {
                return;
            }

            if (value is null || _isAutoSelectingTarget)
            {
                return;
            }

            _ = AutoCommitFavoriteTargetSelectionAsync(value);
        }
    }

    public NetDocumentsWorkspaceTargetResultView? SelectedWorkspaceSearchTarget
    {
        get => _selectedWorkspaceSearchTarget;
        set
        {
            if (SetField(ref _selectedWorkspaceSearchTarget, value))
            {
                OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));

                if (value is null || _isAutoSelectingTarget)
                {
                    return;
                }

                _ = AutoCommitWorkspaceTargetSelectionAsync(value);
            }
        }
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
            OnPropertyChanged(nameof(CanRunDirectUpload));
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

    public bool IsLoadingRecentTargets
    {
        get => _isLoadingRecentTargets;
        private set => SetField(ref _isLoadingRecentTargets, value);
    }

    public bool IsLoadingFavoriteTargets
    {
        get => _isLoadingFavoriteTargets;
        private set => SetField(ref _isLoadingFavoriteTargets, value);
    }

    public string TargetBrowserMessage
    {
        get => _targetBrowserMessage;
        private set => SetField(ref _targetBrowserMessage, value);
    }

    public NdTargetBrowserTab SelectedTargetBrowserTab
    {
        get => _selectedTargetBrowserTab;
        set
        {
            if (!SetField(ref _selectedTargetBrowserTab, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTargetBrowserTabIndex));
            _ = EnsureTargetBrowserTabLoadedAsync(value);
        }
    }

    public int SelectedTargetBrowserTabIndex
    {
        get => SelectedTargetBrowserTab switch
        {
            NdTargetBrowserTab.Recent => 0,
            NdTargetBrowserTab.Favorites => 1,
            NdTargetBrowserTab.GoToWorkspace => 2,
            _ => 0
        };
        set
        {
            var tab = value switch
            {
                1 => NdTargetBrowserTab.Favorites,
                2 => NdTargetBrowserTab.GoToWorkspace,
                _ => NdTargetBrowserTab.Recent
            };

            if (SelectedTargetBrowserTab != tab)
            {
                SelectedTargetBrowserTab = tab;
            }
        }
    }

    public string WorkspaceSearchText
    {
        get => _workspaceSearchText;
        set => SetField(ref _workspaceSearchText, value);
    }

    public string WorkspaceLookupStatus
    {
        get => _workspaceLookupStatus;
        private set => SetField(ref _workspaceLookupStatus, value);
    }

    public bool IsBrowseFilterPanelVisible
    {
        get => _isBrowseFilterPanelVisible;
        set
        {
            if (SetField(ref _isBrowseFilterPanelVisible, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool BrowseFilterShowCabFolders
    {
        get => _browseFilterShowCabFolders;
        set
        {
            if (SetField(ref _browseFilterShowCabFolders, value))
            {
                QueueSettingsSave();
                RefreshBrowseRootsForSelectedTab();
            }
        }
    }

    public bool BrowseFilterShowFolders
    {
        get => _browseFilterShowFolders;
        set
        {
            if (SetField(ref _browseFilterShowFolders, value))
            {
                QueueSettingsSave();
                RefreshBrowseRootsForSelectedTab();
            }
        }
    }

    public bool BrowseFilterShowFilters
    {
        get => _browseFilterShowFilters;
        set
        {
            if (SetField(ref _browseFilterShowFilters, value))
            {
                QueueSettingsSave();
                RefreshBrowseRootsForSelectedTab();
            }
        }
    }

    public bool BrowseFilterShowCollabspaces
    {
        get => _browseFilterShowCollabspaces;
        set
        {
            if (SetField(ref _browseFilterShowCollabspaces, value))
            {
                QueueSettingsSave();
                RefreshBrowseRootsForSelectedTab();
            }
        }
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

    public bool CanUseWorkspaceSearchSelection =>
        CanSearchWorkspaceTargets &&
        SelectedWorkspaceSearchTarget is not null;

    public bool CanSearchWorkspaceTargets =>
        CanPickNetDocumentsTarget &&
        _isWorkspaceLookupAvailable;

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
        var snapshot = CaptureTargetBrowserContextSnapshot();
        if (!CanPickNetDocumentsTarget)
        {
            UpdateOnUi(() =>
            {
                _netDocumentsTargetContainers.Clear();
                _recentTargets.Clear();
                _favoriteTargets.Clear();
                _workspaceSearchTargets.Clear();
                SelectedWorkspaceSearchTarget = null;
                _browseRootNodes.Clear();
                SelectedBrowseNode = null;
            });
            _hasLoadedRecentTargets = false;
            _hasLoadedFavoriteTargets = false;
            return;
        }

        IsTargetBrowserBusy = true;
        try
        {
            await LoadTargetBrowserAsync();
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='LoadNetDocumentsTargetContainersAsync' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }

            var sync = RequireSyncService();
            var supportedTargets = await sync.GetSupportedTargetContainersAsync(snapshot.CabinetId);
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='GetSupportedTargetContainersAsync' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }
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
                var path = await sync.ResolveTargetPathAsync(snapshot.CabinetId, _selectedNetDocumentsTarget.Id);
                if (!IsTargetBrowserContextCurrent(snapshot))
                {
                    Trace.WriteLine($"ND-BROWSER stale-drop op='ResolveTargetPathAsync' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                    return;
                }
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
            if (IsTargetBrowserContextCurrent(snapshot))
            {
                RefreshBrowseRootsForSelectedTab();
            }
        }
    }

    public async Task LoadTargetBrowserAsync()
    {
        var snapshot = CaptureTargetBrowserContextSnapshot();
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        if (_workspaceLookupContext is null)
        {
            _workspaceLookupContext = await ResolveWorkspaceLookupContextAsync(snapshot.RepositoryId, snapshot.CabinetId);
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='ResolveWorkspaceLookupContextAsync' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }
            ApplyWorkspaceLookupAvailability(_workspaceLookupContext);
        }

        await EnsureTargetBrowserTabLoadedAsync(SelectedTargetBrowserTab);
    }

    public async Task EnsureTargetBrowserTabLoadedAsync(NdTargetBrowserTab tab)
    {
        var snapshot = CaptureTargetBrowserContextSnapshot();
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        switch (tab)
        {
            case NdTargetBrowserTab.Recent:
                if (!_hasLoadedRecentTargets)
                {
                    await RefreshRecentTargetsAsync();
                }
                break;
            case NdTargetBrowserTab.Favorites:
                if (!_hasLoadedFavoriteTargets)
                {
                    await RefreshFavoriteTargetsAsync();
                }
                break;
            case NdTargetBrowserTab.GoToWorkspace:
                if (_workspaceLookupContext is null)
                {
                    _workspaceLookupContext = await ResolveWorkspaceLookupContextAsync(snapshot.RepositoryId, snapshot.CabinetId);
                    if (!IsTargetBrowserContextCurrent(snapshot))
                    {
                        Trace.WriteLine($"ND-BROWSER stale-drop op='EnsureTargetBrowserTabLoadedAsync.GoToWorkspace' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                        return;
                    }

                    ApplyWorkspaceLookupAvailability(_workspaceLookupContext);
                }
                break;
            default:
                break;
        }

        RefreshBrowseRootsForSelectedTab();
    }

    public async Task RefreshRecentTargetsAsync()
    {
        var snapshot = CaptureTargetBrowserContextSnapshot();
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        if (IsLoadingRecentTargets)
        {
            return;
        }

        var userKey = GetNetDocumentsUserCacheKey();
        var serviceKey = GetNetDocumentsServiceKey();
        var cabinetScope = snapshot.CabinetId;
        IsLoadingRecentTargets = true;
        try
        {
            await _jobStore.InitializeAsync();

            var cachedRecords = await _jobStore.GetNetDocumentsRecentWorkspaceCacheAsync(
                userKey,
                serviceKey,
                cabinetScope);
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='RefreshRecentTargetsAsync.cache' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }
            if (cachedRecords.Count > 0 &&
                DateTime.UtcNow - cachedRecords.Max(r => r.UpdatedUtc) <= WorkspaceCacheTtl)
            {
                var cachedItems = cachedRecords.Select(ToRecentItem).ToList();
                _localRecentTargets = cachedItems.ToList();
                UpdateOnUi(() =>
                {
                    _recentTargets.Clear();
                    foreach (var item in cachedItems)
                    {
                        _recentTargets.Add(NetDocumentsTargetItemView.FromRecent(item));
                    }
                });
                _hasLoadedRecentTargets = true;
                Trace.WriteLine($"ND-CACHE recent source=cache count={cachedItems.Count}");
                return;
            }

            var serverItems = (await RequireSyncService().GetRecentTargetsAsync(cabinetScope))
                .GroupBy(item => NdTargetBrowserLogic.BuildTargetKey(item.Selection), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.LastUsedUtc)
                    .First())
                .ToList();
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='RefreshRecentTargetsAsync.server' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }
            Trace.WriteLine($"ND-CACHE recent source=server count={serverItems.Count}");
            _localRecentTargets = serverItems.ToList();

            var syncedUtc = DateTime.UtcNow;
            var records = serverItems
                .Select(item => ToWorkspaceCacheRecord(userKey, serviceKey, cabinetScope, item.Selection, item.LastUsedUtc, syncedUtc))
                .ToList();
            await _jobStore.ReplaceNetDocumentsRecentWorkspaceCacheAsync(userKey, serviceKey, cabinetScope, records);
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='RefreshRecentTargetsAsync.persist' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }

            UpdateOnUi(() =>
            {
                _recentTargets.Clear();
                foreach (var item in serverItems)
                {
                    _recentTargets.Add(NetDocumentsTargetItemView.FromRecent(item));
                }
            });
            _hasLoadedRecentTargets = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"ND-CACHE recent source=cache-fallback reason={ex.Message}");
            try
            {
                var fallback = await _jobStore.GetNetDocumentsRecentWorkspaceCacheAsync(
                    userKey,
                    serviceKey,
                    cabinetScope);
                if (!IsTargetBrowserContextCurrent(snapshot))
                {
                    Trace.WriteLine($"ND-BROWSER stale-drop op='RefreshRecentTargetsAsync.fallback' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                    return;
                }
                var cachedItems = fallback.Select(ToRecentItem).ToList();
                _localRecentTargets = cachedItems.ToList();
                UpdateOnUi(() =>
                {
                    _recentTargets.Clear();
                    foreach (var item in cachedItems)
                    {
                        _recentTargets.Add(NetDocumentsTargetItemView.FromRecent(item));
                    }
                });
                _hasLoadedRecentTargets = true;
                if (cachedItems.Count > 0)
                {
                    TargetBrowserMessage = "Recent workspaces loaded from cache.";
                }
                else
                {
                    TargetBrowserMessage = $"Unable to load recent workspaces: {ex.Message}";
                }
            }
            catch
            {
                TargetBrowserMessage = $"Unable to load recent workspaces: {ex.Message}";
            }
        }
        finally
        {
            IsLoadingRecentTargets = false;
            if (IsTargetBrowserContextCurrent(snapshot))
            {
                RefreshBrowseRootsForSelectedTab();
            }
        }
    }

    public async Task RefreshFavoriteTargetsAsync()
    {
        var snapshot = CaptureTargetBrowserContextSnapshot();
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        if (IsLoadingFavoriteTargets)
        {
            return;
        }

        var userKey = GetNetDocumentsUserCacheKey();
        var serviceKey = GetNetDocumentsServiceKey();
        var cabinetScope = snapshot.CabinetId;
        IsLoadingFavoriteTargets = true;
        try
        {
            await _jobStore.InitializeAsync();

            var cachedRecords = await _jobStore.GetNetDocumentsFavoriteWorkspaceCacheAsync(
                userKey,
                serviceKey,
                cabinetScope);
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='RefreshFavoriteTargetsAsync.cache' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }
            if (cachedRecords.Count > 0 &&
                DateTime.UtcNow - cachedRecords.Max(r => r.UpdatedUtc) <= WorkspaceCacheTtl)
            {
                var cachedItems = cachedRecords.Select(ToFavoriteItem).ToList();
                _localFavoriteTargets = cachedItems.ToList();
                UpdateOnUi(() =>
                {
                    _favoriteTargets.Clear();
                    foreach (var item in cachedItems)
                    {
                        _favoriteTargets.Add(NetDocumentsTargetItemView.FromFavorite(item));
                    }
                });
                _hasLoadedFavoriteTargets = true;
                Trace.WriteLine($"ND-CACHE favorites source=cache count={cachedItems.Count}");
                return;
            }

            var serverItems = (await RequireSyncService().GetFavoriteTargetsAsync(cabinetScope))
                .GroupBy(item => NdTargetBrowserLogic.BuildTargetKey(item.Selection), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.PinnedUtc)
                    .First())
                .ToList();
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='RefreshFavoriteTargetsAsync.server' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }
            Trace.WriteLine($"ND-CACHE favorites source=server count={serverItems.Count}");
            _localFavoriteTargets = serverItems.ToList();

            var syncedUtc = DateTime.UtcNow;
            var records = serverItems
                .Select(item => ToWorkspaceCacheRecord(userKey, serviceKey, cabinetScope, item.Selection, item.PinnedUtc, syncedUtc))
                .ToList();
            await _jobStore.ReplaceNetDocumentsFavoriteWorkspaceCacheAsync(userKey, serviceKey, cabinetScope, records);
            if (!IsTargetBrowserContextCurrent(snapshot))
            {
                Trace.WriteLine($"ND-BROWSER stale-drop op='RefreshFavoriteTargetsAsync.persist' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                return;
            }

            UpdateOnUi(() =>
            {
                _favoriteTargets.Clear();
                foreach (var item in serverItems)
                {
                    _favoriteTargets.Add(NetDocumentsTargetItemView.FromFavorite(item));
                }
            });
            _hasLoadedFavoriteTargets = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"ND-CACHE favorites source=cache-fallback reason={ex.Message}");
            try
            {
                var fallback = await _jobStore.GetNetDocumentsFavoriteWorkspaceCacheAsync(
                    userKey,
                    serviceKey,
                    cabinetScope);
                if (!IsTargetBrowserContextCurrent(snapshot))
                {
                    Trace.WriteLine($"ND-BROWSER stale-drop op='RefreshFavoriteTargetsAsync.fallback' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                    return;
                }
                var cachedItems = fallback.Select(ToFavoriteItem).ToList();
                _localFavoriteTargets = cachedItems.ToList();
                UpdateOnUi(() =>
                {
                    _favoriteTargets.Clear();
                    foreach (var item in cachedItems)
                    {
                        _favoriteTargets.Add(NetDocumentsTargetItemView.FromFavorite(item));
                    }
                });
                _hasLoadedFavoriteTargets = true;
                if (cachedItems.Count > 0)
                {
                    TargetBrowserMessage = "Favorite workspaces loaded from cache.";
                }
                else
                {
                    TargetBrowserMessage = $"Unable to load favorite workspaces: {ex.Message}";
                }
            }
            catch
            {
                TargetBrowserMessage = $"Unable to load favorite workspaces: {ex.Message}";
            }
        }
        finally
        {
            IsLoadingFavoriteTargets = false;
            if (IsTargetBrowserContextCurrent(snapshot))
            {
                RefreshBrowseRootsForSelectedTab();
            }
        }
    }

    public async Task SearchWorkspacesAsync()
    {
        await SearchWorkspaceTargetsAsync();
    }

    public async Task SearchWorkspaceTargetsAsync()
    {
        var snapshot = CaptureTargetBrowserContextSnapshot();
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        if (SelectedTargetBrowserTab != NdTargetBrowserTab.GoToWorkspace)
        {
            SelectedTargetBrowserTab = NdTargetBrowserTab.GoToWorkspace;
        }

        if (!CanSearchWorkspaceTargets)
        {
            WorkspaceLookupStatus = "This cabinet does not expose workspace lookup attributes; workspace search is disabled.";
            TargetBrowserMessage = WorkspaceLookupStatus;
            RefreshBrowseRootsForSelectedTab();
            return;
        }

        var query = WorkspaceSearchText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            WorkspaceLookupStatus = "Enter workspace search text.";
            TargetBrowserMessage = WorkspaceLookupStatus;
            _workspaceTreeSearchTargets = new List<NetDocumentsWorkspaceTargetResultView>();
            UpdateOnUi(() =>
            {
                _workspaceSearchTargets.Clear();
                SelectedWorkspaceSearchTarget = null;
            });
            RefreshBrowseRootsForSelectedTab();
            return;
        }

        _workspaceSearchCts?.Cancel();
        _workspaceSearchCts?.Dispose();
        _workspaceSearchCts = new CancellationTokenSource();
        var token = _workspaceSearchCts.Token;

        IsTargetBrowserBusy = true;
        try
        {
            var resolved = await SearchWorkspaceTargetsInternalAsync(query, token);
            if (token.IsCancellationRequested || !IsTargetBrowserContextCurrent(snapshot))
            {
                if (!token.IsCancellationRequested)
                {
                    Trace.WriteLine($"ND-BROWSER stale-drop op='SearchWorkspaceTargetsAsync' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
                }
                return;
            }

            _workspaceTreeSearchTargets = resolved.ToList();
            UpdateOnUi(() =>
            {
                _workspaceSearchTargets.Clear();
                foreach (var result in resolved)
                {
                    _workspaceSearchTargets.Add(result);
                }

                SelectedWorkspaceSearchTarget = null;
            });

            WorkspaceLookupStatus = resolved.Count == 0
                ? "No workspaces matched your search."
                : $"Workspace matches: {resolved.Count}.";
            TargetBrowserMessage = WorkspaceLookupStatus;
            Trace.WriteLine($"ND-SEARCH tree-result-count={_workspaceTreeSearchTargets.Count}");
            Trace.WriteLine($"ND-SEARCH ui-result query='{query}' count={resolved.Count}");
            QueueSettingsSave();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search.
        }
        catch (Exception ex)
        {
            WorkspaceLookupStatus = $"Workspace search failed: {ex.Message}";
            TargetBrowserMessage = WorkspaceLookupStatus;
            StatusText = TargetBrowserMessage;
        }
        finally
        {
            IsTargetBrowserBusy = false;
            if (IsTargetBrowserContextCurrent(snapshot))
            {
                RefreshBrowseRootsForSelectedTab();
            }
        }
    }

    public async Task UseSelectedWorkspaceSearchTargetAsync()
    {
        if (SelectedWorkspaceSearchTarget is null)
        {
            WorkspaceLookupStatus = "Select a workspace result first.";
            TargetBrowserMessage = WorkspaceLookupStatus;
            return;
        }

        if (_workspaceLookupContext is not null)
        {
            if (!string.IsNullOrWhiteSpace(SelectedWorkspaceSearchTarget.ParentKey))
            {
                _workspaceLookupContext.ParentKey = SelectedWorkspaceSearchTarget.ParentKey;
            }

            if (!string.IsNullOrWhiteSpace(SelectedWorkspaceSearchTarget.ChildKey))
            {
                _workspaceLookupContext.ChildKey = SelectedWorkspaceSearchTarget.ChildKey;
            }
        }

        await CommitSelectedTargetAsync(
            SelectedWorkspaceSearchTarget.Selection,
            SelectedWorkspaceSearchTarget.PathDisplay,
            _workspaceLookupContext);
    }

    public async Task LoadBrowseRootsAsync()
    {
        if (!CanPickNetDocumentsTarget)
        {
            return;
        }

        RefreshBrowseRootsForSelectedTab();
        await Task.CompletedTask;
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
            var sync = RequireSyncService();
            IReadOnlyList<NdContainerNode> children;
            if (IsCabinetRootBrowseNode(node))
            {
                children = await _browseChildrenCache.GetOrLoadAsync(
                    GetNetDocumentsServiceKey(),
                    SelectedNetDocumentsRepositoryId,
                    SelectedNetDocumentsCabinetId,
                    node.Id,
                    async cancellationToken =>
                        await sync.GetCabinetTopLevelFoldersAsync(
                            SelectedNetDocumentsCabinetId,
                            cancellationToken));
            }
            else
            {
                var scopeId = await ResolveBrowseExpansionScopeIdAsync(node);
                var cacheKey = string.IsNullOrWhiteSpace(scopeId) ? node.Id : scopeId;
                children = await _browseChildrenCache.GetOrLoadAsync(
                    GetNetDocumentsServiceKey(),
                    SelectedNetDocumentsRepositoryId,
                    SelectedNetDocumentsCabinetId,
                    cacheKey,
                    async cancellationToken =>
                        await sync.GetContainerChildrenAsync(
                            SelectedNetDocumentsCabinetId,
                            parentContainerId: scopeId,
                            workspaceId: node.SupportedType == NdTargetType.Workspace ? scopeId : null,
                            preferredType: node.SupportedType,
                            cancellationToken: cancellationToken));
            }

            var mapped = children
                .Select(child => new NetDocumentsBrowseNodeView(
                    child,
                    sourceFlow: node.SourceFlow,
                    metadata: BuildBrowseChildMetadata(child)))
                .Where(ShouldIncludeBrowseNode)
                .ToList();
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

    private bool IsCabinetRootBrowseNode(NetDocumentsBrowseNodeView node)
    {
        return string.Equals(node.TypeRaw, CabinetRootNodeTypeRaw, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ResolveBrowseExpansionScopeIdAsync(NetDocumentsBrowseNodeView node)
    {
        if (string.IsNullOrWhiteSpace(node.Id))
        {
            return string.Empty;
        }

        if (node.SupportedType is not NdTargetType.Workspace and not NdTargetType.Folder)
        {
            return node.Id;
        }

        var cacheKey = $"{GetNetDocumentsServiceKey()}:{SelectedNetDocumentsRepositoryId}:{SelectedNetDocumentsCabinetId}:{node.Id}";
        if (_browseExpansionScopeCache.TryGetValue(cacheKey, out var cached) &&
            !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var resolved = node.Id;
        try
        {
            resolved = await RequireSyncService().ResolveContainerIdForBrowseAsync(node.Id);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                resolved = node.Id;
            }
        }
        catch
        {
            resolved = node.Id;
        }

        _browseExpansionScopeCache[cacheKey] = resolved;
        return resolved;
    }

    public async Task SelectTargetFromRecentAsync()
    {
        if (SelectedRecentTarget is null)
        {
            TargetBrowserMessage = "Select a recent target first.";
            return;
        }

        var selection = CloneSelection(SelectedRecentTarget.Selection);
        selection.SourceFlow = NdTargetSourceFlow.Recent;
        await CommitSelectedTargetAsync(selection, SelectedRecentTarget.PathDisplay);
    }

    public async Task SelectTargetFromFavoriteAsync()
    {
        if (SelectedFavoriteTarget is null)
        {
            TargetBrowserMessage = "Select a favorite target first.";
            return;
        }

        var selection = CloneSelection(SelectedFavoriteTarget.Selection);
        selection.SourceFlow = NdTargetSourceFlow.Favorite;
        await CommitSelectedTargetAsync(selection, SelectedFavoriteTarget.PathDisplay);
    }

    public Task SearchWorkspaceParentsAsync() => SearchWorkspaceTargetsAsync();
    public Task LoadWorkspaceChildrenAsync() => SearchWorkspaceTargetsAsync();
    public Task ResolveWorkspaceAsync() => SearchWorkspaceTargetsAsync();
    public Task ConfirmResolvedWorkspaceAsync() => UseSelectedWorkspaceSearchTargetAsync();
    public Task SelectWorkspaceAsTargetAsync() => UseSelectedWorkspaceSearchTargetAsync();
    public Task LoadSelectedWorkspaceAsync() => UseSelectedWorkspaceSearchTargetAsync();

    private async Task<List<NetDocumentsWorkspaceTargetResultView>> SearchWorkspaceTargetsInternalAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = CaptureTargetBrowserContextSnapshot();
        if (!CanSearchWorkspaceTargets)
        {
            Trace.WriteLine($"ND-SEARCH skipped reason='workspace lookup unavailable' repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
            return new List<NetDocumentsWorkspaceTargetResultView>();
        }
        var results = new List<NetDocumentsWorkspaceTargetResultView>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sync = RequireSyncService();
        var repositoryId = snapshot.RepositoryId;
        var cabinetId = snapshot.CabinetId;
        Trace.WriteLine($"ND-SEARCH start repo='{repositoryId}', cabinet='{cabinetId}', query='{query}', ctxVersion={snapshot.Version}.");
        var fallbackWorkspaces = await sync.SearchWorkspacesAsync(cabinetId, query, 50, cancellationToken);
        Trace.WriteLine($"ND-SEARCH api-fallback-candidates={fallbackWorkspaces.Count}");
        foreach (var item in fallbackWorkspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.WorkspaceId) || !seen.Add(item.WorkspaceId))
            {
                continue;
            }

            var selection = new NdTargetSelection
            {
                Type = NdTargetType.Workspace,
                Id = item.WorkspaceId,
                Name = string.IsNullOrWhiteSpace(item.WorkspaceName) ? item.WorkspaceId : item.WorkspaceName,
                ParentWorkspaceId = item.WorkspaceId,
                Extension = "ndws",
                SourceFlow = NdTargetSourceFlow.LookupWs
            };

            results.Add(new NetDocumentsWorkspaceTargetResultView(selection, string.Empty, "API"));
            if (results.Count >= WorkspaceSearchMaxResults)
            {
                break;
            }
        }
        Trace.WriteLine($"ND-SEARCH api-objects-kept={results.Count}");

        _workspaceLookupContext = await ResolveWorkspaceLookupContextAsync(repositoryId, cabinetId, cancellationToken);
        if (!IsTargetBrowserContextCurrent(snapshot))
        {
            Trace.WriteLine($"ND-BROWSER stale-drop op='SearchWorkspaceTargetsInternalAsync.ResolveWorkspaceLookupContextAsync' version={snapshot.Version} repo='{snapshot.RepositoryId}' cabinet='{snapshot.CabinetId}'.");
            return new List<NetDocumentsWorkspaceTargetResultView>();
        }
        ApplyWorkspaceLookupAvailability(_workspaceLookupContext);
        if (_workspaceLookupContext is not null)
        {
            Trace.WriteLine(
                $"ND-SEARCH lookup-context workspaceAttr={_workspaceLookupContext.WorkspaceAttrNum}('{_workspaceLookupContext.WorkspaceAttrName}') parentAttr={_workspaceLookupContext.ParentAttrNum}('{_workspaceLookupContext.ParentAttrName}') childAttr={_workspaceLookupContext.ChildAttrNum}('{_workspaceLookupContext.ChildAttrName}') parentChild={_workspaceLookupContext.IsParentChild}.");

            var resolveAttempts = 0;
            if (_workspaceLookupContext.IsParentChild && _workspaceLookupContext.ChildAttrNum > 0)
            {
                var workspaceCandidates = await sync.SearchLookupValuesAsync(
                    _workspaceLookupContext.RepositoryId,
                    _workspaceLookupContext.ChildAttrNum,
                    query,
                    top: 30,
                    extendedFiltering: true,
                    cancellationToken: cancellationToken);
                var rankedWorkspaceCandidates = RankLookupCandidatesByTerm(workspaceCandidates, query)
                    .Where(c => !string.IsNullOrWhiteSpace(c.ParentKey))
                    .ToList();
                Trace.WriteLine($"ND-SEARCH workspace-attribute-candidates={workspaceCandidates.Count} ranked={rankedWorkspaceCandidates.Count}.");

                foreach (var child in rankedWorkspaceCandidates.Take(WorkspaceSearchMaxResolveAttempts))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parent = new NdLookupValueItem
                    {
                        Key = child.ParentKey,
                        Description = child.ParentDescription
                    };

                    var resolved = await ResolveWorkspaceCandidateAsync(parent, child, cancellationToken);
                    resolveAttempts++;
                    if (resolved is not null && seen.Add(resolved.Selection.Id))
                    {
                        results.Add(resolved);
                        Trace.WriteLine($"ND-SEARCH resolved workspace id='{resolved.Selection.Id}' name='{resolved.Selection.Name}'.");
                    }

                    if (resolveAttempts >= WorkspaceSearchMaxResolveAttempts || results.Count >= WorkspaceSearchMaxResults)
                    {
                        break;
                    }
                }

                if (resolveAttempts == 0 && _workspaceLookupContext.ParentAttrNum > 0)
                {
                    var parentCandidates = await sync.SearchLookupValuesAsync(
                        _workspaceLookupContext.RepositoryId,
                        _workspaceLookupContext.ParentAttrNum,
                        query,
                        top: 12,
                        extendedFiltering: true,
                        cancellationToken: cancellationToken);
                    var recentParents = await sync.SearchLookupValuesAsync(
                        _workspaceLookupContext.RepositoryId,
                        _workspaceLookupContext.ParentAttrNum,
                        string.Empty,
                        top: 10,
                        extendedFiltering: true,
                        cancellationToken: cancellationToken);
                    if (recentParents.Count > 0)
                    {
                        var mergedParents = new Dictionary<string, NdLookupValueItem>(StringComparer.OrdinalIgnoreCase);
                        foreach (var candidate in parentCandidates)
                        {
                            if (!string.IsNullOrWhiteSpace(candidate.Key))
                            {
                                mergedParents[candidate.Key] = candidate;
                            }
                        }

                        foreach (var recent in recentParents)
                        {
                            if (!string.IsNullOrWhiteSpace(recent.Key) && !mergedParents.ContainsKey(recent.Key))
                            {
                                mergedParents[recent.Key] = recent;
                            }
                        }

                        parentCandidates = mergedParents.Values.ToList();
                    }

                    var rankedParents = RankLookupCandidatesByTerm(parentCandidates, query);
                    Trace.WriteLine($"ND-SEARCH parent-fallback-candidates={parentCandidates.Count} ranked={rankedParents.Count}.");

                    foreach (var parent in rankedParents.Take(WorkspaceSearchMaxParentCandidates))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var children = await sync.GetChildLookupValuesAsync(
                            _workspaceLookupContext.RepositoryId,
                            _workspaceLookupContext.ChildAttrNum,
                            parent.Key,
                            query,
                            top: 25,
                            includeUnfilteredFallback: false,
                            cancellationToken: cancellationToken);
                        var rankedChildren = RankLookupCandidatesByTerm(children, query);
                        foreach (var child in rankedChildren.Take(WorkspaceSearchMaxChildCandidatesPerParent))
                        {
                            var resolved = await ResolveWorkspaceCandidateAsync(parent, child, cancellationToken);
                            resolveAttempts++;
                            if (resolved is not null && seen.Add(resolved.Selection.Id))
                            {
                                results.Add(resolved);
                                Trace.WriteLine($"ND-SEARCH resolved workspace id='{resolved.Selection.Id}' name='{resolved.Selection.Name}' via parent fallback.");
                            }

                            if (resolveAttempts >= WorkspaceSearchMaxResolveAttempts || results.Count >= WorkspaceSearchMaxResults)
                            {
                                break;
                            }
                        }

                        if (resolveAttempts >= WorkspaceSearchMaxResolveAttempts || results.Count >= WorkspaceSearchMaxResults)
                        {
                            break;
                        }
                    }
                }

                if (resolveAttempts == 0 && _workspaceLookupContext.WorkspaceAttrNum > 0)
                {
                    var parentOnlyCandidates = await sync.SearchLookupValuesAsync(
                        _workspaceLookupContext.RepositoryId,
                        _workspaceLookupContext.WorkspaceAttrNum,
                        query,
                        top: 20,
                        extendedFiltering: true,
                        cancellationToken: cancellationToken);
                    var rankedParentOnly = RankLookupCandidatesByTerm(parentOnlyCandidates, query);
                    Trace.WriteLine($"ND-SEARCH workspace-attr-parent-only-fallback candidates={parentOnlyCandidates.Count} ranked={rankedParentOnly.Count}.");

                    foreach (var parent in rankedParentOnly.Take(WorkspaceSearchMaxResolveAttempts))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var resolved = await ResolveWorkspaceCandidateAsync(parent, parent, cancellationToken);
                        resolveAttempts++;
                        if (resolved is not null && seen.Add(resolved.Selection.Id))
                        {
                            results.Add(resolved);
                            Trace.WriteLine($"ND-SEARCH resolved parent-only fallback workspace id='{resolved.Selection.Id}' name='{resolved.Selection.Name}'.");
                        }

                        if (resolveAttempts >= WorkspaceSearchMaxResolveAttempts || results.Count >= WorkspaceSearchMaxResults)
                        {
                            break;
                        }
                    }
                }
            }

            if (!_workspaceLookupContext.IsParentChild && _workspaceLookupContext.ParentAttrNum > 0)
            {
                var parentCandidates = await sync.SearchLookupValuesAsync(
                    _workspaceLookupContext.RepositoryId,
                    _workspaceLookupContext.ParentAttrNum,
                    query,
                    top: 20,
                    extendedFiltering: true,
                    cancellationToken: cancellationToken);
                var rankedParents = RankLookupCandidatesByTerm(parentCandidates, query);
                Trace.WriteLine($"ND-SEARCH parent-only-candidates={parentCandidates.Count} ranked={rankedParents.Count}");

                foreach (var parent in rankedParents.Take(WorkspaceSearchMaxResolveAttempts))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resolved = await ResolveWorkspaceCandidateAsync(parent, parent, cancellationToken);
                    resolveAttempts++;
                    if (resolved is not null && seen.Add(resolved.Selection.Id))
                    {
                        results.Add(resolved);
                        Trace.WriteLine($"ND-SEARCH resolved parent-only workspace id='{resolved.Selection.Id}' name='{resolved.Selection.Name}'.");
                    }

                    if (resolveAttempts >= WorkspaceSearchMaxResolveAttempts || results.Count >= WorkspaceSearchMaxResults)
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            Trace.WriteLine("ND-SEARCH lookup-context unavailable; skipping lookup enrichment.");
        }

        Trace.WriteLine($"ND-SEARCH final-result-count={results.Count}");

        var strictResults = FilterWorkspaceResultsByQuery(results, query);
        Trace.WriteLine($"ND-SEARCH strict-filter query='{query}' kept={strictResults.Count} dropped={results.Count - strictResults.Count}.");

        return strictResults
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(WorkspaceSearchMaxResults)
            .ToList();
    }

    private static List<NetDocumentsWorkspaceTargetResultView> FilterWorkspaceResultsByQuery(
        List<NetDocumentsWorkspaceTargetResultView> results,
        string? query)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return results;
        }

        return results
            .Where(item =>
                (!string.IsNullOrWhiteSpace(item.ParentKey) &&
                 item.ParentKey.Contains(normalized, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(item.ParentDescription) &&
                 item.ParentDescription.Contains(normalized, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(item.ChildKey) &&
                 item.ChildKey.Contains(normalized, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(item.ChildDescription) &&
                 item.ChildDescription.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private async Task<NetDocumentsWorkspaceTargetResultView?> ResolveWorkspaceFromLookupAsync(string parentKey, string childKey, CancellationToken cancellationToken)
    {
        if (_workspaceLookupContext is null)
        {
            return null;
        }

        var sync = RequireSyncService();
        var envId = await sync.ResolveWorkspaceEnvIdAsync(_workspaceLookupContext.CabinetId, parentKey, childKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(envId))
        {
            return null;
        }

        var container = await sync.GetContainerInfoAsync(envId, cancellationToken);
        if (container is null)
        {
            return null;
        }

        var extension = string.IsNullOrWhiteSpace(container.Extension) ? container.TypeRaw : container.Extension;
        var resolvedType = NdTargetBrowserLogic.NormalizeSupportedType(extension, hasWorkspaceIdHint: true);
        if (resolvedType != NdTargetType.Workspace)
        {
            return null;
        }

        var path = $"{parentKey}/{childKey}";

        _workspaceLookupContext.ParentKey = parentKey;
        _workspaceLookupContext.ChildKey = childKey;

        var selection = new NdTargetSelection
        {
            Type = NdTargetType.Workspace,
            Id = envId,
            Name = container.Name,
            ParentWorkspaceId = container.ParentWorkspaceId,
            Extension = extension,
            SourceFlow = NdTargetSourceFlow.LookupWs
        };

        return new NetDocumentsWorkspaceTargetResultView(selection, path, "Lookup", parentKey, string.Empty, childKey, string.Empty);
    }

    private async Task<NetDocumentsWorkspaceTargetResultView?> ResolveWorkspaceCandidateAsync(
        NdLookupValueItem parent,
        NdLookupValueItem child,
        CancellationToken cancellationToken)
    {
        var pairKey = $"{parent.Key}|{child.Key}";
        if (_workspaceLookupInvalidPairCache.ContainsKey(pairKey))
        {
            Trace.WriteLine($"ND-SEARCH skipped invalid wsurl pair parent='{parent.Key}' child='{child.Key}'");
            return null;
        }

        if (_workspaceLookupPairCache.TryGetValue(pairKey, out var cached))
        {
            Trace.WriteLine($"ND-SEARCH lookup-cache hit parent='{parent.Key}' child='{child.Key}' hasResult={(cached is not null)}.");
            return cached;
        }

        try
        {
            var resolved = await ResolveWorkspaceFromLookupAsync(parent.Key, child.Key, cancellationToken);
            if (resolved is not null)
            {
                resolved = new NetDocumentsWorkspaceTargetResultView(
                    resolved.Selection,
                    $"{DisplayLookup(parent.Key, parent.Description)} / {DisplayLookup(child.Key, child.Description)}",
                    resolved.Source,
                    parent.Key,
                    parent.Description,
                    child.Key,
                    child.Description);
            }

            _workspaceLookupPairCache[pairKey] = resolved;
            return resolved;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("400 Bad Request", StringComparison.OrdinalIgnoreCase))
            {
                _workspaceLookupInvalidPairCache[pairKey] = true;
                Trace.WriteLine($"ND-SEARCH marked invalid wsurl pair parent='{parent.Key}' child='{child.Key}'.");
            }

            Trace.WriteLine($"ND-SEARCH lookup-resolve failed parent='{parent.Key}' child='{child.Key}' error='{ex.Message}'.");
            _workspaceLookupPairCache[pairKey] = null;
            return null;
        }
    }

    private static string DisplayLookup(string key, string description)
    {
        return string.IsNullOrWhiteSpace(description)
            ? key
            : $"{description} ({key})";
    }

    private static List<NdLookupValueItem> RankLookupCandidatesByTerm(
        IReadOnlyList<NdLookupValueItem> values,
        string? term)
    {
        if (values.Count == 0)
        {
            return new List<NdLookupValueItem>();
        }

        var normalized = (term ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return values.ToList();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranked = new List<NdLookupValueItem>();

        var exactKey = values
            .Where(v => !string.IsNullOrWhiteSpace(v.Key) &&
                        string.Equals(v.Key, normalized, StringComparison.OrdinalIgnoreCase));
        foreach (var item in exactKey)
        {
            if (seen.Add(item.Key))
            {
                ranked.Add(item);
            }
        }

        var descriptionContains = values
            .Where(v => !string.IsNullOrWhiteSpace(v.Description) &&
                        v.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        foreach (var item in descriptionContains)
        {
            if (seen.Add(item.Key))
            {
                ranked.Add(item);
            }
        }

        var keyContains = values
            .Where(v => !string.IsNullOrWhiteSpace(v.Key) &&
                        v.Key.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        foreach (var item in keyContains)
        {
            if (seen.Add(item.Key))
            {
                ranked.Add(item);
            }
        }

        return ranked;
    }

    private string GetNetDocumentsServiceKey()
    {
        return GetApiBaseUrl().TrimEnd('/');
    }

    private string GetNetDocumentsUserCacheKey()
    {
        if (!string.IsNullOrWhiteSpace(_netDocumentsCurrentUserId))
        {
            return _netDocumentsCurrentUserId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(NetDocumentsConnectedUser))
        {
            return NetDocumentsConnectedUser.Trim();
        }

        // TODO: replace fallback when a stable account identifier is always available.
        return "default-user";
    }

    private static NetDocumentsWorkspaceCacheRecord ToWorkspaceCacheRecord(
        string userKey,
        string serviceKey,
        string cabinetScope,
        NdTargetSelection selection,
        DateTime rowTimestampUtc,
        DateTime syncedUtc)
    {
        return new NetDocumentsWorkspaceCacheRecord(
            userKey,
            serviceKey,
            cabinetScope ?? string.Empty,
            selection.Id,
            selection.Name,
            selection.Type.ToString(),
            selection.ParentWorkspaceId,
            selection.Extension,
            selection.Name,
            syncedUtc == default ? rowTimestampUtc : syncedUtc);
    }

    private static NdTargetRecentItem ToRecentItem(NetDocumentsWorkspaceCacheRecord record)
    {
        return new NdTargetRecentItem
        {
            Selection = new NdTargetSelection
            {
                Id = record.WorkspaceId,
                Name = record.WorkspaceName,
                Type = ParseTargetType(record.TargetType),
                ParentWorkspaceId = record.ParentWorkspaceId,
                Extension = record.Extension,
                SourceFlow = NdTargetSourceFlow.Recent
            },
            LastUsedUtc = record.UpdatedUtc,
            Source = NdTargetSource.Server
        };
    }

    private static NdTargetFavoriteItem ToFavoriteItem(NetDocumentsWorkspaceCacheRecord record)
    {
        return new NdTargetFavoriteItem
        {
            Selection = new NdTargetSelection
            {
                Id = record.WorkspaceId,
                Name = record.WorkspaceName,
                Type = ParseTargetType(record.TargetType),
                ParentWorkspaceId = record.ParentWorkspaceId,
                Extension = record.Extension,
                SourceFlow = NdTargetSourceFlow.Favorite
            },
            PinnedUtc = record.UpdatedUtc,
            Source = NdTargetSource.Server
        };
    }

    private void RefreshBrowseRootsForSelectedTab()
    {
        if (!CanPickNetDocumentsTarget)
        {
            UpdateOnUi(() =>
            {
                _browseRootNodes.Clear();
                SelectedBrowseNode = null;
            });
            return;
        }

        IReadOnlyList<NetDocumentsBrowseNodeView> roots = SelectedTargetBrowserTab switch
        {
            NdTargetBrowserTab.Recent => BuildBrowseRootsFromRecentTargets(),
            NdTargetBrowserTab.Favorites => BuildBrowseRootsFromFavoriteTargets(),
            NdTargetBrowserTab.GoToWorkspace => BuildBrowseRootsFromWorkspaceSearchTargets(),
            _ => Array.Empty<NetDocumentsBrowseNodeView>()
        };
        var cabinetRoot = BuildCabinetRootBrowseNode();
        if (cabinetRoot is not null)
        {
            roots = new[] { cabinetRoot }
                .Concat(roots)
                .ToList();
        }
        roots = roots.Where(ShouldIncludeBrowseNode).ToList();
        Trace.WriteLine(
            $"ND-BROWSER roots-refresh tab={SelectedTargetBrowserTab} roots={roots.Count} " +
            $"workspaceTree={_workspaceTreeSearchTargets.Count} recent={_recentTargets.Count} favorites={_favoriteTargets.Count}");

        UpdateOnUi(() =>
        {
            _browseRootNodes.Clear();
            foreach (var root in roots)
            {
                _browseRootNodes.Add(root);
            }

            SelectedBrowseNode = null;
        });
    }

    private List<NetDocumentsBrowseNodeView> BuildBrowseRootsFromRecentTargets()
    {
        return _recentTargets
            .Select(item =>
                new NetDocumentsBrowseNodeView(
                    CreateBrowseNodeFromSelection(item.Selection, item.PathDisplay),
                    sourceFlow: NdTargetSourceFlow.Recent,
                    metadata: $"{item.TypeDisplay} \u00B7 {item.SourceDisplay} \u00B7 {item.TimestampDisplay}"))
            .ToList();
    }

    private List<NetDocumentsBrowseNodeView> BuildBrowseRootsFromFavoriteTargets()
    {
        return _favoriteTargets
            .Select(item =>
                new NetDocumentsBrowseNodeView(
                    CreateBrowseNodeFromSelection(item.Selection, item.PathDisplay),
                    sourceFlow: NdTargetSourceFlow.Favorite,
                    metadata: $"{item.TypeDisplay} \u00B7 {item.SourceDisplay} \u00B7 {item.TimestampDisplay}"))
            .ToList();
    }

    private List<NetDocumentsBrowseNodeView> BuildBrowseRootsFromWorkspaceSearchTargets()
    {
        return _workspaceTreeSearchTargets
            .Select(item =>
            {
                var keyContext = string.IsNullOrWhiteSpace(item.ParentKey) && string.IsNullOrWhiteSpace(item.ChildKey)
                    ? string.Empty
                    : $" \u00B7 {item.ParentKey}/{item.ChildKey}";
                return new NetDocumentsBrowseNodeView(
                    CreateBrowseNodeFromSelection(item.Selection, item.PathDisplay),
                    sourceFlow: NdTargetSourceFlow.LookupWs,
                    metadata: $"{item.TypeDisplay} \u00B7 {item.Source}{keyContext}");
            })
            .ToList();
    }

    private NetDocumentsBrowseNodeView? BuildCabinetRootBrowseNode()
    {
        if (!CanPickNetDocumentsTarget)
        {
            return null;
        }

        var selectedCabinet = _netDocumentsCabinets
            .FirstOrDefault(c => string.Equals(c.CabinetId, SelectedNetDocumentsCabinetId, StringComparison.OrdinalIgnoreCase));
        var cabinetName = string.IsNullOrWhiteSpace(selectedCabinet?.CabinetName)
            ? SelectedNetDocumentsCabinetId
            : selectedCabinet!.CabinetName;
        var node = new NdContainerNode
        {
            Id = $"cabinet-root:{SelectedNetDocumentsCabinetId}",
            Name = cabinetName,
            TypeRaw = CabinetRootNodeTypeRaw,
            Extension = string.Empty,
            ParentId = string.Empty,
            ParentWorkspaceId = null,
            PathDisplay = cabinetName,
            SupportedType = null,
            IsSelectable = false,
            UnsupportedReason = "Expand to browse top-level cabinet folders.",
            HasChildren = true,
            ChildrenLoadState = NdChildrenLoadState.NotLoaded
        };

        return new NetDocumentsBrowseNodeView(
            node,
            sourceFlow: NdTargetSourceFlow.Browse,
            metadata: "Cabinet \u00B7 Top-level folders");
    }

    private static NdContainerNode CreateBrowseNodeFromSelection(NdTargetSelection selection, string pathDisplay)
    {
        return new NdContainerNode
        {
            Id = selection.Id,
            Name = string.IsNullOrWhiteSpace(selection.Name) ? selection.Id : selection.Name,
            TypeRaw = string.IsNullOrWhiteSpace(selection.Extension) ? selection.Type.ToString() : selection.Extension,
            Extension = selection.Extension,
            ParentId = string.Empty,
            ParentWorkspaceId = selection.ParentWorkspaceId,
            PathDisplay = string.IsNullOrWhiteSpace(pathDisplay) ? selection.Name : pathDisplay,
            SupportedType = selection.Type,
            IsSelectable = true,
            UnsupportedReason = string.Empty,
            HasChildren = selection.Type != NdTargetType.WorkspaceFilter,
            ChildrenLoadState = NdChildrenLoadState.NotLoaded
        };
    }

    private static string BuildBrowseChildMetadata(NdContainerNode node)
    {
        var typeDisplay = node.SupportedType.HasValue
            ? NdTargetBrowserLogic.ResolveTypeDisplay(node.SupportedType.Value, node.Id)
            : "Container";

        if (!string.IsNullOrWhiteSpace(node.PathDisplay) &&
            !string.Equals(node.PathDisplay, node.Name, StringComparison.OrdinalIgnoreCase))
        {
            return $"{typeDisplay} \u00B7 {node.PathDisplay}";
        }

        return typeDisplay;
    }

    private bool ShouldIncludeBrowseNode(NetDocumentsBrowseNodeView node)
    {
        if (node.IsPlaceholder)
        {
            return true;
        }

        if (IsCabinetRootBrowseNode(node))
        {
            return BrowseFilterShowCabFolders;
        }

        if (node.SupportedType == NdTargetType.Workspace)
        {
            return BrowseFilterShowCollabspaces;
        }

        if (node.SupportedType == NdTargetType.WorkspaceFilter)
        {
            return BrowseFilterShowFilters;
        }

        if (node.SupportedType == NdTargetType.Folder)
        {
            return NdTargetBrowserLogic.IsCollabspaceIdentifier(node.Id)
                ? BrowseFilterShowCollabspaces
                : BrowseFilterShowFolders;
        }

        return true;
    }

    private static NdTargetType ParseTargetType(string raw)
    {
        return Enum.TryParse<NdTargetType>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : NdTargetType.Workspace;
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

        var selection = NdTargetBrowserLogic.CreateSelectionFromContainerNode(
            SelectedBrowseNode.ToNodeModel(),
            SelectedBrowseNode.SourceFlow);

        await CommitSelectedTargetAsync(selection, SelectedBrowseNode.PathDisplay);
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
        SelectedNetDocumentsTargetTypeDisplay = NdTargetBrowserLogic.ResolveTypeDisplay(selection.Type, selection.Id);

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
        TryApplyWorkspaceLookupKeysFromSelection(selection, SelectedNetDocumentsTargetPath);
        TargetBrowserMessage = $"Selected target: {SelectedNetDocumentsTargetName}.";
        Trace.WriteLine($"NetDocuments target browser: selected target type={selection.Type}, id={selection.Id}, name={selection.Name}, flow={selection.SourceFlow}");
        OnPropertyChanged(nameof(IsSelectedTargetFavorite));
        OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
        OnPropertyChanged(nameof(CanContinueToReviewScope));
        QueueSettingsSave();
    }

    private async Task CommitSelectedTargetAsync(
        NdTargetSelection selection,
        string? pathDisplay,
        WorkspaceLookupContext? contextOverride = null)
    {
        await SetSelectedTargetAsync(selection, pathDisplay);
        await SyncSelectedTargetProfileSnapshotAsync();
        await RefreshReviewScopeNetDocumentsAsync();
        TargetBrowserMessage = $"Selected target: {SelectedNetDocumentsTargetName}. Profile metadata refreshed.";

        var context = contextOverride ?? _workspaceLookupContext;
        if (selection.Type != NdTargetType.Workspace ||
            context is null ||
            string.IsNullOrWhiteSpace(context.ParentKey) ||
            string.IsNullOrWhiteSpace(context.ChildKey))
        {
            return;
        }

        try
        {
            await RequireSyncService().UpdateRecentLookupSelectionAsync(
                context.RepositoryId,
                context.ChildAttrNum,
                context.ChildKey,
                context.ParentAttrNum,
                context.ParentKey);
        }
        catch
        {
            // Ignore optional recent-update failures.
        }
    }

    private async Task AutoCommitRecentTargetSelectionAsync(NetDocumentsTargetItemView selected)
    {
        var cts = BeginAutoTargetSelection();
        try
        {
            await Task.Delay(150, cts.Token);
            if (cts.Token.IsCancellationRequested || !CanPickNetDocumentsTarget)
            {
                return;
            }

            var selection = CloneSelection(selected.Selection);
            selection.SourceFlow = NdTargetSourceFlow.Recent;
            await CommitSelectedTargetAsync(selection, selected.PathDisplay);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer selection.
        }
        finally
        {
            EndAutoTargetSelection(cts);
        }
    }

    private async Task AutoCommitFavoriteTargetSelectionAsync(NetDocumentsTargetItemView selected)
    {
        var cts = BeginAutoTargetSelection();
        try
        {
            await Task.Delay(150, cts.Token);
            if (cts.Token.IsCancellationRequested || !CanPickNetDocumentsTarget)
            {
                return;
            }

            var selection = CloneSelection(selected.Selection);
            selection.SourceFlow = NdTargetSourceFlow.Favorite;
            await CommitSelectedTargetAsync(selection, selected.PathDisplay);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer selection.
        }
        finally
        {
            EndAutoTargetSelection(cts);
        }
    }

    private async Task AutoCommitWorkspaceTargetSelectionAsync(NetDocumentsWorkspaceTargetResultView selected)
    {
        var cts = BeginAutoTargetSelection();
        try
        {
            await Task.Delay(150, cts.Token);
            if (cts.Token.IsCancellationRequested || !CanPickNetDocumentsTarget)
            {
                return;
            }

            if (_workspaceLookupContext is not null)
            {
                if (!string.IsNullOrWhiteSpace(selected.ParentKey))
                {
                    _workspaceLookupContext.ParentKey = selected.ParentKey;
                }

                if (!string.IsNullOrWhiteSpace(selected.ChildKey))
                {
                    _workspaceLookupContext.ChildKey = selected.ChildKey;
                }
            }

            await CommitSelectedTargetAsync(selected.Selection, selected.PathDisplay, _workspaceLookupContext);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer selection.
        }
        finally
        {
            EndAutoTargetSelection(cts);
        }
    }

    private CancellationTokenSource BeginAutoTargetSelection()
    {
        _autoTargetSelectionCts?.Cancel();
        _autoTargetSelectionCts?.Dispose();
        _autoTargetSelectionCts = new CancellationTokenSource();
        _isAutoSelectingTarget = true;
        return _autoTargetSelectionCts;
    }

    private void EndAutoTargetSelection(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_autoTargetSelectionCts, cts))
        {
            cts.Dispose();
            return;
        }

        _isAutoSelectingTarget = false;
        _autoTargetSelectionCts = null;
        cts.Dispose();
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
            var hasLookupDefaults =
                _workspaceLookupContext is not null &&
                !string.IsNullOrWhiteSpace(_workspaceLookupContext.ParentKey);
            var allowLookupDefaults =
                _selectedNetDocumentsTarget.Type == NdTargetType.Workspace ||
                (_selectedNetDocumentsTarget.Type == NdTargetType.Folder &&
                 _selectedNetDocumentsTarget.SourceFlow == NdTargetSourceFlow.LookupWs);
            var lookupContext = hasLookupDefaults && allowLookupDefaults
                ? _workspaceLookupContext
                : null;
            snapshot = await RequireSyncService().GetTargetProfileSnapshotAsync(
                SelectedNetDocumentsCabinetId,
                SelectedNetDocumentsRepositoryId,
                _selectedNetDocumentsTarget,
                lookupContext);
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

    private void TryApplyWorkspaceLookupKeysFromSelection(NdTargetSelection selection, string? pathDisplay)
    {
        if (_workspaceLookupContext is null ||
            selection.Type != NdTargetType.Workspace)
        {
            return;
        }

        if (TryExtractWorkspaceKeys(pathDisplay, out var pathParentKey, out var pathChildKey))
        {
            _workspaceLookupContext.ParentKey = pathParentKey;
            _workspaceLookupContext.ChildKey = pathChildKey;
            return;
        }

        if (TryExtractWorkspaceKeys(selection.Name, out var nameParentKey, out var nameChildKey))
        {
            _workspaceLookupContext.ParentKey = nameParentKey;
            _workspaceLookupContext.ChildKey = nameChildKey;
        }
    }

    private static bool TryExtractWorkspaceKeys(string? text, out string parentKey, out string childKey)
    {
        parentKey = string.Empty;
        childKey = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var pathPattern = Regex.Match(
            text,
            @"\((?<parent>[A-Za-z0-9_-]+)\)\s*/\s*.*\((?<child>[A-Za-z0-9_-]+)\)",
            RegexOptions.CultureInvariant);
        if (pathPattern.Success)
        {
            parentKey = pathPattern.Groups["parent"].Value;
            childKey = pathPattern.Groups["child"].Value;
            return !string.IsNullOrWhiteSpace(parentKey) && !string.IsNullOrWhiteSpace(childKey);
        }

        var dottedPattern = Regex.Match(
            text,
            @"\b(?<parent>[A-Za-z0-9_-]+)\.(?<child>[A-Za-z0-9_-]+)\b",
            RegexOptions.CultureInvariant);
        if (dottedPattern.Success)
        {
            parentKey = dottedPattern.Groups["parent"].Value;
            childKey = dottedPattern.Groups["child"].Value;
            return !string.IsNullOrWhiteSpace(parentKey) && !string.IsNullOrWhiteSpace(childKey);
        }

        return false;
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

            OnPropertyChanged(nameof(HasReviewEffectiveDefaults));
        });
    }

    private async Task<WorkspaceLookupContext?> ResolveWorkspaceLookupContextAsync(CancellationToken cancellationToken = default)
    {
        return await ResolveWorkspaceLookupContextAsync(
            SelectedNetDocumentsRepositoryId,
            SelectedNetDocumentsCabinetId,
            cancellationToken);
    }

    private async Task<WorkspaceLookupContext?> ResolveWorkspaceLookupContextAsync(
        string repositoryId,
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || string.IsNullOrWhiteSpace(cabinetId))
        {
            return null;
        }

        await _jobStore.InitializeAsync(cancellationToken);
        var attributes = await _jobStore.GetNetDocumentsAttributesAsync(cabinetId, cancellationToken);
        if (attributes.Count == 0)
        {
            try
            {
                Trace.WriteLine(
                    $"ND-SEARCH lookup-context bootstrap: syncing attributes for repo='{repositoryId}' cabinet='{cabinetId}'.");
                await RequireSyncService().SyncCabinetAttributesAsync(
                    cabinetId,
                    repositoryId,
                    cancellationToken);
                attributes = await _jobStore.GetNetDocumentsAttributesAsync(cabinetId, cancellationToken);
                Trace.WriteLine($"ND-SEARCH lookup-context bootstrap: synced attributes count={attributes.Count}.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ND-SEARCH lookup-context bootstrap failed: {ex.Message}");
            }
        }
        var selectedCabinet = _netDocumentsCabinets
            .FirstOrDefault(c => string.Equals(c.CabinetId, cabinetId, StringComparison.OrdinalIgnoreCase));

        var workspaceAttrNum = selectedCabinet?.WorkspaceAttributeNum;
        var workspaceAttrName = selectedCabinet?.WorkspacePluralName ?? string.Empty;
        var allowFileInWorkspaces = selectedCabinet?.AllowFileInWorkspaces;

        var lookupAttributes = attributes
            .Where(a => a.IsLookup)
            .ToList();
        if (lookupAttributes.Count == 0 && attributes.Count > 0)
        {
            try
            {
                Trace.WriteLine(
                    $"ND-SEARCH lookup-context refresh: no lookup flags found; re-syncing attributes for repo='{repositoryId}' cabinet='{cabinetId}'.");
                await RequireSyncService().SyncCabinetAttributesAsync(
                    cabinetId,
                    repositoryId,
                    cancellationToken);
                attributes = await _jobStore.GetNetDocumentsAttributesAsync(cabinetId, cancellationToken);
                lookupAttributes = attributes.Where(a => a.IsLookup).ToList();
                Trace.WriteLine($"ND-SEARCH lookup-context refresh: lookup attributes after re-sync={lookupAttributes.Count}.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ND-SEARCH lookup-context refresh failed: {ex.Message}");
            }
        }

        if ((!workspaceAttrNum.HasValue || workspaceAttrNum.Value <= 0) && attributes.Count > 0)
        {
            var relationshipChild = attributes
                .Where(a => a.ParentAttributeNum.HasValue && a.ParentAttributeNum.Value > 0)
                .OrderByDescending(a => NameContains(a.Name, "matter"))
                .ThenBy(a => a.AttributeNum)
                .FirstOrDefault();
            if (relationshipChild is not null)
            {
                workspaceAttrNum = relationshipChild.AttributeNum;
                if (string.IsNullOrWhiteSpace(workspaceAttrName))
                {
                    workspaceAttrName = relationshipChild.Name;
                }

                Trace.WriteLine(
                    $"ND-SEARCH lookup-context inferred workspace attribute from parent-child metadata childAttr={relationshipChild.AttributeNum} parentAttr={relationshipChild.ParentAttributeNum}.");
            }
        }
        if ((!workspaceAttrNum.HasValue || workspaceAttrNum.Value <= 0) && attributes.Count > 0)
        {
            var namedWorkspace = attributes
                .Select(a => new
                {
                    Attribute = a,
                    Score = NameContains(a.Name, "matter") * 6 +
                            NameContains(a.Name, "workspace") * 4 +
                            NameContains(a.Name, "project") * 2
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Attribute.AttributeNum)
                .Select(x => x.Attribute)
                .FirstOrDefault();
            if (namedWorkspace is not null)
            {
                workspaceAttrNum = namedWorkspace.AttributeNum;
                if (string.IsNullOrWhiteSpace(workspaceAttrName))
                {
                    workspaceAttrName = namedWorkspace.Name;
                }

                Trace.WriteLine(
                    $"ND-SEARCH lookup-context inferred workspace attribute by name childAttr={namedWorkspace.AttributeNum} name='{namedWorkspace.Name}'.");
            }
        }

        if (lookupAttributes.Count == 0)
        {
            if (!workspaceAttrNum.HasValue || workspaceAttrNum.Value <= 0)
            {
                Trace.WriteLine("ND-SEARCH lookup-context unavailable: synced metadata did not identify workspace attribute.");
                return null;
            }

            var inferredParentAttrNum = workspaceAttrNum.Value > 1 ? workspaceAttrNum.Value - 1 : 0;
            var inferredIsParentChild = inferredParentAttrNum > 0;
            var inferredParentName = inferredIsParentChild
                ? attributes
                    .Select(a => new
                    {
                        Attribute = a,
                        Score = NameContains(a.Name, "client") * 6 +
                                NameContains(a.Name, "customer") * 4 +
                                NameContains(a.Name, "parent") * 2
                    })
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => Math.Abs(x.Attribute.AttributeNum - workspaceAttrNum.Value))
                    .ThenBy(x => x.Attribute.AttributeNum)
                    .Select(x => x.Attribute.Name)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Parent"
                : workspaceAttrName;
            var inferredChildName = inferredIsParentChild ? workspaceAttrName : string.Empty;

            Trace.WriteLine(
                $"ND-SEARCH lookup-context fallback from cabinet metadata workspaceAttr={workspaceAttrNum.Value} parentAttr={inferredParentAttrNum} parentChild={inferredIsParentChild}.");

            return new WorkspaceLookupContext
            {
                RepositoryId = repositoryId,
                CabinetId = cabinetId,
                WorkspaceEnabled = true,
                WorkspaceAttrNum = workspaceAttrNum.Value,
                WorkspaceAttrName = workspaceAttrName,
                IsParentChild = inferredIsParentChild,
                ParentAttrNum = inferredIsParentChild ? inferredParentAttrNum : workspaceAttrNum.Value,
                ChildAttrNum = inferredIsParentChild ? workspaceAttrNum.Value : 0,
                ParentAttrName = inferredParentName,
                ChildAttrName = inferredChildName,
                AllowFileInWorkspaces = allowFileInWorkspaces,
                ParentKey = _workspaceLookupContext?.ParentKey ?? string.Empty,
                ChildKey = _workspaceLookupContext?.ChildKey ?? string.Empty
            };
        }
        if (!workspaceAttrNum.HasValue || workspaceAttrNum.Value <= 0)
        {
            var fallbackWorkspace = lookupAttributes
                .OrderByDescending(a => NameContains(a.Name, "workspace"))
                .ThenByDescending(a => NameContains(a.Name, "matter"))
                .ThenBy(a => a.AttributeNum)
                .FirstOrDefault();
            workspaceAttrNum = fallbackWorkspace?.AttributeNum;
            if (string.IsNullOrWhiteSpace(workspaceAttrName))
            {
                workspaceAttrName = fallbackWorkspace?.Name ?? string.Empty;
            }
        }

        if (!workspaceAttrNum.HasValue || workspaceAttrNum.Value <= 0)
        {
            Trace.WriteLine("ND-SEARCH lookup-context unavailable: workspace attribute number not detected.");
            return null;
        }

        var workspaceAttribute = lookupAttributes
            .FirstOrDefault(a => a.AttributeNum == workspaceAttrNum.Value);

        var parentAttrNum = workspaceAttribute?.ParentAttributeNum ?? 0;
        if (parentAttrNum <= 0)
        {
            var namedParent = attributes
                .Select(a => new
                {
                    Attribute = a,
                    Score = NameContains(a.Name, "client") * 6 +
                            NameContains(a.Name, "customer") * 4 +
                            NameContains(a.Name, "parent") * 2
                })
                .Where(x => x.Attribute.AttributeNum != workspaceAttrNum.Value && x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => Math.Abs(x.Attribute.AttributeNum - workspaceAttrNum.Value))
                .ThenBy(x => x.Attribute.AttributeNum)
                .Select(x => x.Attribute)
                .FirstOrDefault();
            if (namedParent is not null)
            {
                parentAttrNum = namedParent.AttributeNum;
            }
        }

        if (parentAttrNum <= 0 && workspaceAttrNum.Value > 1)
        {
            parentAttrNum = workspaceAttrNum.Value - 1;
        }
        var childAttrNum = workspaceAttrNum.Value;
        var isParentChild = parentAttrNum > 0;

        var parentAttrName = string.Empty;
        var childAttrName = workspaceAttribute?.Name ?? workspaceAttrName;

        if (isParentChild)
        {
            parentAttrName = attributes
                .FirstOrDefault(a => a.AttributeNum == parentAttrNum)?.Name ?? string.Empty;
        }
        else
        {
            // Parent-only cabinet workspace structure.
            parentAttrNum = workspaceAttrNum.Value;
            childAttrNum = 0;
            parentAttrName = workspaceAttribute?.Name ?? workspaceAttrName;
            childAttrName = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(workspaceAttrName))
        {
            workspaceAttrName = isParentChild ? childAttrName : parentAttrName;
        }

        return new WorkspaceLookupContext
        {
            RepositoryId = repositoryId,
            CabinetId = cabinetId,
            WorkspaceEnabled = true,
            WorkspaceAttrNum = workspaceAttrNum.Value,
            WorkspaceAttrName = workspaceAttrName,
            IsParentChild = isParentChild,
            ParentAttrNum = parentAttrNum,
            ChildAttrNum = childAttrNum,
            ParentAttrName = parentAttrName,
            ChildAttrName = childAttrName,
            AllowFileInWorkspaces = allowFileInWorkspaces,
            ParentKey = _workspaceLookupContext?.ParentKey ?? string.Empty,
            ChildKey = _workspaceLookupContext?.ChildKey ?? string.Empty
        };
    }

    private (long Version, string RepositoryId, string CabinetId) CaptureTargetBrowserContextSnapshot()
    {
        return (
            _targetBrowserContextVersion,
            SelectedNetDocumentsRepositoryId ?? string.Empty,
            SelectedNetDocumentsCabinetId ?? string.Empty);
    }

    private bool IsTargetBrowserContextCurrent((long Version, string RepositoryId, string CabinetId) snapshot)
    {
        return snapshot.Version == _targetBrowserContextVersion &&
               string.Equals(snapshot.RepositoryId, SelectedNetDocumentsRepositoryId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(snapshot.CabinetId, SelectedNetDocumentsCabinetId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private void InvalidateTargetBrowserContext(string reason)
    {
        _targetBrowserContextVersion++;
        _browseChildrenCache.InvalidateAll();
        _browseExpansionScopeCache.Clear();
        _workspaceTreeSearchTargets = new List<NetDocumentsWorkspaceTargetResultView>();
        _workspaceSearchCts?.Cancel();
        _workspaceSearchCts?.Dispose();
        _workspaceSearchCts = null;
        _workspaceLookupContext = null;
        _isWorkspaceLookupAvailable = true;
        UpdateOnUi(() =>
        {
            _browseRootNodes.Clear();
            SelectedBrowseNode = null;
        });
        Trace.WriteLine($"ND-BROWSER context-reset reason='{reason}' version={_targetBrowserContextVersion} repo='{SelectedNetDocumentsRepositoryId}' cabinet='{SelectedNetDocumentsCabinetId}'.");
        OnPropertyChanged(nameof(CanSearchWorkspaceTargets));
        OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
    }

    private void ApplyWorkspaceLookupAvailability(WorkspaceLookupContext? context)
    {
        _isWorkspaceLookupAvailable = context is not null;
        OnPropertyChanged(nameof(CanSearchWorkspaceTargets));
        OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));

        if (!_isWorkspaceLookupAvailable)
        {
            WorkspaceLookupStatus = "This cabinet does not expose workspace lookup attributes; workspace search is disabled.";
            if (SelectedTargetBrowserTab == NdTargetBrowserTab.GoToWorkspace)
            {
                TargetBrowserMessage = WorkspaceLookupStatus;
            }
        }
        else if (WorkspaceLookupStatus.Contains("workspace search is disabled", StringComparison.OrdinalIgnoreCase))
        {
            WorkspaceLookupStatus = string.Empty;
        }
    }

    private static int NameContains(string? name, string term)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               name.Contains(term, StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private string BuildTargetSnapshotCacheKey(NdTargetSelection target)
    {
        return $"{SelectedNetDocumentsRepositoryId}:{SelectedNetDocumentsCabinetId}:{NdTargetBrowserLogic.BuildTargetKey(target)}";
    }

    private void RestoreTargetSelectionFromSettings(NetDocumentsConnectionSettings settings)
    {
        _localRecentTargets = new List<NdTargetRecentItem>();
        _localFavoriteTargets = NdTargetBrowserLogic.DeserializeFavoriteTargets(settings.FavoriteTargetsJson).ToList();
        WorkspaceSearchText = settings.LastWorkspaceQuery ?? string.Empty;
        _isBrowseFilterPanelVisible = settings.IsBrowseFilterPanelVisible;
        _browseFilterShowCabFolders = settings.BrowseFilterShowCabFolders;
        _browseFilterShowFolders = settings.BrowseFilterShowFolders;
        _browseFilterShowFilters = settings.BrowseFilterShowFilters;
        _browseFilterShowCollabspaces = settings.BrowseFilterShowCollabspaces;
        OnPropertyChanged(nameof(IsBrowseFilterPanelVisible));
        OnPropertyChanged(nameof(BrowseFilterShowCabFolders));
        OnPropertyChanged(nameof(BrowseFilterShowFolders));
        OnPropertyChanged(nameof(BrowseFilterShowFilters));
        OnPropertyChanged(nameof(BrowseFilterShowCollabspaces));
        _workspaceLookupContext = WorkspaceLookupContext.FromJson(settings.WorkspaceLookupContextJson);
        WorkspaceLookupStatus = string.Empty;

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
            ParentWorkspaceId = settings.SelectedTargetParentWorkspaceId,
            Extension = settings.SelectedTargetExtension ?? string.Empty,
            SourceFlow = NdTargetSourceFlow.Browse
        };

        SelectedNetDocumentsTargetId = _selectedNetDocumentsTarget.Id;
        SelectedNetDocumentsTargetName = _selectedNetDocumentsTarget.Name;
        SelectedNetDocumentsTargetTypeDisplay = NdTargetBrowserLogic.ResolveTypeDisplay(_selectedNetDocumentsTarget.Type, _selectedNetDocumentsTarget.Id);
        SelectedNetDocumentsTargetPath = string.IsNullOrWhiteSpace(settings.SelectedTargetPath)
            ? _selectedNetDocumentsTarget.Name
            : settings.SelectedTargetPath;
        _selectedNetDocumentsTargetSupported = true;
        OnPropertyChanged(nameof(IsSelectedTargetFavorite));
    }

    private void SaveTargetSelectionToSettings(NetDocumentsConnectionSettings settings)
    {
        settings.RecentTargetsJson = string.Empty;
        settings.FavoriteTargetsJson = NdTargetBrowserLogic.SerializeFavoriteTargets(_localFavoriteTargets);
        settings.LastWorkspaceQuery = WorkspaceSearchText ?? string.Empty;
        settings.IsBrowseFilterPanelVisible = IsBrowseFilterPanelVisible;
        settings.BrowseFilterShowCabFolders = BrowseFilterShowCabFolders;
        settings.BrowseFilterShowFolders = BrowseFilterShowFolders;
        settings.BrowseFilterShowFilters = BrowseFilterShowFilters;
        settings.BrowseFilterShowCollabspaces = BrowseFilterShowCollabspaces;
        settings.WorkspaceLookupContextJson = _workspaceLookupContext?.ToJson() ?? string.Empty;

        if (_selectedNetDocumentsTarget is null)
        {
            settings.SelectedTargetType = string.Empty;
            settings.SelectedTargetId = string.Empty;
            settings.SelectedTargetName = string.Empty;
            settings.SelectedTargetParentWorkspaceId = string.Empty;
            settings.SelectedTargetExtension = string.Empty;
            settings.SelectedTargetPath = string.Empty;
            settings.EffectiveProfileDefaultsJson = string.Empty;
            return;
        }

        settings.SelectedTargetType = _selectedNetDocumentsTarget.Type.ToString();
        settings.SelectedTargetId = _selectedNetDocumentsTarget.Id;
        settings.SelectedTargetName = _selectedNetDocumentsTarget.Name;
        settings.SelectedTargetParentWorkspaceId = _selectedNetDocumentsTarget.ParentWorkspaceId ?? string.Empty;
        settings.SelectedTargetExtension = _selectedNetDocumentsTarget.Extension ?? string.Empty;
        settings.SelectedTargetPath = SelectedNetDocumentsTargetPath ?? string.Empty;
        settings.EffectiveProfileDefaultsJson = EffectiveProfileDefaults.ToJson();
    }

    private static NdTargetSelection CloneSelection(NdTargetSelection selection)
    {
        return new NdTargetSelection
        {
            Type = selection.Type,
            Id = selection.Id,
            Name = selection.Name,
            ParentWorkspaceId = selection.ParentWorkspaceId,
            Extension = selection.Extension,
            SourceFlow = selection.SourceFlow
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

    public string TypeDisplay => NdTargetBrowserLogic.ResolveTypeDisplay(Selection.Type, Selection.Id);

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

public sealed class NetDocumentsWorkspaceTargetResultView
{
    public NetDocumentsWorkspaceTargetResultView(
        NdTargetSelection selection,
        string pathDisplay,
        string source,
        string parentKey = "",
        string parentDescription = "",
        string childKey = "",
        string childDescription = "")
    {
        Selection = selection;
        PathDisplay = pathDisplay;
        Source = source;
        ParentKey = parentKey;
        ParentDescription = parentDescription;
        ChildKey = childKey;
        ChildDescription = childDescription;
    }

    public NdTargetSelection Selection { get; }

    public string Name => Selection.Name;

    public string TypeDisplay => NdTargetBrowserLogic.ResolveTypeDisplay(Selection.Type, Selection.Id);

    public string PathDisplay { get; }

    public string Source { get; }

    public string ParentKey { get; }

    public string ParentDescription { get; }

    public string ChildKey { get; }

    public string ChildDescription { get; }
}

public sealed class NetDocumentsBrowseNodeView
{
    public NetDocumentsBrowseNodeView(
        NdContainerNode node,
        NdTargetSourceFlow sourceFlow = NdTargetSourceFlow.Browse,
        string metadata = "")
    {
        Id = node.Id;
        Name = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;
        TypeRaw = node.TypeRaw;
        Extension = node.Extension;
        ParentId = node.ParentId;
        ParentWorkspaceId = node.ParentWorkspaceId;
        PathDisplay = node.PathDisplay;
        SupportedType = node.SupportedType;
        IsSelectable = node.IsSelectable;
        UnsupportedReason = node.UnsupportedReason;
        HasChildren = node.HasChildren;
        ChildrenLoadState = node.ChildrenLoadState;
        SourceFlow = sourceFlow;
        Metadata = metadata ?? string.Empty;

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
        Extension = string.Empty;
        ParentId = string.Empty;
        PathDisplay = string.Empty;
        UnsupportedReason = string.Empty;
        Metadata = string.Empty;
        SourceFlow = NdTargetSourceFlow.Browse;
    }

    public string Id { get; }

    public string Name { get; }

    public string TypeRaw { get; }

    public string Extension { get; }

    public string ParentId { get; }

    public string? ParentWorkspaceId { get; }

    public string PathDisplay { get; }

    public NdTargetType? SupportedType { get; }

    public NdTargetSourceFlow SourceFlow { get; }

    public string Metadata { get; }

    public bool IsSelectable { get; }

    public string UnsupportedReason { get; }

    public bool HasChildren { get; }

    public bool IsPlaceholder { get; }

    public ObservableCollection<NetDocumentsBrowseNodeView> Children { get; } = new();

    public NdChildrenLoadState ChildrenLoadState { get; set; }

    public string IconGlyph => NdTargetBrowserLogic.ResolveIconDescriptor(SupportedType, Id).Glyph;

    public string IconColorHex => NdTargetBrowserLogic.ResolveIconDescriptor(SupportedType, Id).ColorHex;

    public string TypeDisplay
    {
        get
        {
            if (SupportedType.HasValue)
            {
                return NdTargetBrowserLogic.ResolveTypeDisplay(SupportedType.Value, Id);
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

    public NdContainerNode ToNodeModel()
    {
        return new NdContainerNode
        {
            Id = Id,
            Name = Name,
            TypeRaw = TypeRaw,
            Extension = Extension,
            ParentId = ParentId,
            ParentWorkspaceId = ParentWorkspaceId,
            PathDisplay = PathDisplay,
            SupportedType = SupportedType,
            IsSelectable = IsSelectable,
            UnsupportedReason = UnsupportedReason,
            HasChildren = HasChildren,
            ChildrenLoadState = ChildrenLoadState
        };
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
