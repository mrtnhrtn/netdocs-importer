using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public class NetDocumentsTargetBrowserLogicTests
{
    [Theory]
    [InlineData("workspace", NdTargetType.Workspace)]
    [InlineData("Workspace Filter", NdTargetType.WorkspaceFilter)]
    [InlineData("folder", NdTargetType.Folder)]
    public void NormalizeSupportedType_RecognizesAllowedTypes(string raw, NdTargetType expected)
    {
        var result = NdTargetBrowserLogic.NormalizeSupportedType(raw, hasWorkspaceIdHint: false);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MergeRecentTargets_DeduplicatesAndKeepsLatest()
    {
        var selection = new NdTargetSelection { Type = NdTargetType.Folder, Id = "f1", Name = "Folder 1" };
        var server = new[]
        {
            new NdTargetRecentItem { Selection = selection, LastUsedUtc = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc), Source = NdTargetSource.Server }
        };
        var local = new[]
        {
            new NdTargetRecentItem { Selection = selection, LastUsedUtc = new DateTime(2026, 2, 2, 10, 0, 0, DateTimeKind.Utc), Source = NdTargetSource.Local }
        };

        var merged = NdTargetBrowserLogic.MergeRecentTargets(server, local);

        Assert.Single(merged);
        Assert.Equal(new DateTime(2026, 2, 2, 10, 0, 0, DateTimeKind.Utc), merged[0].LastUsedUtc);
    }

    [Fact]
    public void MergeFavoriteTargets_ContainsUnion()
    {
        var server = new[]
        {
            new NdTargetFavoriteItem
            {
                Selection = new NdTargetSelection { Type = NdTargetType.Workspace, Id = "w1", Name = "Workspace 1" },
                PinnedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                Source = NdTargetSource.Server
            }
        };

        var local = new[]
        {
            new NdTargetFavoriteItem
            {
                Selection = new NdTargetSelection { Type = NdTargetType.Folder, Id = "f1", Name = "Folder 1" },
                PinnedUtc = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
                Source = NdTargetSource.Local
            }
        };

        var merged = NdTargetBrowserLogic.MergeFavoriteTargets(server, local);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void RecentAndFavoriteSerialization_RoundTrips()
    {
        var recents = new[]
        {
            new NdTargetRecentItem
            {
                Selection = new NdTargetSelection { Type = NdTargetType.WorkspaceFilter, Id = "wf1", Name = "WF" },
                LastUsedUtc = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc),
                Source = NdTargetSource.Local
            }
        };

        var favorites = new[]
        {
            new NdTargetFavoriteItem
            {
                Selection = new NdTargetSelection { Type = NdTargetType.Folder, Id = "f9", Name = "Folder 9" },
                PinnedUtc = new DateTime(2026, 2, 4, 0, 0, 0, DateTimeKind.Utc),
                Source = NdTargetSource.Local
            }
        };

        var recentsJson = NdTargetBrowserLogic.SerializeRecentTargets(recents);
        var favoritesJson = NdTargetBrowserLogic.SerializeFavoriteTargets(favorites);

        var recentsRoundTrip = NdTargetBrowserLogic.DeserializeRecentTargets(recentsJson);
        var favoritesRoundTrip = NdTargetBrowserLogic.DeserializeFavoriteTargets(favoritesJson);

        Assert.Single(recentsRoundTrip);
        Assert.Single(favoritesRoundTrip);
        Assert.Equal("wf1", recentsRoundTrip[0].Selection.Id);
        Assert.Equal("f9", favoritesRoundTrip[0].Selection.Id);
    }
}
