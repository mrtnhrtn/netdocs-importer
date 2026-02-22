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
            Store = new JobStore(Path.Combine(_tempRoot, "jobs.db"));
            Store.InitializeAsync().GetAwaiter().GetResult();
        }

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
}
