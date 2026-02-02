using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public class UnitTest1
{
    [Fact]
    public async Task ScanReportsCountsAndLargeFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var smallFile = Path.Combine(tempRoot, "small.bin");
            var largeFile = Path.Combine(tempRoot, "large.bin");
            var nestedDir = Path.Combine(tempRoot, "nested");
            Directory.CreateDirectory(nestedDir);
            var nestedFile = Path.Combine(nestedDir, "nested.bin");

            await File.WriteAllBytesAsync(smallFile, new byte[5]);
            await File.WriteAllBytesAsync(largeFile, new byte[12]);
            await File.WriteAllBytesAsync(nestedFile, new byte[20]);

            FileScanProgress? last = null;
            var largeFiles = new List<LargeFileItem>();
            var progress = new ImmediateProgress<FileScanProgress>(p =>
            {
                last = p;
                if (p.LargeFile is not null)
                {
                    largeFiles.Add(p.LargeFile);
                }
            });

            await FileScanner.ScanAsync(tempRoot, 10, progress, CancellationToken.None);

            Assert.NotNull(last);
            Assert.Equal(3, last!.TotalFiles);
            Assert.Equal(5 + 12 + 20, last.TotalBytes);
            Assert.Equal(2, largeFiles.Count);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public ImmediateProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }
}
