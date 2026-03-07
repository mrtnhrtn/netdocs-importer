namespace NetDocsImporter.NetDocs;

/// <summary>
/// Represents OAuth client settings used to authenticate against a NetDocuments region.
/// </summary>
public sealed class NetDocumentsAuthContext
{
    /// <summary>
    /// Gets the OAuth authorization endpoint base URL.
    /// </summary>
    public string OAuthAuthorizeBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets the OAuth token endpoint URL.
    /// </summary>
    public string OAuthTokenUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets the OAuth client identifier.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the OAuth client secret.
    /// </summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>
    /// Gets the configured loopback redirect URI.
    /// </summary>
    public string RedirectUri { get; init; } = string.Empty;
}
