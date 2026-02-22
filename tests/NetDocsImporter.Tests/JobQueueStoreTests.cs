using Microsoft.Data.Sqlite;
using NetDocsImporter.Data;

namespace NetDocsImporter.Tests;

public class JobQueueStoreTests
{
    [Fact]
    public async Task QueueStateTransitions_WorkForMvpFlow()
    {
        using var fixture = new QueueStoreFixture();
        var createdUtc = DateTime.UtcNow;
        var queued = await fixture.Store.CreateUploadQueueJobAsync(
            sourceJobId: "job-a",
            sourceRoot: @"C:\source",
            snapshotJson: CreateSnapshotJson("job-a"),
            createdUtc,
            scheduledForUtc: null);

        var canceled = await fixture.Store.CancelQueuedJobAsync(queued.QueueJobId, createdUtc.AddMinutes(1));
        Assert.True(canceled);

        var runningCandidate = await fixture.Store.TryAcquireNextQueuedJobAsync(createdUtc.AddMinutes(2));
        Assert.Null(runningCandidate);

        var toRun = await fixture.Store.CreateUploadQueueJobAsync(
            sourceJobId: "job-b",
            sourceRoot: @"C:\source",
            snapshotJson: CreateSnapshotJson("job-b"),
            createdUtc.AddMinutes(3),
            scheduledForUtc: null);
        var running = await fixture.Store.TryAcquireNextQueuedJobAsync(createdUtc.AddMinutes(4));
        Assert.NotNull(running);
        Assert.Equal(toRun.QueueJobId, running!.QueueJobId);
        Assert.Equal(UploadQueueJobState.Running, running.State);

        await fixture.Store.MarkJobCompletedAsync(running.QueueJobId, createdUtc.AddMinutes(5));
        var activeRunning = await fixture.Store.GetRunningJobAsync();
        Assert.Null(activeRunning);
    }

    [Fact]
    public async Task OptionBOrdering_SelectsScheduledFirstThenCreatedAt()
    {
        using var fixture = new QueueStoreFixture();
        var baseTime = DateTime.UtcNow;

        var queuedOld = await fixture.Store.CreateUploadQueueJobAsync(
            sourceJobId: "queued-old",
            sourceRoot: @"C:\source",
            snapshotJson: CreateSnapshotJson("queued-old"),
            baseTime.AddMinutes(1),
            scheduledForUtc: null);
        var scheduledLate = await fixture.Store.CreateUploadQueueJobAsync(
            sourceJobId: "scheduled-late",
            sourceRoot: @"C:\source",
            snapshotJson: CreateSnapshotJson("scheduled-late"),
            baseTime.AddMinutes(2),
            scheduledForUtc: baseTime.AddHours(2));
        var scheduledSoon = await fixture.Store.CreateUploadQueueJobAsync(
            sourceJobId: "scheduled-soon",
            sourceRoot: @"C:\source",
            snapshotJson: CreateSnapshotJson("scheduled-soon"),
            baseTime.AddMinutes(3),
            scheduledForUtc: baseTime.AddHours(1));

        await fixture.Store.PromoteDueScheduledJobsAsync(baseTime.AddHours(3));

        var first = await fixture.Store.TryAcquireNextQueuedJobAsync(baseTime.AddHours(3).AddMinutes(1));
        Assert.NotNull(first);
        Assert.Equal(scheduledSoon.QueueJobId, first!.QueueJobId);
        await fixture.Store.MarkJobCompletedAsync(first.QueueJobId, baseTime.AddHours(3).AddMinutes(2));

        var second = await fixture.Store.TryAcquireNextQueuedJobAsync(baseTime.AddHours(3).AddMinutes(3));
        Assert.NotNull(second);
        Assert.Equal(scheduledLate.QueueJobId, second!.QueueJobId);
        await fixture.Store.MarkJobCompletedAsync(second.QueueJobId, baseTime.AddHours(3).AddMinutes(4));

        var third = await fixture.Store.TryAcquireNextQueuedJobAsync(baseTime.AddHours(3).AddMinutes(5));
        Assert.NotNull(third);
        Assert.Equal(queuedOld.QueueJobId, third!.QueueJobId);
    }

    [Fact]
    public async Task ScheduledPromotion_OnlyPromotesDueJobs()
    {
        using var fixture = new QueueStoreFixture();
        var baseTime = DateTime.UtcNow;

        await fixture.Store.CreateUploadQueueJobAsync(
            sourceJobId: "due",
            sourceRoot: @"C:\source",
            snapshotJson: CreateSnapshotJson("due"),
            baseTime,
            scheduledForUtc: baseTime.AddMinutes(-1));
        await fixture.Store.CreateUploadQueueJobAsync(
            sourceJobId: "future",
            sourceRoot: @"C:\source",
            snapshotJson: CreateSnapshotJson("future"),
            baseTime.AddMinutes(1),
            scheduledForUtc: baseTime.AddHours(1));

        var promoted = await fixture.Store.PromoteDueScheduledJobsAsync(baseTime);
        Assert.Equal(1, promoted);

        var queueView = await fixture.Store.GetQueueViewAsync(10);
        Assert.Equal(2, queueView.Count);
        Assert.Contains(queueView, q => q.SourceJobId == "due" && q.State == UploadQueueJobState.Queued);
        Assert.Contains(queueView, q => q.SourceJobId == "future" && q.State == UploadQueueJobState.Scheduled);
    }

    [Fact]
    public async Task TryAcquireNextQueuedJob_EnforcesSingleRunningJob()
    {
        using var fixture = new QueueStoreFixture();
        var now = DateTime.UtcNow;
        await fixture.Store.CreateUploadQueueJobAsync("a", @"C:\source", CreateSnapshotJson("a"), now, scheduledForUtc: null);
        await fixture.Store.CreateUploadQueueJobAsync("b", @"C:\source", CreateSnapshotJson("b"), now.AddMinutes(1), scheduledForUtc: null);

        var first = await fixture.Store.TryAcquireNextQueuedJobAsync(now.AddMinutes(2));
        Assert.NotNull(first);

        var second = await fixture.Store.TryAcquireNextQueuedJobAsync(now.AddMinutes(3));
        Assert.Null(second);
    }

    private static string CreateSnapshotJson(string sourceJobId)
    {
        return $$"""{"sourceJobId":"{{sourceJobId}}","capturedUtc":"{{DateTime.UtcNow:O}}"}""";
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
