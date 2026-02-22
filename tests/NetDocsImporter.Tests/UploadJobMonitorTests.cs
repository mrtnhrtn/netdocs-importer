using Microsoft.Data.Sqlite;
using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.Tests;

public class UploadJobMonitorTests
{
    [Fact]
    public async Task TickOnceAsync_RunsNextQueuedJob_AndMarksCompleted()
    {
        using var fixture = new QueueStoreFixture();
        var now = DateTime.UtcNow;
        await fixture.Store.CreateUploadQueueJobAsync("job-a", @"C:\source", CreateSnapshotJson("job-a"), now, scheduledForUtc: null);

        var runner = new RecordingRunner(_ => new UploadRunnerResult(true));
        var monitor = new UploadJobMonitor(fixture.Store, runner, new FakeClock(now), TimeSpan.FromMinutes(1));

        await monitor.TickOnceAsync();

        Assert.Single(runner.StartedJobs);
        Assert.Equal("job-a", runner.StartedJobs[0].SourceJobId);
        Assert.Null(await fixture.Store.GetRunningJobAsync());
    }

    [Fact]
    public async Task StartAsync_PromotesDueAndFailsStaleRunning_ForRestartBehavior()
    {
        using var fixture = new QueueStoreFixture();
        var baseTime = DateTime.UtcNow;
        await fixture.Store.CreateUploadQueueJobAsync(
            "due-job",
            @"C:\source",
            CreateSnapshotJson("due-job"),
            baseTime,
            scheduledForUtc: baseTime.AddMinutes(-5));
        await fixture.Store.CreateUploadQueueJobAsync(
            "stale-running",
            @"C:\source",
            CreateSnapshotJson("stale-running"),
            baseTime.AddMinutes(1),
            scheduledForUtc: null);
        _ = await fixture.Store.TryAcquireNextQueuedJobAsync(baseTime.AddMinutes(2));

        var runner = new RecordingRunner(_ => new UploadRunnerResult(true));
        var monitor = new UploadJobMonitor(fixture.Store, runner, new BlockingClock(baseTime), TimeSpan.FromMinutes(1));

        await monitor.StartAsync();
        Assert.True(monitor.StartupPromotedDueCount >= 1);
        await monitor.TickOnceAsync();
        await monitor.DisposeAsync();

        Assert.Contains(runner.StartedJobs, j => j.SourceJobId == "due-job");
        Assert.DoesNotContain(runner.StartedJobs, j => j.SourceJobId == "stale-running");
    }

    [Fact]
    public async Task TickOnceAsync_WhenMarkCompletedThrows_FailsJobForRecovery()
    {
        using var fixture = new QueueStoreFixture();
        var now = DateTime.UtcNow;
        var queued = await fixture.Store.CreateUploadQueueJobAsync(
            "job-a",
            @"C:\source",
            CreateSnapshotJson("job-a"),
            now,
            scheduledForUtc: null);

        var flakyStore = new FlakyQueueStore(fixture.Store)
        {
            ThrowOnFirstMarkCompleted = true
        };
        var monitor = new UploadJobMonitor(flakyStore, new RecordingRunner(_ => new UploadRunnerResult(true)), new FakeClock(now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.TickOnceAsync());

        Assert.Null(await fixture.Store.GetRunningJobAsync());
        Assert.Equal("Failed", await GetQueueJobStateAsync(fixture.DbPath, queued.QueueJobId));
    }

    [Fact]
    public async Task TickOnceAsync_RecoversAndContinuesAfterTransientStoreException()
    {
        using var fixture = new QueueStoreFixture();
        var now = DateTime.UtcNow;
        await fixture.Store.CreateUploadQueueJobAsync("job-a", @"C:\source", CreateSnapshotJson("job-a"), now, scheduledForUtc: null);

        var flakyStore = new FlakyQueueStore(fixture.Store)
        {
            ThrowOnFirstAcquire = true
        };
        var runner = new RecordingRunner(_ => new UploadRunnerResult(true));
        var monitor = new UploadJobMonitor(flakyStore, runner, new FakeClock(now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.TickOnceAsync());
        await monitor.TickOnceAsync();

        Assert.Single(runner.StartedJobs);
        Assert.Null(await fixture.Store.GetRunningJobAsync());
    }

    private static string CreateSnapshotJson(string sourceJobId)
    {
        return $$"""{"sourceJobId":"{{sourceJobId}}","capturedUtc":"{{DateTime.UtcNow:O}}"}""";
    }

    private sealed class RecordingRunner : IUploadRunner
    {
        private readonly Func<UploadQueueJobRecord, UploadRunnerResult> _handler;

        public RecordingRunner(Func<UploadQueueJobRecord, UploadRunnerResult> handler)
        {
            _handler = handler;
        }

        public List<UploadQueueJobRecord> StartedJobs { get; } = new();

        public Task<UploadRunnerResult> RunAsync(UploadQueueJobRecord job, CancellationToken cancellationToken = default)
        {
            StartedJobs.Add(job);
            return Task.FromResult(_handler(job));
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingClock : IClock
    {
        public BlockingClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class QueueStoreFixture : IDisposable
    {
        private readonly string _tempRoot;

        public QueueStoreFixture()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            DbPath = Path.Combine(_tempRoot, "jobs.db");
            Store = new JobStore(DbPath);
            Store.InitializeAsync().GetAwaiter().GetResult();
        }

        public string DbPath { get; }

        public JobStore Store { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
    }

    private sealed class FlakyQueueStore : IUploadQueueStore
    {
        private readonly IUploadQueueStore _inner;

        public FlakyQueueStore(IUploadQueueStore inner)
        {
            _inner = inner;
        }

        public bool ThrowOnFirstAcquire { get; set; }

        public bool ThrowOnFirstMarkCompleted { get; set; }

        public async Task<int> PromoteDueScheduledJobsAsync(DateTime utcNow, CancellationToken cancellationToken = default)
        {
            return await _inner.PromoteDueScheduledJobsAsync(utcNow, cancellationToken);
        }

        public async Task<int> FailRunningJobsAsync(DateTime utcNow, string reason, CancellationToken cancellationToken = default)
        {
            return await _inner.FailRunningJobsAsync(utcNow, reason, cancellationToken);
        }

        public async Task<UploadQueueJobRecord?> TryAcquireNextQueuedJobAsync(DateTime utcNow, CancellationToken cancellationToken = default)
        {
            if (ThrowOnFirstAcquire)
            {
                ThrowOnFirstAcquire = false;
                throw new InvalidOperationException("simulated acquire failure");
            }

            return await _inner.TryAcquireNextQueuedJobAsync(utcNow, cancellationToken);
        }

        public async Task MarkJobCompletedAsync(string queueJobId, DateTime utcNow, CancellationToken cancellationToken = default)
        {
            if (ThrowOnFirstMarkCompleted)
            {
                ThrowOnFirstMarkCompleted = false;
                throw new InvalidOperationException("simulated mark completion failure");
            }

            await _inner.MarkJobCompletedAsync(queueJobId, utcNow, cancellationToken);
        }

        public async Task MarkJobFailedAsync(string queueJobId, DateTime utcNow, string error, CancellationToken cancellationToken = default)
        {
            await _inner.MarkJobFailedAsync(queueJobId, utcNow, error, cancellationToken);
        }

        public async Task<UploadQueueJobRecord?> GetRunningJobAsync(CancellationToken cancellationToken = default)
        {
            return await _inner.GetRunningJobAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<UploadQueueJobRecord>> GetQueueViewAsync(int take, CancellationToken cancellationToken = default)
        {
            return await _inner.GetQueueViewAsync(take, cancellationToken);
        }
    }

    private static async Task<string?> GetQueueJobStateAsync(string dbPath, string queueJobId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT State
            FROM UploadQueueJobs
            WHERE QueueJobId = $queueJobId;
            """;
        command.Parameters.AddWithValue("$queueJobId", queueJobId);
        var state = await command.ExecuteScalarAsync();
        return state as string;
    }
}
