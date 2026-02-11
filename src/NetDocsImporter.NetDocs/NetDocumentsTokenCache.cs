using System.Text.Json;

namespace NetDocsImporter.NetDocs;

/// <summary>
/// Stores OAuth access/refresh token state persisted by <see cref="NetDocumentsTokenStore"/>.
/// </summary>
internal sealed class NetDocumentsTokenCache
{
    /// <summary>
    /// Gets or sets the bearer access token.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the refresh token used to renew access tokens.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC expiry timestamp for <see cref="AccessToken"/>.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Gets a value indicating whether the cache currently contains a non-expired access token.
    /// </summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAtUtc > DateTime.UtcNow;

    /// <summary>
    /// Attempts to deserialize a token cache payload.
    /// </summary>
    /// <param name="json">Serialized token cache JSON.</param>
    /// <returns>Deserialized cache when payload is valid; otherwise <see langword="null"/>.</returns>
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

    /// <summary>
    /// Serializes this cache instance to JSON.
    /// </summary>
    /// <returns>Serialized cache payload.</returns>
    public string Serialize()
    {
        return JsonSerializer.Serialize(this);
    }
}
