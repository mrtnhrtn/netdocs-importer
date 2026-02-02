using System.Diagnostics;
using NetDocsImporter.Data;

namespace NetDocsImporter.Core;

public sealed class ScanJobRunner
{
    private readonly JobStore _jobStore;

    public ScanJobRunner(JobStore jobStore)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    }

    public async Task RunAsync(
        string sourceRoot,
        long largeFileThresholdBytes,
        IProgress<FileScanProgress>? progress,
        CancellationToken cancellationToken,
        string? jobId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            throw new ArgumentException("Source root is required.", nameof(sourceRoot));
        }

        var resolvedJobId = string.IsNullOrWhiteSpace(jobId) ? Guid.NewGuid().ToString("N") : jobId;

        await _jobStore.InitializeAsync(cancellationToken);
        await _jobStore.InsertJobAsync(new JobRecord(
            resolvedJobId,
            DateTime.UtcNow,
            sourceRoot,
            "Scanning"), cancellationToken);

        JobFileWriter? fileWriter = null;
        JobFolderWriter? folderWriter = null;

        try
        {
            fileWriter = _jobStore.OpenFileWriter();
            folderWriter = _jobStore.OpenFolderWriter();
            await ScanWithFoldersAsync(
                resolvedJobId,
                sourceRoot,
                largeFileThresholdBytes,
                progress,
                fileWriter,
                folderWriter,
                cancellationToken);

            await _jobStore.UpdateJobStatusAsync(resolvedJobId, "Complete", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await _jobStore.UpdateJobStatusAsync(resolvedJobId, "Canceled", cancellationToken);
            throw;
        }
        catch
        {
            await _jobStore.UpdateJobStatusAsync(resolvedJobId, "Failed", cancellationToken);
            throw;
        }
        finally
        {
            fileWriter?.Dispose();
            folderWriter?.Dispose();
        }
    }

    private static async Task ScanWithFoldersAsync(
        string jobId,
        string rootPath,
        long largeFileThresholdBytes,
        IProgress<FileScanProgress>? progress,
        JobFileWriter fileWriter,
        JobFolderWriter folderWriter,
        CancellationToken cancellationToken)
    {
        var totalFiles = 0L;
        var totalBytes = 0L;

        var stack = new Stack<FolderWorkItem>();
        stack.Push(new FolderWorkItem(rootPath, null, 0));

        var lastReport = DateTime.UtcNow;
        const int ReportEveryFiles = 250;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = stack.Pop();

            var folderId = Guid.NewGuid().ToString("N");
            var relativePath = NormalizeRelativePath(rootPath, current.Path);
            var folderRecord = new FolderRecord(
                folderId,
                jobId,
                current.Path,
                relativePath,
                current.ParentFolderId,
                current.Depth,
                true,
                false,
                DateTime.UtcNow,
                "inherit",
                "inherit");
            folderWriter.Insert(folderRecord);

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(current.Path))
                {
                    stack.Push(new FolderWorkItem(dir, folderId, current.Depth + 1));
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to enumerate directories under {current.Path}: {ex.Message}");
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current.Path);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to enumerate files under {current.Path}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to access file {file}: {ex.Message}");
                    continue;
                }

                totalFiles++;
                totalBytes += info.Length;

                LargeFileItem? largeFile = null;
                var isLargeWarning = info.Length > largeFileThresholdBytes;
                if (isLargeWarning)
                {
                    largeFile = new LargeFileItem(info.FullName, info.Length);
                }

                var fileRecord = new FileRecord(
                    Guid.NewGuid().ToString("N"),
                    jobId,
                    info.FullName,
                    NormalizeRelativePath(rootPath, info.FullName),
                    info.Length,
                    info.LastWriteTimeUtc,
                    isLargeWarning,
                    folderId);
                fileWriter.Insert(fileRecord);

                if (progress is not null && (totalFiles % ReportEveryFiles == 0 || largeFile is not null))
                {
                    progress.Report(new FileScanProgress(totalFiles, totalBytes, largeFile));
                    lastReport = DateTime.UtcNow;
                }
                else if (progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(500))
                {
                    progress.Report(new FileScanProgress(totalFiles, totalBytes, null));
                    lastReport = DateTime.UtcNow;
                }
            }
        }

        progress?.Report(new FileScanProgress(totalFiles, totalBytes, null));
        await Task.CompletedTask;
    }

    private static string NormalizeRelativePath(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        if (relative == ".")
        {
            return string.Empty;
        }

        var normalized = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToLowerInvariant();
        }

        return normalized;
    }

    private sealed record FolderWorkItem(string Path, string? ParentFolderId, int Depth);
}
