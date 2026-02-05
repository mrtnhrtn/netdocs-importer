using System.Security.Cryptography;
using System.Text;

namespace NetDocsImporter.Core.Security;

public sealed class SecretStore
{
    private readonly string _secretsDirectory;

    public SecretStore(string secretsDirectory)
    {
        _secretsDirectory = secretsDirectory;
    }

    public string GetSecretPath(string secretName)
    {
        return Path.Combine(_secretsDirectory, secretName);
    }

    public async Task<string?> ReadSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = GetSecretPath(secretName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task WriteSecretAsync(string secretName, string secret, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_secretsDirectory);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        var path = GetSecretPath(secretName);
        await File.WriteAllBytesAsync(path, encrypted, cancellationToken);
    }

    public void DeleteSecret(string secretName)
    {
        var path = GetSecretPath(secretName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
