using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using NetDocsImporter.Core;
using NetDocsImporter.Data;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.App;

/// <summary>
/// Hosts NetDocuments connection, metadata synchronization, and cabinet selection state used by the UI.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly ObservableCollection<NetDocumentsRepositoryView> _netDocumentsRepositories = new();
    private readonly ObservableCollection<NetDocumentsCabinetView> _netDocumentsCabinets = new();
    private readonly ObservableCollection<NetDocumentsSyncedAttributeView> _syncedAttributes = new();

    private INetDocumentsAuthService? _netDocumentsAuthService;
    private NetDocumentsApiClient? _netDocumentsApiClient;
    private NetDocumentsSyncService? _netDocumentsSyncService;
    private INetDocumentsMetadataProvider? _netDocumentsMetadataProvider;
    private IDirectUploadService? _netDocumentsDirectUploadService;

    private bool _isNetDocumentsConnected;
    private string _netDocumentsConnectedUser = string.Empty;
    private string _selectedNetDocumentsRepositoryId = string.Empty;
    private string _selectedNetDocumentsCabinetId = string.Empty;
    private string _selectedNetDocumentsCabinetName = string.Empty;
    private string _currentJobRepositoryId = string.Empty;
    private string _netDocumentsCurrentUserId = string.Empty;
    private NetDocumentsSyncedAttributeView? _selectedSyncedAttribute;

    /// <summary>
    /// Gets the repositories available in the connected NetDocuments region.
    /// </summary>
    public ObservableCollection<NetDocumentsRepositoryView> NetDocumentsRepositories => _netDocumentsRepositories;

    /// <summary>
    /// Gets the cabinets visible for the selected repository.
    /// </summary>
    public ObservableCollection<NetDocumentsCabinetView> NetDocumentsCabinets => _netDocumentsCabinets;

    /// <summary>
    /// Gets synced cabinet profile attributes available for lookup preview and profile-aware operations.
    /// </summary>
    public ObservableCollection<NetDocumentsSyncedAttributeView> SyncedAttributes => _syncedAttributes;

    /// <summary>
    /// Gets the currently connected NetDocuments user display string.
    /// </summary>
    public string NetDocumentsConnectedUser
    {
        get => _netDocumentsConnectedUser;
        private set => SetField(ref _netDocumentsConnectedUser, value);
    }

    /// <summary>
    /// Gets a value indicating whether an authenticated NetDocuments session is active.
    /// </summary>
    public bool IsNetDocumentsConnected
    {
        get => _isNetDocumentsConnected;
        private set
        {
            if (SetField(ref _isNetDocumentsConnected, value))
            {
                if (!value)
                {
                    IsSettingsOpen = true;
                    SetCurrentStep(StepKey.SelectFolder);
                }

                OnPropertyChanged(nameof(CanSyncNetDocumentsCabinets));
                OnPropertyChanged(nameof(CanSyncNetDocumentsAttributes));
                OnPropertyChanged(nameof(CanSelectSourceFolder));
                OnPropertyChanged(nameof(IsAuthenticationRequired));
                OnPropertyChanged(nameof(CanAccessMainFlow));
                OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
                OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
                OnPropertyChanged(nameof(CanContinueToReviewScope));
                OnPropertyChanged(nameof(CanSearchWorkspaceTargets));
                OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
                OnPropertyChanged(nameof(CanRunDirectUpload));
            }
        }
    }

    /// <summary>
    /// Gets or sets the selected repository identifier and refreshes repository-scoped target state.
    /// </summary>
    public string SelectedNetDocumentsRepositoryId
    {
        get => _selectedNetDocumentsRepositoryId;
        set
        {
            if (!IsRepositoryAllowedForCurrentJob(value))
            {
                StatusText = $"Current job is locked to repository {_currentJobRepositoryId}.";
                return;
            }

            if (!SetField(ref _selectedNetDocumentsRepositoryId, value))
            {
                return;
            }

            InvalidateTargetBrowserContext("repository-changed");
            _refreshedCabinetSchemaThisSession.Clear();
            _targetProfileCache.Clear();
            _workspaceLookupContext = null;
            _workspaceLookupPairCache.Clear();
            _workspaceLookupInvalidPairCache.Clear();
            _workspaceSearchTargets.Clear();
            SelectedWorkspaceSearchTarget = null;
            WorkspaceLookupStatus = string.Empty;
            _hasLoadedRecentTargets = false;
            _hasLoadedFavoriteTargets = false;
            FilterCabinetsByRepository();
            OnPropertyChanged(nameof(CanSyncNetDocumentsAttributes));
            OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
            OnPropertyChanged(nameof(CanSearchWorkspaceTargets));
            OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
            OnPropertyChanged(nameof(CanRunDirectUpload));
            HandleDirectUploadContextChanged(
                "NetDocuments repository changed. Refresh direct upload preflight.",
                refreshPreflight: false);
            QueueSettingsSave();
            _ = RefreshReviewScopeNetDocumentsAsync();
            _ = LoadNetDocumentsTargetContainersAsync();
        }
    }

    /// <summary>
    /// Gets or sets the selected cabinet identifier and refreshes cabinet-scoped target and metadata state.
    /// </summary>
    public string SelectedNetDocumentsCabinetId
    {
        get => _selectedNetDocumentsCabinetId;
        set
        {
            if (!SetField(ref _selectedNetDocumentsCabinetId, value))
            {
                return;
            }

            InvalidateTargetBrowserContext("cabinet-changed");
            _refreshedCabinetSchemaThisSession.Clear();
            _targetProfileCache.Clear();
            _workspaceLookupContext = null;
            _workspaceLookupPairCache.Clear();
            _workspaceLookupInvalidPairCache.Clear();
            _workspaceSearchTargets.Clear();
            SelectedWorkspaceSearchTarget = null;
            WorkspaceLookupStatus = string.Empty;
            _hasLoadedRecentTargets = false;
            _hasLoadedFavoriteTargets = false;
            var cabinet = _netDocumentsCabinets.FirstOrDefault(c => c.CabinetId == value);
            _selectedNetDocumentsCabinetName = cabinet?.CabinetName ?? string.Empty;
            OnPropertyChanged(nameof(SelectedNetDocumentsCabinetName));
            SyncNdImportCabinetFromSelectedCabinetId();
            OnPropertyChanged(nameof(CanSyncNetDocumentsAttributes));
            OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
            OnPropertyChanged(nameof(CanSearchWorkspaceTargets));
            OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
            OnPropertyChanged(nameof(CanRunDirectUpload));
            HandleDirectUploadContextChanged(
                "NetDocuments cabinet changed. Refresh direct upload preflight.",
                refreshPreflight: false);
            QueueSettingsSave();
            _ = LoadSyncedAttributesForSelectedCabinetAsync();
            _ = RefreshReviewScopeNetDocumentsAsync();
            _ = LoadNetDocumentsTargetContainersAsync();
            _ = RefreshRecentTargetsAfterContextChangeAsync();
        }
    }

    /// <summary>
    /// Gets the selected cabinet display name.
    /// </summary>
    public string SelectedNetDocumentsCabinetName => _selectedNetDocumentsCabinetName;

    /// <summary>
    /// Gets the repository lock for the active job, if one has been established.
    /// </summary>
    public string CurrentJobRepositoryId => _currentJobRepositoryId;

    /// <summary>
    /// Gets or sets the selected synced attribute for lookup-value preview.
    /// </summary>
    public NetDocumentsSyncedAttributeView? SelectedSyncedAttribute
    {
        get => _selectedSyncedAttribute;
        set
        {
            if (SetField(ref _selectedSyncedAttribute, value))
            {
                OnPropertyChanged(nameof(CanViewLookupValues));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether cabinet synchronization can run.
    /// </summary>
    public bool CanSyncNetDocumentsCabinets => IsNetDocumentsConnected;

    /// <summary>
    /// Gets a value indicating whether attribute synchronization can run for the current repository and cabinet.
    /// </summary>
    public bool CanSyncNetDocumentsAttributes =>
        IsNetDocumentsConnected &&
        !string.IsNullOrWhiteSpace(SelectedNetDocumentsRepositoryId) &&
        !string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId);

    /// <summary>
    /// Gets a value indicating whether lookup values can be viewed for the selected attribute.
    /// </summary>
    public bool CanViewLookupValues => SelectedSyncedAttribute?.IsLookup == true;

    private void InitializeNetDocumentsIntegration()
    {
        var tokenPath = Path.Combine(_paths.SecretsDirectory, AppSettings.DefaultNetDocumentsTokenRef);
        _netDocumentsAuthService = new NetDocumentsAuthService(tokenPath);
        _netDocumentsApiClient = new NetDocumentsApiClient(
            _netDocumentsAuthService,
            BuildAuthContext,
            GetApiBaseUrl);
        _netDocumentsSyncService = new NetDocumentsSyncService(_netDocumentsApiClient, _jobStore);
        _netDocumentsMetadataProvider = new NetDocumentsMetadataProvider(_jobStore);
        _netDocumentsDirectUploadService = new NetDocumentsDirectUploadService(_netDocumentsApiClient, _jobStore);
    }

    private NetDocumentsAuthContext BuildAuthContext()
    {
        var oauthConfig = _selectedNetDocumentsOAuthClientConfig
            ?? throw new InvalidOperationException($"No NetDocuments OAuth profile configured for region {SelectedNetDocumentsRegion}.");

        return new NetDocumentsAuthContext
        {
            OAuthAuthorizeBaseUrl = oauthConfig.OAuthAuthorizeBaseUrl,
            OAuthTokenUrl = oauthConfig.OAuthTokenUrl,
            ClientId = oauthConfig.ClientId,
            ClientSecret = oauthConfig.ClientSecret,
            RedirectUri = oauthConfig.RedirectUri.Trim()
        };
    }

    private string GetApiBaseUrl()
    {
        return _selectedNetDocumentsOAuthClientConfig?.ApiBaseUrl
               ?? GetSelectedNetDocumentsRegionSetting().ApiBaseUrl;
    }

    private NetDocumentsSyncService RequireSyncService()
    {
        return _netDocumentsSyncService ?? throw new InvalidOperationException("NetDocuments integration is not initialized.");
    }

    private INetDocumentsAuthService RequireAuthService()
    {
        return _netDocumentsAuthService ?? throw new InvalidOperationException("NetDocuments auth service is not initialized.");
    }

    private INetDocumentsMetadataProvider RequireMetadataProvider()
    {
        return _netDocumentsMetadataProvider ?? throw new InvalidOperationException("NetDocuments metadata provider is not initialized.");
    }

    private IDirectUploadService RequireDirectUploadService()
    {
        return _netDocumentsDirectUploadService ?? throw new InvalidOperationException("NetDocuments direct upload service is not initialized.");
    }

    private bool IsRepositoryAllowedForCurrentJob(string repositoryId)
    {
        return string.IsNullOrWhiteSpace(CurrentJobId) ||
               string.IsNullOrWhiteSpace(_currentJobRepositoryId) ||
               string.Equals(_currentJobRepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts interactive sign-in and synchronizes cabinets, attributes, and target-browser caches for the current region.
    /// </summary>
    /// <returns>A task that completes when connection bootstrap finishes.</returns>
    public async Task ConnectAndSyncNetDocumentsAsync()
    {
        if (!CanConnectToNetDocuments)
        {
            StatusText = "OAuth profile for this region is not installed. Contact administrator.";
            return;
        }

        try
        {
            InvalidateTargetBrowserContext("connect-and-sync");
            _refreshedCabinetSchemaThisSession.Clear();
            var auth = RequireAuthService();
            var sync = RequireSyncService();

            await auth.SignInInteractiveAsync(BuildAuthContext());
            var user = await sync.GetCurrentUserAsync();
            _netDocumentsCurrentUserId = string.IsNullOrWhiteSpace(user.UserId) ? string.Empty : user.UserId;
            NetDocumentsConnectedUser = string.IsNullOrWhiteSpace(user.DisplayName)
                ? user.UserId
                : $"{user.DisplayName} ({user.Email})";
            IsNetDocumentsConnected = true;

            await SyncNetDocumentsCabinetsAsync();
            if (CanSyncNetDocumentsAttributes)
            {
                await SyncNetDocumentsAttributesAsync();
            }
            await LoadNetDocumentsTargetContainersAsync();
            await RefreshRecentTargetsAfterConnectAsync();

            await RefreshReviewScopeNetDocumentsAsync();

            StatusText = string.IsNullOrWhiteSpace(user.DisplayName)
                ? "Connected to NetDocuments."
                : $"Connected to NetDocuments as {user.DisplayName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"NetDocuments connect failed: {ex.Message}";
        }
    }

    private async Task RefreshRecentTargetsAfterConnectAsync()
    {
        try
        {
            await RefreshRecentTargetsAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"ND-CACHE recent post-connect refresh failed: {ex.Message}");
        }
    }

    private async Task RefreshRecentTargetsAfterContextChangeAsync()
    {
        try
        {
            if (CanPickNetDocumentsTarget)
            {
                await RefreshRecentTargetsAsync();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"ND-CACHE recent post-context refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves developer bootstrap OAuth settings into the local secure profile store for the selected region.
    /// </summary>
    /// <returns>A task that completes when profile persistence and state refresh are done.</returns>
    public async Task SaveNetDocumentsOAuthProfileAsync()
    {
        if (!IsDeveloperMode)
        {
            StatusText = "Developer mode is required to save local OAuth profiles.";
            return;
        }

        if (!CanSaveNetDocumentsDevBootstrap)
        {
            StatusText = "Provide Client ID and Redirect URI to save a local OAuth profile.";
            return;
        }

        var redirectUri = NetDocumentsBootstrapRedirectUri.Trim();
        if (!TryValidateLoopbackRedirectUri(redirectUri, out var redirectError))
        {
            StatusText = redirectError;
            return;
        }

        var region = GetSelectedNetDocumentsRegionSetting();
        if (string.IsNullOrWhiteSpace(region.ApiBaseUrl) ||
            string.IsNullOrWhiteSpace(region.OAuthAuthorizeBaseUrl) ||
            string.IsNullOrWhiteSpace(region.OAuthTokenUrl))
        {
            StatusText = "Selected NetDocuments region endpoints are not configured.";
            return;
        }

        var profile = new NetDocumentsOAuthClientConfig
        {
            Region = SelectedNetDocumentsRegion,
            ClientId = NetDocumentsBootstrapClientId.Trim(),
            ClientSecret = NetDocumentsBootstrapClientSecret,
            RedirectUri = redirectUri,
            ApiBaseUrl = region.ApiBaseUrl,
            OAuthAuthorizeBaseUrl = region.OAuthAuthorizeBaseUrl,
            OAuthTokenUrl = region.OAuthTokenUrl
        };

        _netDocumentsOAuthClientProfiles[SelectedNetDocumentsRegion.ToString()] = profile;
        await SaveNetDocumentsOAuthClientProfilesAsync();
        ResolveNetDocumentsOAuthClientConfig();
        ClearNetDocumentsBootstrapFields();

        OnPropertyChanged(nameof(NetDocumentsConnectionProfileStatus));
        OnPropertyChanged(nameof(CanConnectToNetDocuments));
        OnPropertyChanged(nameof(CanShowNetDocumentsDevBootstrap));
        OnPropertyChanged(nameof(CanSaveNetDocumentsDevBootstrap));
        QueueSettingsSave();

        StatusText = $"Developer OAuth profile saved for {SelectedNetDocumentsRegion}.";
    }

    /// <summary>
    /// Attempts to rehydrate a previous authenticated NetDocuments session without prompting the user.
    /// </summary>
    /// <returns>A task that completes after session restoration is attempted.</returns>
    public async Task TryRestoreNetDocumentsSessionAsync()
    {
        try
        {
            var auth = RequireAuthService();
            await auth.GetAccessTokenAsync(BuildAuthContext());
            IsNetDocumentsConnected = true;

            var user = await RequireSyncService().GetCurrentUserAsync();
            _netDocumentsCurrentUserId = string.IsNullOrWhiteSpace(user.UserId) ? string.Empty : user.UserId;
            NetDocumentsConnectedUser = string.IsNullOrWhiteSpace(user.DisplayName)
                ? user.UserId
                : $"{user.DisplayName} ({user.Email})";
        }
        catch
        {
            IsNetDocumentsConnected = false;
            _netDocumentsCurrentUserId = string.Empty;
            _refreshedCabinetSchemaThisSession.Clear();
        }
    }

    private void ClearNetDocumentsBootstrapFields()
    {
        _netDocumentsBootstrapClientId = string.Empty;
        _netDocumentsBootstrapClientSecret = string.Empty;
        _netDocumentsBootstrapRedirectUri = NetDocumentsRegionDefaults.DefaultRedirectUri;
        OnPropertyChanged(nameof(NetDocumentsBootstrapClientId));
        OnPropertyChanged(nameof(NetDocumentsBootstrapClientSecret));
        OnPropertyChanged(nameof(NetDocumentsBootstrapRedirectUri));
    }

    private static bool TryValidateLoopbackRedirectUri(string redirectUri, out string error)
    {
        error = string.Empty;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            error = "Redirect URI must be a valid absolute URI.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = "Redirect URI must use http.";
            return false;
        }

        var isLoopbackHost =
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        if (!isLoopbackHost)
        {
            error = "Redirect URI host must be localhost or 127.0.0.1.";
            return false;
        }

        if (uri.Port <= 0)
        {
            error = "Redirect URI must include a port.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Synchronizes region cabinet metadata from NetDocuments and refreshes repository and target-browser state.
    /// </summary>
    /// <returns>A task that completes when synchronization and local refresh complete.</returns>
    public async Task SyncNetDocumentsCabinetsAsync()
    {
        if (!IsNetDocumentsConnected)
        {
            StatusText = "Connect to NetDocuments first.";
            return;
        }

        try
        {
            var sync = RequireSyncService();
            var region = SelectedNetDocumentsRegion.ToString();
            await sync.SyncCabinetsAsync(region);
            await LoadNetDocumentsMetadataAsync();
            await LoadNetDocumentsTargetContainersAsync();
            StatusText = "NetDocuments cabinets synced.";
            await RefreshReviewScopeNetDocumentsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Cabinet sync failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Synchronizes profile attributes for the selected cabinet and refreshes schema-dependent state.
    /// </summary>
    /// <returns>A task that completes when attribute sync and refresh operations finish.</returns>
    public async Task SyncNetDocumentsAttributesAsync()
    {
        if (!CanSyncNetDocumentsAttributes)
        {
            StatusText = "Select repository and cabinet first.";
            return;
        }

        if (!IsRepositoryAllowedForCurrentJob(SelectedNetDocumentsRepositoryId))
        {
            StatusText = $"Current job is locked to repository {_currentJobRepositoryId}.";
            return;
        }

        try
        {
            var sync = RequireSyncService();
            var attributes = await sync.SyncCabinetAttributesAsync(SelectedNetDocumentsCabinetId, SelectedNetDocumentsRepositoryId);

            await LoadSyncedAttributesForSelectedCabinetAsync();
            await LoadSchemaFromSyncedMetadataAsync();
            await EnsureCurrentJobRepositoryAsync(SelectedNetDocumentsRepositoryId);
            await LoadNetDocumentsTargetContainersAsync();
            await RefreshReviewScopeNetDocumentsAsync();

            StatusText = $"Synced {attributes.Count} profile attributes for cabinet {SelectedNetDocumentsCabinetName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Attribute sync failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads cached repository and cabinet metadata for the selected region and rebinds current selections safely.
    /// </summary>
    /// <returns>A task that completes when metadata, schema, and target-browser state are refreshed.</returns>
    public async Task LoadNetDocumentsMetadataAsync()
    {
        await _jobStore.InitializeAsync();
        var region = SelectedNetDocumentsRegion.ToString();
        var cabinets = await _jobStore.GetNetDocumentsCabinetsAsync(region);
        var repositoryViews = cabinets
            .Where(c => !string.IsNullOrWhiteSpace(c.RepositoryId))
            .GroupBy(c => c.RepositoryId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new NetDocumentsRepositoryView(
                group.Key,
                group.First().RepositoryName))
            .OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UpdateOnUi(() =>
        {
            _netDocumentsRepositories.Clear();
            foreach (var repository in repositoryViews)
            {
                _netDocumentsRepositories.Add(repository);
            }
        });

        if (repositoryViews.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(SelectedNetDocumentsRepositoryId) ||
                repositoryViews.All(r => !string.Equals(r.RepositoryId, SelectedNetDocumentsRepositoryId, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedNetDocumentsRepositoryId = repositoryViews[0].RepositoryId;
            }
            else
            {
                OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
                OnPropertyChanged(nameof(CanSearchWorkspaceTargets));
                OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
            }
        }
        else if (!string.IsNullOrWhiteSpace(SelectedNetDocumentsRepositoryId))
        {
            SelectedNetDocumentsRepositoryId = string.Empty;
        }

        _refreshedCabinetSchemaThisSession.Clear();
        FilterCabinetsByRepository();
        await LoadSyncedAttributesForSelectedCabinetAsync();
        await LoadNetDocumentsTargetContainersAsync();
        await RefreshReviewScopeNetDocumentsAsync();
    }

    private void FilterCabinetsByRepository()
    {
        var region = SelectedNetDocumentsRegion.ToString();
        _ = Task.Run(async () =>
        {
            await _jobStore.InitializeAsync();
            var allCabinets = await _jobStore.GetNetDocumentsCabinetsAsync(region);
            var cabinets = allCabinets
                .Where(c =>
                    string.IsNullOrWhiteSpace(SelectedNetDocumentsRepositoryId) ||
                    string.Equals(c.RepositoryId, SelectedNetDocumentsRepositoryId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.CabinetName, StringComparer.OrdinalIgnoreCase)
                .Select(c => new NetDocumentsCabinetView(
                    c.CabinetId,
                    c.RepositoryId,
                    c.CabinetName,
                    c.Description,
                    c.WorkspaceAttributeNum,
                    c.WorkspacePluralName,
                    c.AllowFileInWorkspaces))
                .ToList();

            UpdateOnUi(() =>
            {
                _netDocumentsCabinets.Clear();
                foreach (var cabinet in cabinets)
                {
                    _netDocumentsCabinets.Add(cabinet);
                }

                if (_netDocumentsCabinets.Count == 0)
                {
                    if (!string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId))
                    {
                        SelectedNetDocumentsCabinetId = string.Empty;
                    }
                    else
                    {
                        _selectedNetDocumentsCabinetName = string.Empty;
                        _refreshedCabinetSchemaThisSession.Clear();
                        OnPropertyChanged(nameof(SelectedNetDocumentsCabinetName));
                        OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
                        OnPropertyChanged(nameof(CanSearchWorkspaceTargets));
                        OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
                    }
                    return;
                }

                if (_netDocumentsCabinets.All(c => !string.Equals(c.CabinetId, SelectedNetDocumentsCabinetId, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedNetDocumentsCabinetId = _netDocumentsCabinets[0].CabinetId;
                    return;
                }

                var selectedCabinet = _netDocumentsCabinets.FirstOrDefault(c =>
                    string.Equals(c.CabinetId, SelectedNetDocumentsCabinetId, StringComparison.OrdinalIgnoreCase));
                var selectedCabinetName = selectedCabinet?.CabinetName ?? string.Empty;
                if (!string.Equals(_selectedNetDocumentsCabinetName, selectedCabinetName, StringComparison.Ordinal))
                {
                    _selectedNetDocumentsCabinetName = selectedCabinetName;
                    OnPropertyChanged(nameof(SelectedNetDocumentsCabinetName));
                }

                SyncNdImportCabinetFromSelectedCabinetId();
                OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
                OnPropertyChanged(nameof(CanSearchWorkspaceTargets));
                OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
            });
        });
    }

    private void SyncNdImportCabinetFromSelectedCabinetId()
    {
        if (string.IsNullOrWhiteSpace(_selectedNetDocumentsCabinetId))
        {
            return;
        }

        var selectedId = _selectedNetDocumentsCabinetId.Trim();
        if (!string.Equals(NdImportCabinet, selectedId, StringComparison.OrdinalIgnoreCase))
        {
            NdImportCabinet = selectedId;
        }
    }

    private async Task EnsureCurrentJobRepositoryAsync(string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId) || string.IsNullOrWhiteSpace(repositoryId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_currentJobRepositoryId) &&
            !string.Equals(_currentJobRepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Current job is locked to repository {_currentJobRepositoryId}.");
        }

        if (string.IsNullOrWhiteSpace(_currentJobRepositoryId))
        {
            await _jobStore.UpdateJobRepositoryAsync(CurrentJobId, repositoryId);
            _currentJobRepositoryId = repositoryId;
            OnPropertyChanged(nameof(CurrentJobRepositoryId));
        }
    }

    private async Task LoadSyncedAttributesForSelectedCabinetAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId))
        {
            UpdateOnUi(() =>
            {
                _syncedAttributes.Clear();
                SelectedSyncedAttribute = null;
            });
            return;
        }

        var provider = RequireMetadataProvider();
        var attributes = await provider.GetSyncedAttributesAsync(SelectedNetDocumentsCabinetId);
        var viewItems = attributes
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new NetDocumentsSyncedAttributeView(
                a.CabinetId,
                a.AttributeNum,
                a.Name,
                a.DataType,
                a.IsLookup,
                a.IsMultiValue,
                a.ParentAttributeNum))
            .ToList();

        UpdateOnUi(() =>
        {
            _syncedAttributes.Clear();
            foreach (var item in viewItems)
            {
                _syncedAttributes.Add(item);
            }

            SelectedSyncedAttribute = _syncedAttributes.FirstOrDefault();
        });
    }

    private async Task LoadSchemaFromSyncedMetadataAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId))
        {
            return;
        }

        var provider = RequireMetadataProvider();
        var attributes = await provider.GetSyncedAttributesAsync(SelectedNetDocumentsCabinetId);
        if (attributes.Count == 0)
        {
            return;
        }

        var fields = new List<ProfileSchemaField>(attributes.Count);
        foreach (var attribute in attributes.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var lookupValues = attribute.IsLookup
                ? await provider.GetLookupValuesAsync(attribute.CabinetId, attribute.AttributeNum)
                : Array.Empty<NetDocumentsSyncedLookupValue>();
            var schemaValues = lookupValues
                .Select(v => new ProfileSchemaValue(v.Key, v.Description, v.ParentKey))
                .ToList();

            fields.Add(new ProfileSchemaField(
                attribute.AttributeNum.ToString(),
                attribute.Name,
                schemaValues,
                attribute.IsLookup,
                attribute.IsMultiValue));
        }

        _schema = new ProfileSchemaDictionary(
            SelectedNetDocumentsCabinetName,
            "synced",
            fields);
        _schemaCatalog = new ProfileSchemaCatalog(new[] { _schema });
        HasSchemaLoaded = true;
        SchemaPath = $"synced://{SelectedNetDocumentsCabinetId}";
        SchemaCabinetName = SelectedNetDocumentsCabinetName;
        SchemaStatus = $"Synced metadata loaded ({fields.Count} attributes).";
        UpdateSchemaMatch();
        ResolveProfileFieldHints();
    }

    /// <summary>
    /// Displays a preview dialog of lookup values for the currently selected synced attribute.
    /// </summary>
    /// <returns>A task that completes when lookup values are loaded and the dialog is shown.</returns>
    public async Task ViewSelectedLookupValuesAsync()
    {
        if (SelectedSyncedAttribute is null || !SelectedSyncedAttribute.IsLookup)
        {
            StatusText = "Select a lookup attribute first.";
            return;
        }

        var provider = RequireMetadataProvider();
        var values = await provider.GetLookupValuesAsync(
            SelectedSyncedAttribute.CabinetId,
            SelectedSyncedAttribute.AttributeNum);

        var preview = values
            .Take(100)
            .Select(v => string.IsNullOrWhiteSpace(v.Description) ? v.Key : $"{v.Key} - {v.Description}")
            .ToList();

        var text = preview.Count == 0
            ? "No lookup values were found."
            : string.Join(Environment.NewLine, preview);

        var dialog = new TaskDialogPage
        {
            Caption = "Lookup values",
            Heading = SelectedSyncedAttribute.Name,
            Text = text,
            Buttons = { TaskDialogButton.OK }
        };

        ShowTaskDialog(dialog);
    }
}

/// <summary>
/// View model row representing a NetDocuments repository option.
/// </summary>
public sealed class NetDocumentsRepositoryView
{
    /// <summary>
    /// Initializes a repository view model row.
    /// </summary>
    /// <param name="repositoryId">Stable NetDocuments repository identifier.</param>
    /// <param name="repositoryName">Repository display name.</param>
    public NetDocumentsRepositoryView(string repositoryId, string repositoryName)
    {
        RepositoryId = repositoryId;
        RepositoryName = string.IsNullOrWhiteSpace(repositoryName) ? repositoryId : repositoryName;
    }

    /// <summary>
    /// Gets the repository identifier.
    /// </summary>
    public string RepositoryId { get; }

    /// <summary>
    /// Gets the repository display name.
    /// </summary>
    public string RepositoryName { get; }
}

/// <summary>
/// View model row representing a NetDocuments cabinet option and related workspace capabilities.
/// </summary>
public sealed class NetDocumentsCabinetView
{
    /// <summary>
    /// Initializes a cabinet view model row.
    /// </summary>
    /// <param name="cabinetId">Stable cabinet identifier.</param>
    /// <param name="repositoryId">Owning repository identifier.</param>
    /// <param name="cabinetName">Cabinet display name.</param>
    /// <param name="description">Cabinet description text.</param>
    /// <param name="workspaceAttributeNum">Workspace attribute number when workspaces are configured.</param>
    /// <param name="workspacePluralName">Workspace plural name supplied by NetDocuments.</param>
    /// <param name="allowFileInWorkspaces">Flag that indicates whether filing to workspaces is enabled.</param>
    public NetDocumentsCabinetView(
        string cabinetId,
        string repositoryId,
        string cabinetName,
        string description,
        int? workspaceAttributeNum,
        string workspacePluralName,
        bool? allowFileInWorkspaces)
    {
        CabinetId = cabinetId;
        RepositoryId = repositoryId;
        CabinetName = cabinetName;
        Description = description;
        WorkspaceAttributeNum = workspaceAttributeNum;
        WorkspacePluralName = workspacePluralName;
        AllowFileInWorkspaces = allowFileInWorkspaces;
    }

    /// <summary>
    /// Gets the cabinet identifier.
    /// </summary>
    public string CabinetId { get; }

    /// <summary>
    /// Gets the owning repository identifier.
    /// </summary>
    public string RepositoryId { get; }

    /// <summary>
    /// Gets the cabinet display name.
    /// </summary>
    public string CabinetName { get; }

    /// <summary>
    /// Gets the cabinet description.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the workspace attribute number when workspace support is enabled.
    /// </summary>
    public int? WorkspaceAttributeNum { get; }

    /// <summary>
    /// Gets the workspace plural display name from NetDocuments.
    /// </summary>
    public string WorkspacePluralName { get; }

    /// <summary>
    /// Gets a value indicating whether items may be filed directly in workspaces.
    /// </summary>
    public bool? AllowFileInWorkspaces { get; }
}

/// <summary>
/// View model row representing a synced profile attribute for the selected cabinet.
/// </summary>
public sealed class NetDocumentsSyncedAttributeView
{
    /// <summary>
    /// Initializes a synced-attribute row.
    /// </summary>
    /// <param name="cabinetId">Cabinet identifier the attribute belongs to.</param>
    /// <param name="attributeNum">Attribute number in the cabinet schema.</param>
    /// <param name="name">Attribute display name.</param>
    /// <param name="dataType">Underlying NetDocuments data type.</param>
    /// <param name="isLookup">Indicates whether this attribute is lookup-backed.</param>
    /// <param name="isMultiValue">Indicates whether this attribute supports multiple values.</param>
    /// <param name="parentAttributeNum">Parent attribute number for child lookup attributes.</param>
    public NetDocumentsSyncedAttributeView(
        string cabinetId,
        int attributeNum,
        string name,
        string dataType,
        bool isLookup,
        bool isMultiValue,
        int? parentAttributeNum)
    {
        CabinetId = cabinetId;
        AttributeNum = attributeNum;
        Name = name;
        DataType = dataType;
        IsLookup = isLookup;
        IsMultiValue = isMultiValue;
        ParentAttributeNum = parentAttributeNum;
    }

    /// <summary>
    /// Gets the cabinet identifier this attribute belongs to.
    /// </summary>
    public string CabinetId { get; }

    /// <summary>
    /// Gets the numeric attribute identifier.
    /// </summary>
    public int AttributeNum { get; }

    /// <summary>
    /// Gets the attribute display name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the source data type.
    /// </summary>
    public string DataType { get; }

    /// <summary>
    /// Gets a value indicating whether this attribute uses a lookup table.
    /// </summary>
    public bool IsLookup { get; }

    /// <summary>
    /// Gets a value indicating whether this attribute accepts multiple values.
    /// </summary>
    public bool IsMultiValue { get; }

    /// <summary>
    /// Gets the parent attribute number when this is a child lookup attribute.
    /// </summary>
    public int? ParentAttributeNum { get; }

    /// <summary>
    /// Gets a concise display string used in UI lists to describe lookup/multi-value behavior.
    /// </summary>
    public string TypeDisplay =>
        IsLookup
            ? IsMultiValue ? "Lookup (multi)" : "Lookup"
            : IsMultiValue ? "Text (multi)" : "Text";
}
