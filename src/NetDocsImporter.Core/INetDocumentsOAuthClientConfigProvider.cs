namespace NetDocsImporter.Core;

public interface INetDocumentsOAuthClientConfigProvider
{
    Task<IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> profiles, CancellationToken cancellationToken = default);
}
