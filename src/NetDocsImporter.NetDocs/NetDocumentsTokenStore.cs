using System.Security.Cryptography;
using System.Text;

namespace NetDocsImporter.NetDocs;

internal sealed class NetDocumentsTokenStore
{
    private readonly string _tokenPath;

    public NetDocumentsTokenStore(string tokenPath)
    {
        _tokenPath = tokenPath ?? throw new ArgumentNullException(nameof(tokenPath));
    }

    public async Task<NetDocumentsTokenCache?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!File.Exists(_tokenPath))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_tokenPath, cancellationToken);
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plaintext);
            return NetDocumentsTokenCache.Deserialize(json);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task WriteAsync(NetDocumentsTokenCache cache, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.GetDirectoryName(_tokenPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plaintext = Encoding.UTF8.GetBytes(cache.Serialize());
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_tokenPath, encrypted, cancellationToken);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_tokenPath))
        {
            File.Delete(_tokenPath);
        }

        return Task.CompletedTask;
    }
}
