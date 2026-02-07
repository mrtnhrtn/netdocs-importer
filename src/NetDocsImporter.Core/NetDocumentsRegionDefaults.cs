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
                ApiBaseUrl = "https://api.vault.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://vault.netdocuments.com",
                OAuthTokenUrl = "https://vault.netdocuments.com/oauth/token"
            },
            NetDocumentsRegion.EU => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.eu.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://eu.netdocuments.com",
                OAuthTokenUrl = "https://eu.netdocuments.com/oauth/token"
            },
            NetDocumentsRegion.DE => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.de.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://de.netdocuments.com",
                OAuthTokenUrl = "https://de.netdocuments.com/oauth/token"
            },
            NetDocumentsRegion.AU => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.au.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://au.netdocuments.com",
                OAuthTokenUrl = "https://au.netdocuments.com/oauth/token"
            },
            NetDocumentsRegion.Ducot => new NetDocumentsRegionSetting
            {
                ApiBaseUrl = "https://api.ducot.netdocuments.com",
                OAuthAuthorizeBaseUrl = "https://ducot.netdocuments.com",
                OAuthTokenUrl = "https://ducot.netdocuments.com/oauth/token"
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
            value.OAuthAuthorizeBaseUrl = string.IsNullOrWhiteSpace(value.OAuthAuthorizeBaseUrl) ? fallback.OAuthAuthorizeBaseUrl : value.OAuthAuthorizeBaseUrl;
            value.OAuthTokenUrl = string.IsNullOrWhiteSpace(value.OAuthTokenUrl) ? fallback.OAuthTokenUrl : value.OAuthTokenUrl;
        }
    }
}

