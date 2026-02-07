namespace NetDocsImporter.NetDocs;

public sealed class NetDocumentsAuthContext
{
    public string OAuthAuthorizeBaseUrl { get; init; } = string.Empty;

    public string OAuthTokenUrl { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    public string RedirectUri { get; init; } = string.Empty;
}
