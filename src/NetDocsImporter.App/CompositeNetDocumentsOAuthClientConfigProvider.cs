using NetDocsImporter.Core;

namespace NetDocsImporter.App;

internal sealed class CompositeNetDocumentsOAuthClientConfigProvider : INetDocumentsOAuthClientConfigProvider
{
    private readonly INetDocumentsOAuthClientConfigProvider _provisionedProvider;
    private readonly INetDocumentsOAuthClientConfigProvider _userProvider;

    public CompositeNetDocumentsOAuthClientConfigProvider(
        INetDocumentsOAuthClientConfigProvider provisionedProvider,
        INetDocumentsOAuthClientConfigProvider userProvider)
    {
        _provisionedProvider = provisionedProvider;
        _userProvider = userProvider;
    }

    public async Task<IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var userProfiles = await _userProvider.LoadAsync(cancellationToken);
        var provisionedProfiles = await _provisionedProvider.LoadAsync(cancellationToken);
        return NetDocumentsOAuthClientConfigResolution.MergeWithProvisionedPriority(userProfiles, provisionedProfiles);
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> profiles, CancellationToken cancellationToken = default)
    {
        var provisionedProfiles = await _provisionedProvider.LoadAsync(cancellationToken);
        var userOnly = NetDocumentsOAuthClientConfigResolution.FilterUserWritableProfiles(profiles, provisionedProfiles);
        await _userProvider.SaveAsync(userOnly, cancellationToken);
    }
}
