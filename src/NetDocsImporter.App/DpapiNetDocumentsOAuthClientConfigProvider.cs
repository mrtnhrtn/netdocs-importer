using System.Text.Json;
using NetDocsImporter.Core;
using NetDocsImporter.Core.Security;

namespace NetDocsImporter.App;

internal sealed class DpapiNetDocumentsOAuthClientConfigProvider : INetDocumentsOAuthClientConfigProvider
{
    private readonly SecretStore _secretStore;
    private readonly string _secretName;

    public DpapiNetDocumentsOAuthClientConfigProvider(SecretStore secretStore, string secretName)
    {
        _secretStore = secretStore;
        _secretName = secretName;
    }

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
