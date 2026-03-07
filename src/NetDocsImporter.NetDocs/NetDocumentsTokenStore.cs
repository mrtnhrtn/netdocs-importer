using System.Security.Cryptography;
using System.Text;

namespace NetDocsImporter.NetDocs;

/// <summary>
/// Persists NetDocuments OAuth tokens encrypted with Windows DPAPI in current-user scope.
/// </summary>
internal sealed class NetDocumentsTokenStore
{
    private readonly string _tokenPath;

    /// <summary>
    /// Initializes a token store bound to a single token file path.
    /// </summary>
    /// <param name="tokenPath">File path used to read/write encrypted token payloads.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tokenPath"/> is null.</exception>
    public NetDocumentsTokenStore(string tokenPath)
    {
        _tokenPath = tokenPath ?? throw new ArgumentNullException(nameof(tokenPath));
    }

    /// <summary>
    /// Reads and decrypts token state from disk.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel file IO.</param>
    /// <returns>Token cache when available and decryptable; otherwise <see langword="null"/>.</returns>
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

    /// <summary>
    /// Encrypts and writes token state to disk.
    /// </summary>
    /// <param name="cache">Token cache to persist.</param>
    /// <param name="cancellationToken">Token used to cancel file IO.</param>
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

    /// <summary>
    /// Deletes persisted token state when present.
    /// </summary>
    /// <param name="cancellationToken">Unused cancellation token for API consistency.</param>
    /// <returns>A completed task.</returns>
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_tokenPath))
        {
            File.Delete(_tokenPath);
        }

        return Task.CompletedTask;
    }
}
