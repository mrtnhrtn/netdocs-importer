using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetDocsImporter.Core;

namespace NetDocsImporter.App;

internal sealed class ProvisionedNetDocumentsOAuthClientConfigProvider : INetDocumentsOAuthClientConfigProvider
{
    private readonly string _path;

    public ProvisionedNetDocumentsOAuthClientConfigProvider(string path)
    {
        _path = path;
    }

    public async Task<IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_path))
        {
            return new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken);
            var payload = TryUnprotect(encrypted);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
            }

            var profiles = JsonSerializer.Deserialize<Dictionary<string, NetDocumentsOAuthClientConfig>>(
                payload,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            return profiles ?? new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> profiles, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var normalized = new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in profiles)
        {
            normalized[item.Key] = item.Value;
        }

        var payload = JsonSerializer.Serialize(normalized);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(payload), null, DataProtectionScope.LocalMachine);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(_path, encrypted, cancellationToken);
    }

    private static string? TryUnprotect(byte[] encrypted)
    {
        try
        {
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            try
            {
                // Dev/test fallback for profiles encrypted under CurrentUser scope.
                var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }
}
