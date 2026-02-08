using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using NetDocsImporter.Core;
using NetDocsImporter.Data;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.App;

public sealed partial class MainViewModel
{
    private readonly ObservableCollection<NetDocumentsRepositoryView> _netDocumentsRepositories = new();
    private readonly ObservableCollection<NetDocumentsCabinetView> _netDocumentsCabinets = new();
    private readonly ObservableCollection<NetDocumentsSyncedAttributeView> _syncedAttributes = new();

    private INetDocumentsAuthService? _netDocumentsAuthService;
    private NetDocumentsApiClient? _netDocumentsApiClient;
    private NetDocumentsSyncService? _netDocumentsSyncService;
    private INetDocumentsMetadataProvider? _netDocumentsMetadataProvider;

    private bool _isNetDocumentsConnected;
    private string _netDocumentsConnectedUser = string.Empty;
    private string _selectedNetDocumentsRepositoryId = string.Empty;
    private string _selectedNetDocumentsCabinetId = string.Empty;
    private string _selectedNetDocumentsCabinetName = string.Empty;
    private string _currentJobRepositoryId = string.Empty;
    private string _netDocumentsCurrentUserId = string.Empty;
    private NetDocumentsSyncedAttributeView? _selectedSyncedAttribute;

    public ObservableCollection<NetDocumentsRepositoryView> NetDocumentsRepositories => _netDocumentsRepositories;

    public ObservableCollection<NetDocumentsCabinetView> NetDocumentsCabinets => _netDocumentsCabinets;

    public ObservableCollection<NetDocumentsSyncedAttributeView> SyncedAttributes => _syncedAttributes;

    public string NetDocumentsConnectedUser
    {
        get => _netDocumentsConnectedUser;
        private set => SetField(ref _netDocumentsConnectedUser, value);
    }

    public bool IsNetDocumentsConnected
    {
        get => _isNetDocumentsConnected;
        private set
        {
            if (SetField(ref _isNetDocumentsConnected, value))
            {
                OnPropertyChanged(nameof(CanSyncNetDocumentsCabinets));
                OnPropertyChanged(nameof(CanSyncNetDocumentsAttributes));
                OnPropertyChanged(nameof(CanSelectSourceFolder));
                OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
                OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
                OnPropertyChanged(nameof(CanContinueToReviewScope));
                OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
            }
        }
    }

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
            OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
            QueueSettingsSave();
            _ = RefreshReviewScopeNetDocumentsAsync();
            _ = LoadNetDocumentsTargetContainersAsync();
        }
    }

    public string SelectedNetDocumentsCabinetId
    {
        get => _selectedNetDocumentsCabinetId;
        set
        {
            if (!SetField(ref _selectedNetDocumentsCabinetId, value))
            {
                return;
            }

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
            if (cabinet is not null && !string.Equals(NdImportCabinet, cabinet.CabinetName, StringComparison.OrdinalIgnoreCase))
            {
                NdImportCabinet = cabinet.CabinetName;
            }
            OnPropertyChanged(nameof(CanSyncNetDocumentsAttributes));
            OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
            OnPropertyChanged(nameof(CanUseWorkspaceSearchSelection));
            QueueSettingsSave();
            _ = LoadSyncedAttributesForSelectedCabinetAsync();
            _ = RefreshReviewScopeNetDocumentsAsync();
            _ = LoadNetDocumentsTargetContainersAsync();
        }
    }

    public string SelectedNetDocumentsCabinetName => _selectedNetDocumentsCabinetName;

    public string CurrentJobRepositoryId => _currentJobRepositoryId;

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

    public bool CanSyncNetDocumentsCabinets => IsNetDocumentsConnected;

    public bool CanSyncNetDocumentsAttributes =>
        IsNetDocumentsConnected &&
        !string.IsNullOrWhiteSpace(SelectedNetDocumentsRepositoryId) &&
        !string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId);

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

    private bool IsRepositoryAllowedForCurrentJob(string repositoryId)
    {
        return string.IsNullOrWhiteSpace(CurrentJobId) ||
               string.IsNullOrWhiteSpace(_currentJobRepositoryId) ||
               string.Equals(_currentJobRepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task ConnectAndSyncNetDocumentsAsync()
    {
        if (!CanConnectToNetDocuments)
        {
            StatusText = "OAuth profile for this region is not installed. Contact administrator.";
            return;
        }

        try
        {
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
            if (string.IsNullOrWhiteSpace(_selectedNetDocumentsRepositoryId) ||
                repositoryViews.All(r => !string.Equals(r.RepositoryId, _selectedNetDocumentsRepositoryId, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedNetDocumentsRepositoryId = repositoryViews[0].RepositoryId;
                OnPropertyChanged(nameof(SelectedNetDocumentsRepositoryId));
            }
        }
        else
        {
            _selectedNetDocumentsRepositoryId = string.Empty;
            OnPropertyChanged(nameof(SelectedNetDocumentsRepositoryId));
        }

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
                    _selectedNetDocumentsCabinetId = string.Empty;
                    _selectedNetDocumentsCabinetName = string.Empty;
                    OnPropertyChanged(nameof(SelectedNetDocumentsCabinetId));
                    OnPropertyChanged(nameof(SelectedNetDocumentsCabinetName));
                    return;
                }

                if (_netDocumentsCabinets.All(c => !string.Equals(c.CabinetId, _selectedNetDocumentsCabinetId, StringComparison.OrdinalIgnoreCase)))
                {
                    _selectedNetDocumentsCabinetId = _netDocumentsCabinets[0].CabinetId;
                    _selectedNetDocumentsCabinetName = _netDocumentsCabinets[0].CabinetName;
                    OnPropertyChanged(nameof(SelectedNetDocumentsCabinetId));
                    OnPropertyChanged(nameof(SelectedNetDocumentsCabinetName));
                }
            });
        });
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

        TaskDialog.ShowDialog(dialog);
    }
}

public sealed class NetDocumentsRepositoryView
{
    public NetDocumentsRepositoryView(string repositoryId, string repositoryName)
    {
        RepositoryId = repositoryId;
        RepositoryName = string.IsNullOrWhiteSpace(repositoryName) ? repositoryId : repositoryName;
    }

    public string RepositoryId { get; }

    public string RepositoryName { get; }
}

public sealed class NetDocumentsCabinetView
{
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

    public string CabinetId { get; }

    public string RepositoryId { get; }

    public string CabinetName { get; }

    public string Description { get; }

    public int? WorkspaceAttributeNum { get; }

    public string WorkspacePluralName { get; }

    public bool? AllowFileInWorkspaces { get; }
}

public sealed class NetDocumentsSyncedAttributeView
{
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

    public string CabinetId { get; }

    public int AttributeNum { get; }

    public string Name { get; }

    public string DataType { get; }

    public bool IsLookup { get; }

    public bool IsMultiValue { get; }

    public int? ParentAttributeNum { get; }

    public string TypeDisplay =>
        IsLookup
            ? IsMultiValue ? "Lookup (multi)" : "Lookup"
            : IsMultiValue ? "Text (multi)" : "Text";
}
