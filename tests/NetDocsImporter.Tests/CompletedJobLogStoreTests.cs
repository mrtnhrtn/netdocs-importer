using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public sealed class CompletedJobLogStoreTests
{
    [Fact]
    public async Task GetLatestRunsByJobAsync_ReturnsLatestRunPerJob()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var store = new CompletedJobLogStore(tempRoot, TimeSpan.FromDays(30));
            var t1 = new DateTime(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 2, 11, 9, 0, 0, DateTimeKind.Utc);

            await store.WriteRunLogAsync("job-a", t1, "run-a-1");
            await store.WriteSummaryAsync(new CompletedJobRunSummary
            {
                JobId = "job-a",
                StartedUtc = t1,
                Status = "DirectUpload",
                Summary = "first"
            });

            await store.WriteRunLogAsync("job-a", t2, "run-a-2");
            await store.WriteSummaryAsync(new CompletedJobRunSummary
            {
                JobId = "job-a",
                StartedUtc = t2,
                Status = "DirectUpload Partial",
                Summary = "second"
            });

            await store.WriteRunLogAsync("job-b", t1, "run-b-1");
            await store.WriteSummaryAsync(new CompletedJobRunSummary
            {
                JobId = "job-b",
                StartedUtc = t1,
                Status = "DirectUpload",
                Summary = "b-first"
            });

            var latestByJob = await store.GetLatestRunsByJobAsync();
            Assert.Equal(2, latestByJob.Count);
            Assert.True(latestByJob.ContainsKey("job-a"));
            Assert.True(latestByJob.ContainsKey("job-b"));
            Assert.Equal(t2, latestByJob["job-a"].StartedUtc);
            Assert.Equal("DirectUpload Partial", latestByJob["job-a"].Status);
            Assert.Equal("second", latestByJob["job-a"].Summary);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void PruneExpired_RemovesFilesOlderThanRetention()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var nowUtc = new DateTime(2026, 2, 12, 0, 0, 0, DateTimeKind.Utc);
            var store = new CompletedJobLogStore(tempRoot, TimeSpan.FromDays(30));

            var oldFile = Path.Combine(tempRoot, "old-runlog.txt");
            var newFile = Path.Combine(tempRoot, "new-runlog.txt");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(newFile, "new");
            File.SetLastWriteTimeUtc(oldFile, nowUtc.AddDays(-40));
            File.SetLastWriteTimeUtc(newFile, nowUtc.AddDays(-5));

            store.PruneExpired(nowUtc);

            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(newFile));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-completed-jobs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
