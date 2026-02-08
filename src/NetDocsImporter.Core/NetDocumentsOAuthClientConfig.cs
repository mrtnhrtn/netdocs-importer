namespace NetDocsImporter.Core;

public sealed class NetDocumentsOAuthClientConfig
{
    public NetDocumentsRegion Region { get; set; } = NetDocumentsRegion.AU;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = NetDocumentsRegionDefaults.DefaultRedirectUri;

    public string ApiBaseUrl { get; set; } = string.Empty;

    public string OAuthAuthorizeBaseUrl { get; set; } = string.Empty;

    public string OAuthTokenUrl { get; set; } = string.Empty;
}
