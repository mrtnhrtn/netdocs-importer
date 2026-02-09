using System.Text.Json;

namespace NetDocsImporter.Core;

public sealed class AppSettings
{
    public const string DefaultNdImportPasswordRef = "ndimport-password.dat";
    public const string DefaultNetDocumentsClientSecretRef = "netdocuments-client-secret.dat";
    public const string DefaultNetDocumentsTokenRef = "netdocuments-token.dat";
    public const string DefaultNetDocumentsOAuthClientProfilesRef = "netdocuments-oauth-client-profiles.dat";

    public int Version { get; set; } = 2;

    public string NdImportPath { get; set; } = string.Empty;

    public string NdImportHost { get; set; } = string.Empty;

    public string NdImportCabinet { get; set; } = string.Empty;

    public string NdImportUsername { get; set; } = string.Empty;

    public string NdImportDateFormat { get; set; } = "DMY";

    public string ImportExecutionMode { get; set; } = "NdImportCsv";

    public bool RememberNdImportPassword { get; set; }

    public string NdImportPasswordRef { get; set; } = string.Empty;

    public string ProfileSchemaPath { get; set; } = string.Empty;

    public NetDocumentsConnectionSettings NetDocumentsConnection { get; set; } = new();

    public static async Task<AppSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: cancellationToken);
        return settings ?? new AppSettings();
    }

    public static async Task SaveAsync(string path, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: cancellationToken);
    }
}
