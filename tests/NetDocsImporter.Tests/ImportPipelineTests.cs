using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.Tests;

public class ImportPipelineTests
{
    [Fact]
    public async Task EnforcesGlobalPacingBetweenStarts()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var files = await SeedFilesAsync(store, jobId, 3);

        var clock = new FakeClock(DateTime.UtcNow);
        var uploader = new FixedUploader();
        var pipeline = new ImportPipeline(store, uploader, clock, new NullPipelineLogger());
        var starts = new List<DateTime>();

        var progress = new Progress<TransferUpdate>(update =>
        {
            if (update.Status == "Running" && update.StartedUtc.HasValue)
            {
                starts.Add(update.StartedUtc.Value);
            }
        });

        await pipeline.RunAsync(jobId, maxConcurrency: 3, msDelayBetweenStarts: 200, progress, CancellationToken.None);

        Assert.Equal(files.Count, starts.Count);
        starts.Sort();
        for (var i = 1; i < starts.Count; i++)
        {
            var delta = starts[i] - starts[i - 1];
            Assert.True(delta >= TimeSpan.FromMilliseconds(200));
        }

        CleanupTempRoot(tempRoot);
    }

    [Fact]
    public async Task RetriesFailedTransfersUpToTwoTimes()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var files = await SeedFilesAsync(store, jobId, 1);

        var clock = new FakeClock(DateTime.UtcNow);
        var uploader = new FlakyUploader(failuresBeforeSuccess: 2);
        var pipeline = new ImportPipeline(store, uploader, clock, new NullPipelineLogger());

        var attempts = 0;
        var progress = new Progress<TransferUpdate>(update =>
        {
            if (update.Status == "Running")
            {
                attempts++;
            }
        });

        await pipeline.RunAsync(jobId, maxConcurrency: 1, msDelayBetweenStarts: 0, progress, CancellationToken.None);

        Assert.Equal(3, attempts);

        var transfers = await store.GetLatestTransfersAsync(jobId, 1);
        Assert.Single(transfers);
        Assert.Equal("Succeeded", transfers[0].Status);
        Assert.Equal(3, transfers[0].Attempt);

        CleanupTempRoot(tempRoot);
    }

    [Fact]
    public async Task DoesNotReRunSucceededFiles()
    {
        var tempRoot = CreateTempRoot();
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var files = await SeedFilesAsync(store, jobId, 1);
        var file = files[0];

        var transferId = Guid.NewGuid().ToString("N");
        await store.UpsertTransferQueuedAsync(new TransferRecord(
            transferId,
            jobId,
            file.FileId,
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

        await store.UpdateTransferFinishedAsync(
            transferId,
            "Succeeded",
            DateTime.UtcNow,
            12,
            null,
            200,
            "ok",
            10);

        var clock = new FakeClock(DateTime.UtcNow);
        var uploader = new FixedUploader();
        var pipeline = new ImportPipeline(store, uploader, clock, new NullPipelineLogger());

        await pipeline.RunAsync(jobId, maxConcurrency: 1, msDelayBetweenStarts: 0, null, CancellationToken.None);

        Assert.Equal(0, uploader.UploadCount);

        CleanupTempRoot(tempRoot);
    }

    private static async Task<List<FileRecord>> SeedFilesAsync(JobStore store, string jobId, int count)
    {
        var results = new List<FileRecord>();
        for (var i = 0; i < count; i++)
        {
            var file = new FileRecord(
                Guid.NewGuid().ToString("N"),
                jobId,
                $"C:\\data\\file-{i}.txt",
                $"file-{i}.txt",
                100 + i,
                DateTime.UtcNow,
                false,
                null);

            await store.InsertFileAsync(file);
            results.Add(file);
        }

        return results;
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

    private sealed class FakeClock : IClock
    {
        private DateTime _utcNow;
        private readonly object _lock = new();

        public FakeClock(DateTime startUtc)
        {
            _utcNow = startUtc;
        }

        public DateTime UtcNow
        {
            get
            {
                lock (_lock)
                {
                    return _utcNow;
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            lock (_lock)
            {
                _utcNow = _utcNow.Add(delay);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FixedUploader : ISinkUploader
    {
        public int UploadCount { get; private set; }

        public Task<UploadResult> UploadAsync(FileRecord file, CancellationToken cancellationToken)
        {
            UploadCount++;
            return Task.FromResult(new UploadResult(true, 200, "ok", null, 0));
        }
    }

    private sealed class FlakyUploader : ISinkUploader
    {
        private readonly int _failuresBeforeSuccess;
        private int _attempts;

        public FlakyUploader(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public Task<UploadResult> UploadAsync(FileRecord file, CancellationToken cancellationToken)
        {
            _attempts++;
            if (_attempts <= _failuresBeforeSuccess)
            {
                return Task.FromResult(new UploadResult(false, 500, "fail", "fail", 0));
            }

            return Task.FromResult(new UploadResult(true, 200, "ok", null, 0));
        }
    }
}
