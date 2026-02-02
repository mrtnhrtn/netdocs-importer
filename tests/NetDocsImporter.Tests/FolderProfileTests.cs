using NetDocsImporter.Data;

namespace NetDocsImporter.Tests;

public class FolderProfileTests
{
    [Fact]
    public async Task ApplyProfileToChildrenOnlyUpdatesInherit()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var root = new FolderRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data",
            string.Empty,
            null,
            0,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "override");
        await store.InsertFolderAsync(root);

        var childInherit = new FolderRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data\\a",
            "a",
            root.FolderId,
            1,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "inherit");
        await store.InsertFolderAsync(childInherit);

        var childOverride = new FolderRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data\\b",
            "b",
            root.FolderId,
            1,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "override");
        await store.InsertFolderAsync(childOverride);

        await store.UpsertFolderProfileAsync(jobId, root.FolderId, "{\"key\":\"value\"}");
        await store.ApplyProfileToDescendantsAsync(jobId, root.FolderId, "{\"key\":\"value\"}");

        var children = await store.GetChildFoldersAsync(jobId, root.FolderId);
        var inheritFolder = children.Single(f => f.FolderId == childInherit.FolderId);
        var overrideFolder = children.Single(f => f.FolderId == childOverride.FolderId);

        Assert.Equal("override", inheritFolder.ProfileMode);
        Assert.Equal("override", overrideFolder.ProfileMode);

        var payloadInherit = await store.GetFolderProfilePayloadAsync(childInherit.FolderId);
        var payloadOverride = await store.GetFolderProfilePayloadAsync(childOverride.FolderId);

        Assert.NotNull(payloadInherit);
        Assert.Null(payloadOverride);

        CleanupTempRoot(tempRoot);
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }
}
