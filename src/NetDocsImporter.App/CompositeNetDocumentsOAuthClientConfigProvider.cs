using NetDocsImporter.Core;

namespace NetDocsImporter.App;

/// <summary>
/// Resolves OAuth client profiles by combining provisioned machine profiles with user-writable fallback profiles.
/// </summary>
internal sealed class CompositeNetDocumentsOAuthClientConfigProvider : INetDocumentsOAuthClientConfigProvider
{
    private readonly INetDocumentsOAuthClientConfigProvider _provisionedProvider;
    private readonly INetDocumentsOAuthClientConfigProvider _userProvider;

    /// <summary>
    /// Initializes the composite provider.
    /// </summary>
    /// <param name="provisionedProvider">Read-only provider for machine-provisioned profiles.</param>
    /// <param name="userProvider">User-scoped provider used for writable profile persistence.</param>
    public CompositeNetDocumentsOAuthClientConfigProvider(
        INetDocumentsOAuthClientConfigProvider provisionedProvider,
        INetDocumentsOAuthClientConfigProvider userProvider)
    {
        _provisionedProvider = provisionedProvider;
        _userProvider = userProvider;
    }

    /// <summary>
    /// Loads merged profiles with provisioned values taking precedence over user profiles.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel provider operations.</param>
    /// <returns>Merged region-keyed profile dictionary.</returns>
    public async Task<IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var userProfiles = await _userProvider.LoadAsync(cancellationToken);
        var provisionedProfiles = await _provisionedProvider.LoadAsync(cancellationToken);
        return NetDocumentsOAuthClientConfigResolution.MergeWithProvisionedPriority(userProfiles, provisionedProfiles);
    }

    /// <summary>
    /// Saves only user-writable profiles while preserving provisioned machine profiles.
    /// </summary>
    /// <param name="profiles">Region-keyed profiles to persist.</param>
    /// <param name="cancellationToken">Token used to cancel provider operations.</param>
    public async Task SaveAsync(IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> profiles, CancellationToken cancellationToken = default)
    {
        var provisionedProfiles = await _provisionedProvider.LoadAsync(cancellationToken);
        var userOnly = NetDocumentsOAuthClientConfigResolution.FilterUserWritableProfiles(profiles, provisionedProfiles);
        await _userProvider.SaveAsync(userOnly, cancellationToken);
    }
}
