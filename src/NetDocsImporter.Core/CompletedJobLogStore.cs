using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetDocsImporter.Core;

public sealed record CompletedJobRunSummary
{
    public string JobId { get; init; } = string.Empty;

    public DateTime StartedUtc { get; init; }

    public string RunType { get; init; } = "DirectUpload";

    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public long RequestedFiles { get; init; }

    public long PlannedFiles { get; init; }

    public long UploadedFiles { get; init; }

    public long FailedFiles { get; init; }

    public long SkippedFiles { get; init; }

    public long ResumedFiles { get; init; }

    public long CreatedFolders { get; init; }

    public string ReportFileName { get; init; } = string.Empty;

    public string RunLogFileName { get; init; } = string.Empty;
}

public sealed record DirectUploadActiveRunMarker
{
    public string JobId { get; init; } = string.Empty;

    public DateTime StartedUtc { get; init; }

    public string RunType { get; init; } = "DirectUpload";

    public string TargetDisplay { get; init; } = string.Empty;

    public int TotalRequestedFiles { get; init; }

    public int PlannedFiles { get; init; }

    public int SkippedFiles { get; init; }

    public int PlannedFolderCreates { get; init; }
}

public sealed record ActiveRunMarkerEntry(string MarkerPath, DirectUploadActiveRunMarker Marker);

public sealed class CompletedJobLogStore
{
    private const string DisablePruneEnvironmentVariable = "ND_DISABLE_COMPLETED_JOBS_PRUNE";
    private static readonly Regex UnsafeFileCharsRegex = new($"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly TimeSpan _retention;

    public CompletedJobLogStore(string directory, TimeSpan? retention = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Completed jobs directory is required.", nameof(directory));
        }

        _directory = directory;
        _retention = retention ?? TimeSpan.FromDays(30);
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public string BuildRunLogPath(string jobId, DateTime startedUtc)
    {
        var baseName = BuildBaseName(jobId, startedUtc);
        return Path.Combine(_directory, $"{baseName}-runlog.txt");
    }

    public string BuildSummaryPath(string jobId, DateTime startedUtc)
    {
        var baseName = BuildBaseName(jobId, startedUtc);
        return Path.Combine(_directory, $"{baseName}.json");
    }

    public string BuildActiveRunPath(string jobId, DateTime startedUtc)
    {
        var baseName = BuildBaseName(jobId, startedUtc);
        return Path.Combine(_directory, $"{baseName}.active");
    }

    public async Task<string> WriteRunLogAsync(
        string jobId,
        DateTime startedUtc,
        string content,
        CancellationToken cancellationToken = default)
    {
        var logPath = BuildRunLogPath(jobId, startedUtc);
        await File.WriteAllTextAsync(logPath, content, new UTF8Encoding(false), cancellationToken);
        return logPath;
    }

    public async Task<string> WriteSummaryAsync(
        CompletedJobRunSummary summary,
        CancellationToken cancellationToken = default)
    {
        if (summary is null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        var summaryPath = BuildSummaryPath(summary.JobId, summary.StartedUtc);
        var json = JsonSerializer.Serialize(summary, JsonOptions);
        await File.WriteAllTextAsync(summaryPath, json, new UTF8Encoding(false), cancellationToken);
        return summaryPath;
    }

    public async Task<string> WriteActiveRunAsync(
        DirectUploadActiveRunMarker marker,
        CancellationToken cancellationToken = default)
    {
        if (marker is null)
        {
            throw new ArgumentNullException(nameof(marker));
        }

        var markerPath = BuildActiveRunPath(marker.JobId, marker.StartedUtc);
        var json = JsonSerializer.Serialize(marker, JsonOptions);
        await File.WriteAllTextAsync(markerPath, json, new UTF8Encoding(false), cancellationToken);
        return markerPath;
    }

    public async Task<IReadOnlyList<ActiveRunMarkerEntry>> GetActiveRunsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ActiveRunMarkerEntry>();
        if (!Directory.Exists(_directory))
        {
            return results;
        }

        var markerFiles = Directory
            .EnumerateFiles(_directory, "directupload-*.active", SearchOption.TopDirectoryOnly)
            .OrderBy(File.GetLastWriteTimeUtc);

        foreach (var markerFile in markerFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DirectUploadActiveRunMarker? marker;
            try
            {
                var json = await File.ReadAllTextAsync(markerFile, cancellationToken);
                marker = JsonSerializer.Deserialize<DirectUploadActiveRunMarker>(json, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (marker is null || string.IsNullOrWhiteSpace(marker.JobId))
            {
                continue;
            }

            results.Add(new ActiveRunMarkerEntry(markerFile, marker));
        }

        return results;
    }

    public Task DeleteActiveRunAsync(string markerPath)
    {
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<string, CompletedJobRunSummary>> GetLatestRunsByJobAsync(
        int maxJobs = 50,
        CancellationToken cancellationToken = default)
    {
        if (maxJobs <= 0)
        {
            return new Dictionary<string, CompletedJobRunSummary>(StringComparer.OrdinalIgnoreCase);
        }

        PruneExpired();

        var results = new Dictionary<string, CompletedJobRunSummary>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_directory))
        {
            return results;
        }

        var summaryFiles = Directory
            .EnumerateFiles(_directory, "directupload-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (var file in summaryFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CompletedJobRunSummary? summary;
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                summary = JsonSerializer.Deserialize<CompletedJobRunSummary>(json, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (summary is null || string.IsNullOrWhiteSpace(summary.JobId))
            {
                continue;
            }

            if (results.TryGetValue(summary.JobId, out var existing) && existing.StartedUtc >= summary.StartedUtc)
            {
                continue;
            }

            results[summary.JobId] = summary;
            if (results.Count >= maxJobs)
            {
                break;
            }
        }

        return results;
    }

    public void PruneExpired(DateTime? utcNow = null)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(DisablePruneEnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Directory.Exists(_directory))
        {
            return;
        }

        var cutoff = (utcNow ?? DateTime.UtcNow).Subtract(_retention);
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                if (lastWriteUtc <= cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort retention cleanup.
            }
        }
    }

    private static string BuildBaseName(string jobId, DateTime startedUtc)
    {
        var safeJobId = UnsafeFileCharsRegex.Replace(string.IsNullOrWhiteSpace(jobId) ? "unknown" : jobId.Trim(), "-");
        if (string.IsNullOrWhiteSpace(safeJobId))
        {
            safeJobId = "unknown";
        }

        return $"directupload-{safeJobId}-{startedUtc.ToLocalTime():yyyyMMdd_HHmmss}";
    }
}
