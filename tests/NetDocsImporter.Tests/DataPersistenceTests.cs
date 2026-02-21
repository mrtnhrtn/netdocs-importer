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
            Assert.Contains("NetDocumentsCabinets", tables);
            Assert.Contains("NetDocumentsAttributes", tables);
            Assert.Contains("NetDocumentsLookupValues", tables);
            Assert.Contains("IX_Files_JobId", indexes);
            Assert.Contains("IX_Files_RelativePath", indexes);
            Assert.Contains("IX_Files_FolderId", indexes);
            Assert.Contains("IX_Transfers_JobId", indexes);
            Assert.Contains("IX_Transfers_FileId", indexes);
            Assert.Contains("IX_Folders_JobId", indexes);
            Assert.Contains("IX_Folders_ParentFolderId", indexes);
            Assert.Contains("IX_Folders_RelativePath", indexes);
            Assert.Contains("IX_FolderProfiles_FolderId", indexes);
            Assert.Contains("IX_NetDocumentsCabinets_Region", indexes);
            Assert.Contains("IX_NetDocumentsAttributes_CabinetId", indexes);
            Assert.Contains("IX_NetDocumentsLookupValues_CabinetAttr", indexes);
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
    public async Task BuildsNetDocumentsProfileContextSnapshot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobs.db");

        try
        {
            var store = new JobStore(dbPath);
            await store.InitializeAsync();

            var now = DateTime.UtcNow;
            await store.ReplaceNetDocumentsAttributesAsync(
                "cab1",
                new[]
                {
                    new NetDocumentsAttributeRecord("cab1", "repo1", 1001, "1001", "Matter Type", "lookup", true, false, true, null, false, now),
                    new NetDocumentsAttributeRecord("cab1", "repo1", 1002, "1002", "Author", "text", false, false, false, null, false, now)
                });

            await store.ReplaceNetDocumentsLookupValuesAsync(
                "cab1",
                1001,
                new[]
                {
                    new NetDocumentsLookupValueRecord("cab1", 1001, null, "A", "Alpha", now),
                    new NetDocumentsLookupValueRecord("cab1", 1001, null, "B", "Beta", now)
                });

            var snapshot = await store.GetNetDocumentsProfileContextSnapshotAsync("cab1", "repo1");
            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot!.AttributeCount);
            Assert.Equal(1, snapshot.RequiredAttributeCount);
            Assert.Equal(1, snapshot.LookupAttributeCount);
            Assert.Equal(2, snapshot.LookupValueCount);
            Assert.True(snapshot.LastSyncedUtc.HasValue);
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

    [Fact]
    public async Task GetRecentJobsReturnsOnlyJobsWithStartedUploads()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobs.db");

        try
        {
            var store = new JobStore(dbPath);
            await store.InitializeAsync();

            var created = new DateTime(2026, 2, 2, 12, 30, 0, DateTimeKind.Utc);
            var startedJobId = Guid.NewGuid().ToString("N");
            var queuedOnlyJobId = Guid.NewGuid().ToString("N");
            var noTransferJobId = Guid.NewGuid().ToString("N");

            await store.InsertJobAsync(new JobRecord(startedJobId, created, "C:\\data\\started", "Complete"));
            await store.InsertJobAsync(new JobRecord(queuedOnlyJobId, created.AddMinutes(1), "C:\\data\\queued", "Complete"));
            await store.InsertJobAsync(new JobRecord(noTransferJobId, created.AddMinutes(2), "C:\\data\\none", "Complete"));

            var startedFile = new FileRecord(
                Guid.NewGuid().ToString("N"),
                startedJobId,
                "C:\\data\\started\\file-a.txt",
                "file-a.txt",
                120,
                created,
                false,
                null,
                "inherit",
                null);
            var queuedOnlyFile = new FileRecord(
                Guid.NewGuid().ToString("N"),
                queuedOnlyJobId,
                "C:\\data\\queued\\file-b.txt",
                "file-b.txt",
                256,
                created.AddMinutes(1),
                false,
                null,
                "inherit",
                null);

            await store.InsertFileAsync(startedFile);
            await store.InsertFileAsync(queuedOnlyFile);

            var startedTransferId = Guid.NewGuid().ToString("N");
            await store.UpsertTransferQueuedAsync(new TransferRecord(
                startedTransferId,
                startedJobId,
                startedFile.FileId,
                1,
                "Queued",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));
            await store.UpdateTransferRunningAsync(startedTransferId, 1, created.AddMinutes(3), 1);

            var queuedTransferId = Guid.NewGuid().ToString("N");
            await store.UpsertTransferQueuedAsync(new TransferRecord(
                queuedTransferId,
                queuedOnlyJobId,
                queuedOnlyFile.FileId,
                1,
                "Queued",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));

            var recentJobs = await store.GetRecentJobsAsync(10);

            var recentJob = Assert.Single(recentJobs);
            Assert.Equal(startedJobId, recentJob.JobId);
            Assert.Equal(1, recentJob.FileCount);
            Assert.Equal(120, recentJob.TotalBytes);
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
    public async Task MarkInFlightTransfersCanceledAsync_CancelsQueuedAndRunningTransfers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "jobs.db");

        try
        {
            var store = new JobStore(dbPath);
            await store.InitializeAsync();

            var created = new DateTime(2026, 2, 2, 12, 30, 0, DateTimeKind.Utc);
            var jobId = Guid.NewGuid().ToString("N");
            await store.InsertJobAsync(new JobRecord(jobId, created, "C:\\data\\job", "Complete"));

            var fileQueued = new FileRecord(
                Guid.NewGuid().ToString("N"),
                jobId,
                "C:\\data\\job\\queued.txt",
                "queued.txt",
                120,
                created,
                false,
                null,
                "inherit",
                null);
            var fileRunning = new FileRecord(
                Guid.NewGuid().ToString("N"),
                jobId,
                "C:\\data\\job\\running.txt",
                "running.txt",
                256,
                created,
                false,
                null,
                "inherit",
                null);
            var fileSucceeded = new FileRecord(
                Guid.NewGuid().ToString("N"),
                jobId,
                "C:\\data\\job\\succeeded.txt",
                "succeeded.txt",
                512,
                created,
                false,
                null,
                "inherit",
                null);

            await store.InsertFileAsync(fileQueued);
            await store.InsertFileAsync(fileRunning);
            await store.InsertFileAsync(fileSucceeded);

            var queuedTransferId = Guid.NewGuid().ToString("N");
            await store.UpsertTransferQueuedAsync(new TransferRecord(
                queuedTransferId,
                jobId,
                fileQueued.FileId,
                1,
                "Queued",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));

            var runningTransferId = Guid.NewGuid().ToString("N");
            await store.UpsertTransferQueuedAsync(new TransferRecord(
                runningTransferId,
                jobId,
                fileRunning.FileId,
                1,
                "Queued",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));
            await store.UpdateTransferRunningAsync(runningTransferId, 1, created.AddMinutes(2), 1);

            var succeededTransferId = Guid.NewGuid().ToString("N");
            await store.UpsertTransferQueuedAsync(new TransferRecord(
                succeededTransferId,
                jobId,
                fileSucceeded.FileId,
                1,
                "Queued",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));
            await store.UpdateTransferRunningAsync(succeededTransferId, 1, created.AddMinutes(3), 1);
            await store.UpdateTransferFinishedAsync(
                succeededTransferId,
                "Succeeded",
                finishedUtc: created.AddMinutes(3).AddMilliseconds(400),
                durationMs: 400,
                error: null,
                httpStatus: 200,
                responseSnippet: "ok",
                simulatedDelayMs: null);

            await store.MarkInFlightTransfersCanceledAsync(jobId);

            var summaries = await store.GetLatestTransfersAsync(jobId, 10);
            Assert.Equal(3, summaries.Count);

            var queuedSummary = Assert.Single(summaries, s => s.FileId == fileQueued.FileId);
            Assert.Equal("Canceled", queuedSummary.Status);

            var runningSummary = Assert.Single(summaries, s => s.FileId == fileRunning.FileId);
            Assert.Equal("Canceled", runningSummary.Status);

            var succeededSummary = Assert.Single(summaries, s => s.FileId == fileSucceeded.FileId);
            Assert.Equal("Succeeded", succeededSummary.Status);
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
