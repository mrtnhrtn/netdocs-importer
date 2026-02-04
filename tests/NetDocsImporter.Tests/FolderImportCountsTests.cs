using NetDocsImporter.Data;

namespace NetDocsImporter.Tests;

public class FolderImportCountsTests
{
    [Fact]
    public async Task CountsDescendantsWithOverrides()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var rootId = await InsertFolderAsync(store, jobId, "C:\\data", string.Empty, null, 0, "include");
        var excludedId = await InsertFolderAsync(store, jobId, "C:\\data\\excluded", "excluded", rootId, 1, "exclude");
        var overrideId = await InsertFolderAsync(store, jobId, "C:\\data\\excluded\\override", Path.Combine("excluded", "override"), excludedId, 2, "include");

        var file = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data\\excluded\\override\\file.txt",
            Path.Combine("excluded", "override", "file.txt"),
            100,
            DateTime.UtcNow,
            false,
            overrideId);
        await store.InsertFileAsync(file);

        var counts = await store.GetFolderImportCountsForJobAsync(jobId);
        var map = counts.ToDictionary(c => c.FolderId);

        Assert.True(map[rootId].EffectiveIncluded);
        Assert.Equal(1, map[rootId].IncludedDescendantFileCount);

        Assert.False(map[excludedId].EffectiveIncluded);
        Assert.Equal(1, map[excludedId].IncludedDescendantFileCount);

        Assert.True(map[overrideId].EffectiveIncluded);
        Assert.Equal(1, map[overrideId].IncludedFileCount);
        Assert.Equal(1, map[overrideId].IncludedDescendantFileCount);

        CleanupTempRoot(tempRoot);
    }

    [Fact]
    public async Task MarksIncludedFolderWithNoFilesAsEffectivelyEmpty()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var rootId = await InsertFolderAsync(store, jobId, "C:\\data", string.Empty, null, 0, "include");
        var emptyId = await InsertFolderAsync(store, jobId, "C:\\data\\empty", "empty", rootId, 1, "include");

        var counts = await store.GetFolderImportCountsForJobAsync(jobId);
        var map = counts.ToDictionary(c => c.FolderId);

        Assert.True(map[emptyId].EffectiveIncluded);
        Assert.Equal(0, map[emptyId].IncludedDescendantFileCount);
        Assert.Equal(0, map[emptyId].IncludedFileCount);

        CleanupTempRoot(tempRoot);
    }

    private static async Task<string> InsertFolderAsync(
        JobStore store,
        string jobId,
        string fullPath,
        string relativePath,
        string? parentFolderId,
        int depth,
        string importMode)
    {
        var folderId = Guid.NewGuid().ToString("N");
        var folder = new FolderRecord(
            folderId,
            jobId,
            fullPath,
            relativePath,
            parentFolderId,
            depth,
            true,
            false,
            DateTime.UtcNow,
            importMode,
            "inherit");

        await store.InsertFolderAsync(folder);
        return folderId;
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
