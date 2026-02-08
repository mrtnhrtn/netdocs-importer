namespace NetDocsImporter.Core;

public static class NetDocumentsOAuthClientConfigResolution
{
    public static Dictionary<string, NetDocumentsOAuthClientConfig> MergeWithProvisionedPriority(
        IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> userProfiles,
        IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> provisionedProfiles)
    {
        var merged = new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in userProfiles)
        {
            merged[item.Key] = item.Value;
        }

        foreach (var item in provisionedProfiles)
        {
            merged[item.Key] = item.Value;
        }

        return merged;
    }

    public static Dictionary<string, NetDocumentsOAuthClientConfig> FilterUserWritableProfiles(
        IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> allProfiles,
        IReadOnlyDictionary<string, NetDocumentsOAuthClientConfig> provisionedProfiles)
    {
        var filtered = new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in allProfiles)
        {
            if (!provisionedProfiles.ContainsKey(item.Key))
            {
                filtered[item.Key] = item.Value;
            }
        }

        return filtered;
    }
}
