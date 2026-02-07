namespace NetDocsImporter.NetDocs;

public interface INetDocumentsAuthService
{
    Task SignInInteractiveAsync(NetDocumentsAuthContext context, CancellationToken cancellationToken = default);

    Task<string> GetAccessTokenAsync(
        NetDocumentsAuthContext context,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);
}
