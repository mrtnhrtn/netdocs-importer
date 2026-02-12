namespace NetDocsImporter.Core;

public enum NetDocumentsRegion
{
    Vault,
    EU,
    DE,
    AU,
    CAN,
    Ducot
}

public sealed class NetDocumentsRegionSetting
{
    public string ApiBaseUrl { get; set; } = string.Empty;

    public string OAuthAuthorizeBaseUrl { get; set; } = string.Empty;

    public string OAuthTokenUrl { get; set; } = string.Empty;
}

public sealed class NetDocumentsConnectionSettings
{
    public NetDocumentsRegion Region { get; set; } = NetDocumentsRegion.AU;

    // Legacy fields retained for migration from older settings versions.
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecretRef { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = NetDocumentsRegionDefaults.DefaultRedirectUri;

    public bool UseSecureOAuthClientConfig { get; set; } = true;

    public string OAuthClientConfigRef { get; set; } = string.Empty;

    public string SelectedRepositoryId { get; set; } = string.Empty;

    public string SelectedCabinetId { get; set; } = string.Empty;

    public string SelectedCabinetName { get; set; } = string.Empty;

    public string SelectedTargetType { get; set; } = string.Empty;

    public string SelectedTargetId { get; set; } = string.Empty;

    public string SelectedTargetName { get; set; } = string.Empty;

    public string SelectedTargetParentWorkspaceId { get; set; } = string.Empty;

    public string SelectedTargetExtension { get; set; } = string.Empty;

    public string SelectedTargetPath { get; set; } = string.Empty;

    public string WorkspaceLookupContextJson { get; set; } = string.Empty;

    public string EffectiveProfileDefaultsJson { get; set; } = string.Empty;

    public string FavoriteTargetsJson { get; set; } = string.Empty;

    public string RecentTargetsJson { get; set; } = string.Empty;

    public string LastWorkspaceQuery { get; set; } = string.Empty;

    public Dictionary<string, NetDocumentsRegionSetting> Regions { get; set; } = NetDocumentsRegionDefaults.CreateDefaultRegionMap();

    public int? DirectUploadV1DocumentIndexPriority { get; set; } = 400;
}
