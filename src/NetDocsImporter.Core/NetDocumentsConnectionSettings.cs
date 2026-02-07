namespace NetDocsImporter.Core;

public enum NetDocumentsRegion
{
    Vault,
    EU,
    DE,
    AU,
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

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecretRef { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = NetDocumentsRegionDefaults.DefaultRedirectUri;

    public string SelectedRepositoryId { get; set; } = string.Empty;

    public string SelectedCabinetId { get; set; } = string.Empty;

    public string SelectedCabinetName { get; set; } = string.Empty;

    public Dictionary<string, NetDocumentsRegionSetting> Regions { get; set; } = NetDocumentsRegionDefaults.CreateDefaultRegionMap();
}
