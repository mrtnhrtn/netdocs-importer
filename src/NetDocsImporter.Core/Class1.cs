namespace NetDocsImporter.Core;

public sealed record LargeFileItem(string Path, long Bytes);

public sealed record FileScanProgress(long TotalFiles, long TotalBytes, LargeFileItem? LargeFile);

public static class FileScanner
{
    public static Task ScanAsync(
        string rootPath,
        long largeFileThresholdBytes,
        IProgress<FileScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path is required.", nameof(rootPath));
        }

        return Task.Run(
            () => ScanInternal(rootPath, largeFileThresholdBytes, progress, cancellationToken),
            cancellationToken);
    }

    private static void ScanInternal(
        string rootPath,
        long largeFileThresholdBytes,
        IProgress<FileScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalFiles = 0L;
        var totalBytes = 0L;

        var dirs = new Stack<string>();
        dirs.Push(rootPath);

        var lastReport = DateTime.UtcNow;
        const int ReportEveryFiles = 250;

        while (dirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = dirs.Pop();

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(current))
                {
                    dirs.Push(dir);
                }
            }
            catch
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
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
                catch
                {
                    continue;
                }

                totalFiles++;
                totalBytes += info.Length;

                LargeFileItem? largeFile = null;
                if (info.Length >= largeFileThresholdBytes)
                {
                    largeFile = new LargeFileItem(info.FullName, info.Length);
                }

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
    }
}
