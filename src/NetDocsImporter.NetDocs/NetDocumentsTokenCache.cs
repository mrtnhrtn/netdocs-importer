using System.Text.Json;

namespace NetDocsImporter.NetDocs;

internal sealed class NetDocumentsTokenCache
{
    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; } = DateTime.MinValue;

    public bool IsUsable => !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAtUtc > DateTime.UtcNow;

    public static NetDocumentsTokenCache? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NetDocumentsTokenCache>(json);
        }
        catch
        {
            return null;
        }
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(this);
    }
}
