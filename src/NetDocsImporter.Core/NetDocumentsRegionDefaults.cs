namespace NetDocsImporter.Core;

public static class NetDocumentsRegionDefaults
{
    public const string DefaultRedirectUri = "http://127.0.0.1:8400/callback";

    public static NetDocumentsRegionSetting GetDefaults(NetDocumentsRegion region)
    {
        return region switch
        {
            NetDocumentsRegion.Vault => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.vault.netvoyage.com",
                OAuthAuthorizeBaseUrl = "https://api.vault.netvoyage.com/neWeb2/OAuth.aspx",
                OAuthTokenUrl = "https://api.vault.netvoyage.com/v1/OAuth"
            },
            NetDocumentsRegion.EU => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.eu.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://api.eu.netdocuments.com/neWeb2/OAuth.aspx",
                OAuthTokenUrl = "https://api.eu.netdocuments.com/v1/OAuth"
            },
            NetDocumentsRegion.DE => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.de.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://api.de.netdocuments.com/neWeb2/OAuth.aspx",
                OAuthTokenUrl = "https://api.de.netdocuments.com/v1/OAuth"
            },
            NetDocumentsRegion.AU => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.au.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://api.au.netdocuments.com/neWeb2/OAuth.aspx",
                OAuthTokenUrl = "https://api.au.netdocuments.com/v1/OAuth"
            },
            NetDocumentsRegion.Ducot => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.ducot.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://api.ducot.netdocuments.com/neWeb2/OAuth.aspx",
                OAuthTokenUrl = "https://api.ducot.netdocuments.com/v1/OAuth"
            },
            _ => new NetDocumentsRegionSetting()
        };
    }

    public static Dictionary<string, NetDocumentsRegionSetting> CreateDefaultRegionMap()
    {
        var map = new Dictionary<string, NetDocumentsRegionSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in Enum.GetValues<NetDocumentsRegion>())
        {
            map[region.ToString()] = GetDefaults(region);
        }

        return map;
    }

    public static void EnsureDefaults(NetDocumentsConnectionSettings settings)
    {
        settings.RedirectUri = string.IsNullOrWhiteSpace(settings.RedirectUri)
            ? DefaultRedirectUri
            : settings.RedirectUri;

        settings.Regions ??= new Dictionary<string, NetDocumentsRegionSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in Enum.GetValues<NetDocumentsRegion>())
        {
            var key = region.ToString();
            if (!settings.Regions.TryGetValue(key, out var value) || value is null)
            {
                settings.Regions[key] = GetDefaults(region);
                continue;
            }

            var fallback = GetDefaults(region);
            value.ApiBaseUrl = string.IsNullOrWhiteSpace(value.ApiBaseUrl) ? fallback.ApiBaseUrl : value.ApiBaseUrl;
            value.OAuthAuthorizeBaseUrl = NormalizeAuthorizeUrl(value.OAuthAuthorizeBaseUrl, fallback.OAuthAuthorizeBaseUrl);
            value.OAuthTokenUrl = NormalizeTokenUrl(value.OAuthTokenUrl, fallback.OAuthTokenUrl);
        }
    }

    private static string NormalizeAuthorizeUrl(string current, string fallback)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return fallback;
        }

        if (Uri.TryCreate(current, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(path) || string.Equals(path, "", StringComparison.Ordinal))
            {
                return fallback;
            }

            if (string.Equals(path, "/oauth", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }
        }

        if (current.EndsWith("/oauth/authorize", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = current[..^"/oauth/authorize".Length].TrimEnd('/');
            return $"{baseUrl}/neWeb2/OAuth.aspx";
        }

        if (current.EndsWith("/OAuth/authorize", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = current[..^"/OAuth/authorize".Length].TrimEnd('/');
            return $"{baseUrl}/neWeb2/OAuth.aspx";
        }

        if (!current.Contains("OAuth.aspx", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return current;
    }

    private static string NormalizeTokenUrl(string current, string fallback)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return fallback;
        }

        if (Uri.TryCreate(current, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(path) || string.Equals(path, "", StringComparison.Ordinal))
            {
                return fallback;
            }

            if (string.Equals(path, "/oauth", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }
        }

        if (current.EndsWith("/oauth/token", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = current[..^"/oauth/token".Length].TrimEnd('/');
            return $"{baseUrl}/v1/OAuth";
        }

        if (!current.EndsWith("/v1/OAuth", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        if (Uri.TryCreate(current, UriKind.Absolute, out var currentUri) &&
            Uri.TryCreate(fallback, UriKind.Absolute, out var fallbackUri) &&
            !string.Equals(currentUri.Host, fallbackUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return current;
    }
}
