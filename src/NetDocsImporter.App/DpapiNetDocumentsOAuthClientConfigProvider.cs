using System.Text.Json;
using NetDocsImporter.Core;
using NetDocsImporter.Core.Security;

namespace NetDocsImporter.App;

/// <summary>
/// Persists region-scoped OAuth client profiles in a DPAPI-protected secret store payload.
/// </summary>
internal sealed class DpapiNetDocumentsOAuthClientConfigProvider : INetDocumentsOAuthClientConfigProvider
{
    private readonly SecretStore _secretStore;
    private readonly string _secretName;

    /// <summary>
    /// Initializes the DPAPI-backed provider.
    /// </summary>
    /// <param name="secretStore">Secret store abstraction for encrypted payload persistence.</param>
    /// <param name="secretName">Secret name used to store profile payload JSON.</param>
    public DpapiNetDocumentsOAuthClientConfigProvider(SecretStore secretStore, string secretName)
    {
        _secretStore = secretStore;
        _secretName = secretName;
    }

    /// <summary>
    /// Loads OAuth client profiles from the protected secret payload.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel secret-store operations.</param>
    /// <returns>Region-keyed profile dictionary.</returns>
    public async Task<IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _secretStore.ReadSecretAsync(_secretName, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, NetDocumentsOAuthClientConfig>>(payload);
            return data ?? new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Saves OAuth client profiles to the protected secret payload.
    /// </summary>
    /// <param name="profiles">Region-keyed profiles to persist.</param>
    /// <param name="cancellationToken">Token used to cancel secret-store operations.</param>
    public async Task SaveAsync(IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> profiles, CancellationToken cancellationToken = default)
    {
        var normalized = new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in profiles)
        {
            normalized[item.Key] = item.Value;
        }

        var payload = JsonSerializer.Serialize(normalized);
        await _secretStore.WriteSecretAsync(_secretName, payload, cancellationToken);
    }
}
