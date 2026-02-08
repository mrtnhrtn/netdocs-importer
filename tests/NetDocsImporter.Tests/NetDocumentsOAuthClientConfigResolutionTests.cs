using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public sealed class NetDocumentsOAuthClientConfigResolutionTests
{
    [Fact]
    public void MergeWithProvisionedPriority_UsesProvisionedWhenRegionExistsInBoth()
    {
        var user = new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["AU"] = new() { Region = NetDocumentsRegion.AU, ClientId = "user-au" },
            ["CAN"] = new() { Region = NetDocumentsRegion.CAN, ClientId = "user-can" }
        };
        var provisioned = new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["AU"] = new() { Region = NetDocumentsRegion.AU, ClientId = "machine-au" }
        };

        var merged = NetDocumentsOAuthClientConfigResolution.MergeWithProvisionedPriority(user, provisioned);

        Assert.Equal("machine-au", merged["AU"].ClientId);
        Assert.Equal("user-can", merged["CAN"].ClientId);
    }

    [Fact]
    public void FilterUserWritableProfiles_RemovesProvisionedRegions()
    {
        var all = new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["AU"] = new() { Region = NetDocumentsRegion.AU, ClientId = "machine-au" },
            ["CAN"] = new() { Region = NetDocumentsRegion.CAN, ClientId = "user-can" }
        };
        var provisioned = new Dictionary<string, NetDocumentsOAuthClientConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["AU"] = new() { Region = NetDocumentsRegion.AU, ClientId = "machine-au" }
        };

        var filtered = NetDocumentsOAuthClientConfigResolution.FilterUserWritableProfiles(all, provisioned);

        Assert.False(filtered.ContainsKey("AU"));
        Assert.True(filtered.ContainsKey("CAN"));
        Assert.Equal("user-can", filtered["CAN"].ClientId);
    }
}
