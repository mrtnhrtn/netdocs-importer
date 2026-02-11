namespace NetDocsImporter.NetDocs;

/// <summary>
/// Defines NetDocuments OAuth authentication operations used by API clients and sync services.
/// </summary>
public interface INetDocumentsAuthService
{
    /// <summary>
    /// Starts an interactive OAuth sign-in flow and persists tokens on success.
    /// </summary>
    /// <param name="context">OAuth client settings for the target region.</param>
    /// <param name="cancellationToken">Token used to cancel browser/listener flow.</param>
    Task SignInInteractiveAsync(NetDocumentsAuthContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a valid bearer token, optionally forcing refresh from the OAuth token endpoint.
    /// </summary>
    /// <param name="context">OAuth client settings for the target region.</param>
    /// <param name="forceRefresh"><see langword="true"/> to refresh even when a cached token is still valid.</param>
    /// <param name="cancellationToken">Token used to cancel token retrieval.</param>
    /// <returns>A non-empty bearer access token.</returns>
    Task<string> GetAccessTokenAsync(
        NetDocumentsAuthContext context,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears local authentication state and removes persisted tokens.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel sign-out cleanup.</param>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
