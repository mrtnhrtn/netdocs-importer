using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.Tests;

public class FolderTreeTests
{
    [Fact]
    public async Task PersistsFolderRowsAndAssociatesFiles()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var rootFolderId = Guid.NewGuid().ToString("N");
        var folder = new FolderRecord(
            rootFolderId,
            jobId,
            "C:\\data",
            string.Empty,
            null,
            0,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "inherit");
        await store.InsertFolderAsync(folder);

        var file = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data\\file.txt",
            "file.txt",
            123,
            DateTime.UtcNow,
            false,
            rootFolderId);
        await store.InsertFileAsync(file);

        var files = await store.GetFilesForJobAsync(jobId);
        Assert.Single(files);
        Assert.Equal(rootFolderId, files[0].FolderId);

        CleanupTempRoot(tempRoot);
    }

    [Fact]
    public Task EffectiveInclusionInheritsOverrides()
    {
        var provider = new FakeFolderProvider();
        var rootRecord = new FolderRecord(
            "root",
            "job",
            "C:\\data",
            string.Empty,
            null,
            0,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "inherit");
        var childRecord = new FolderRecord(
            "child",
            "job",
            "C:\\data\\child",
            "child",
            "root",
            1,
            true,
            false,
            DateTime.UtcNow,
            "inherit",
            "inherit");

        var root = new FolderNodeViewModel(provider, _ => { }, "job", rootRecord, null, 0);
        var child = new FolderNodeViewModel(provider, _ => { }, "job", childRecord, root, 0);
        root.Children.Add(child);

        Assert.True(root.EffectiveIncluded);
        Assert.True(child.EffectiveIncluded);

        root.SetImportMode("exclude");

        Assert.False(root.EffectiveIncluded);
        Assert.False(child.EffectiveIncluded);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task QueriesChildrenByParentFolderId()
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
            "inherit");
        await store.InsertFolderAsync(root);

        var child1 = new FolderRecord(
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
        var child2 = new FolderRecord(
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
            "inherit");

        await store.InsertFolderAsync(child1);
        await store.InsertFolderAsync(child2);

        var roots = await store.GetChildFoldersAsync(jobId, null);
        Assert.Single(roots);

        var children = await store.GetChildFoldersAsync(jobId, root.FolderId);
        Assert.Equal(2, children.Count);

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

    private sealed class FakeFolderProvider : IFolderTreeProvider
    {
        public Task<IReadOnlyList<FolderRecord>> GetChildFoldersAsync(string jobId, string? parentFolderId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FolderRecord>>(Array.Empty<FolderRecord>());
        }

        public Task<IReadOnlyList<FileRecord>> GetChildFilesAsync(string jobId, string folderId, int limit, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FileRecord>>(Array.Empty<FileRecord>());
        }

        public Task UpdateFolderOverrideAsync(string folderId, bool isOverride, bool isIncluded, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UpdateFolderImportModeAsync(string folderId, string importMode, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task AddFolderRuleAsync(string jobId, string folderId, string ruleType, string scope, string? notes, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
