using Microsoft.Data.Sqlite;
using NetDocsImporter.Data;

namespace NetDocsImporter.Tests;

public class DataPersistenceTests
{
    [Fact]
    public async Task InitializesSchemaWithTablesAndIndexes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobs.db");

        try
        {
            var store = new JobStore(dbPath);
            await store.InitializeAsync();

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();

            var tables = await GetNamesAsync(connection, "table");
            var indexes = await GetNamesAsync(connection, "index");

            Assert.Contains("Jobs", tables);
            Assert.Contains("Files", tables);
            Assert.Contains("Transfers", tables);
            Assert.Contains("Folders", tables);
            Assert.Contains("FolderRules", tables);
            Assert.Contains("FolderProfiles", tables);
            Assert.Contains("IX_Files_JobId", indexes);
            Assert.Contains("IX_Files_RelativePath", indexes);
            Assert.Contains("IX_Files_FolderId", indexes);
            Assert.Contains("IX_Transfers_JobId", indexes);
            Assert.Contains("IX_Transfers_FileId", indexes);
            Assert.Contains("IX_Folders_JobId", indexes);
            Assert.Contains("IX_Folders_ParentFolderId", indexes);
            Assert.Contains("IX_Folders_RelativePath", indexes);
            Assert.Contains("IX_FolderProfiles_FolderId", indexes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task InsertsAndRetrievesJobsAndFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobs.db");

        try
        {
            var store = new JobStore(dbPath);
            await store.InitializeAsync();

            var jobId = Guid.NewGuid().ToString("N");
            var created = new DateTime(2026, 2, 2, 12, 30, 0, DateTimeKind.Utc);
            var job = new JobRecord(jobId, created, "C:\\data", "Scanning");
            await store.InsertJobAsync(job);

            var file1 = new FileRecord(
                Guid.NewGuid().ToString("N"),
                jobId,
                "C:\\data\\file-a.txt",
                "file-a.txt",
                120,
                created,
                false,
                null,
                "inherit",
                null);
            var file2 = new FileRecord(
                Guid.NewGuid().ToString("N"),
                jobId,
                "C:\\data\\folder\\file-b.txt",
                "folder\\file-b.txt",
                2048,
                created.AddMinutes(2),
                true,
                null,
                "inherit",
                null);

            await store.InsertFileAsync(file1);
            await store.InsertFileAsync(file2);

            var storedJob = await store.GetJobAsync(jobId);
            var files = await store.GetFilesForJobAsync(jobId);

            Assert.NotNull(storedJob);
            Assert.Equal(job.JobId, storedJob!.JobId);
            Assert.Equal(job.SourceRoot, storedJob.SourceRoot);
            Assert.Equal(job.Status, storedJob.Status);

            Assert.Equal(2, files.Count);
            Assert.Contains(files, file => file.FullPath == file1.FullPath);
            Assert.Contains(files, file => file.IsLargeWarning);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private static async Task<HashSet<string>> GetNamesAsync(SqliteConnection connection, string type)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
        command.Parameters.AddWithValue("$type", type);

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
