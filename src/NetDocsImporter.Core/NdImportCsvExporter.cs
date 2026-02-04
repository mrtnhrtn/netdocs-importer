using System.Globalization;
using System.Text;
using NetDocsImporter.Data;

namespace NetDocsImporter.Core;

public enum NdImportExportScope
{
    IncludedOnly
}

public enum NdImportMappingMode
{
    Mirror,
    Flatten
}

public sealed class NdImportExportOptions
{
    public NdImportExportScope Scope { get; set; } = NdImportExportScope.IncludedOnly;

    public NdImportMappingMode MappingMode { get; set; } = NdImportMappingMode.Mirror;

    public string CabinetName { get; set; } = string.Empty;

    public string AnchorFolderPath { get; set; } = string.Empty;

    public bool IncludeAuditStamps { get; set; } = true;

    public string ImportedBy { get; set; } = "Imported Content";
}

public sealed class NdImportExportResult
{
    public NdImportExportResult(
        string outputPath,
        string warningsPath,
        int totalFiles,
        int largeFileWarnings,
        int emptyFolderWarnings,
        int accessDeniedWarnings)
    {
        OutputPath = outputPath;
        WarningsPath = warningsPath;
        TotalFiles = totalFiles;
        LargeFileWarnings = largeFileWarnings;
        EmptyFolderWarnings = emptyFolderWarnings;
        AccessDeniedWarnings = accessDeniedWarnings;
    }

    public string OutputPath { get; }

    public string WarningsPath { get; }

    public int TotalFiles { get; }

    public int LargeFileWarnings { get; }

    public int EmptyFolderWarnings { get; }

    public int AccessDeniedWarnings { get; }
}

public static class NdImportCsv
{
    public static string Escape(string value)
    {
        if (value.Contains('"'))
        {
            value = value.Replace("\"", "\"\"");
        }

        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
        {
            return $"\"{value}\"";
        }

        return value;
    }
}

public sealed class NdImportCsvExporter
{
    private const long LargeFileThresholdBytes = 1_800_000_000;
    private readonly JobStore _jobStore;

    public NdImportCsvExporter(JobStore jobStore)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    }

    public async Task<NdImportExportResult> ExportAsync(
        string jobId,
        string reportsDirectory,
        NdImportExportOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        if (string.IsNullOrWhiteSpace(reportsDirectory))
        {
            throw new ArgumentException("Reports directory is required.", nameof(reportsDirectory));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        await _jobStore.InitializeAsync(cancellationToken);
        var files = await _jobStore.GetIncludedFilesForJobAsync(jobId, cancellationToken);

        Directory.CreateDirectory(reportsDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var outputPath = Path.Combine(reportsDirectory, $"ndimport-{jobId}-{timestamp}.csv");
        var warningsPath = Path.Combine(reportsDirectory, $"ndimport-{jobId}-{timestamp}-warnings.csv");

        var largeFileWarnings = 0;
        var accessDeniedWarnings = 0;

        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        var header = new List<string>
        {
            "FULL PATH",
            "DOCUMENT NAME",
            "DOCUMENT EXTENSION",
            "FOLDER"
        };

        if (options.IncludeAuditStamps)
        {
            header.Add("CREATED BY");
            header.Add("CREATED DATE");
            header.Add("LAST MODIFIED BY");
            header.Add("LAST MODIFIED DATE");
        }

        await writer.WriteLineAsync(string.Join(",", header));

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.IsLargeWarning)
            {
                largeFileWarnings++;
            }
            else if (file.SizeBytes > LargeFileThresholdBytes)
            {
                largeFileWarnings++;
            }

            var documentName = Path.GetFileNameWithoutExtension(file.FullPath);
            var documentExtension = Path.GetExtension(file.FullPath).TrimStart('.');
            var folder = ResolveFolderPath(file.RelativePath, options.MappingMode, options.AnchorFolderPath);

            var row = new List<string>
            {
                NdImportCsv.Escape(file.FullPath),
                NdImportCsv.Escape(documentName),
                NdImportCsv.Escape(documentExtension),
                NdImportCsv.Escape(folder)
            };

            if (options.IncludeAuditStamps)
            {
                var importedBy = string.IsNullOrWhiteSpace(options.ImportedBy) ? "Imported Content" : options.ImportedBy;
                row.Add(NdImportCsv.Escape(importedBy));

                if (TryGetFileCreationUtc(file.FullPath, out var createdUtc))
                {
                    row.Add(NdImportCsv.Escape(FormatLocalDate(createdUtc)));
                }
                else
                {
                    accessDeniedWarnings++;
                    row.Add(string.Empty);
                }

                row.Add(NdImportCsv.Escape(importedBy));
                row.Add(NdImportCsv.Escape(FormatLocalDate(file.ModifiedUtc)));
            }

            await writer.WriteLineAsync(string.Join(",", row));
        }

        var emptyFolderWarnings = await WriteWarningsReportAsync(jobId, warningsPath, files, cancellationToken);
        return new NdImportExportResult(outputPath, warningsPath, files.Count, largeFileWarnings, emptyFolderWarnings, accessDeniedWarnings);
    }

    private async Task<int> WriteWarningsReportAsync(
        string jobId,
        string warningsPath,
        IReadOnlyList<FileRecord> includedFiles,
        CancellationToken cancellationToken)
    {
        var folders = await _jobStore.GetFoldersForJobAsync(jobId, cancellationToken);
        var counts = await _jobStore.GetFolderImportCountsForJobAsync(jobId, cancellationToken);
        var countsByFolder = counts.ToDictionary(c => c.FolderId, c => c, StringComparer.OrdinalIgnoreCase);

        var emptyFolderWarnings = 0;

        await using var stream = new FileStream(warningsPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        await writer.WriteLineAsync("TYPE,RELATIVE PATH,FULL PATH,SIZE BYTES");

        foreach (var file in includedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.SizeBytes <= LargeFileThresholdBytes && !file.IsLargeWarning)
            {
                continue;
            }

            var row = string.Join(",", new[]
            {
                "LARGE_FILE",
                NdImportCsv.Escape(file.RelativePath),
                NdImportCsv.Escape(file.FullPath),
                file.SizeBytes.ToString(CultureInfo.InvariantCulture)
            });
            await writer.WriteLineAsync(row);
        }

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!countsByFolder.TryGetValue(folder.FolderId, out var count))
            {
                continue;
            }

            if (!count.EffectiveIncluded || count.IncludedDescendantFileCount > 0)
            {
                continue;
            }

            emptyFolderWarnings++;
            var row = string.Join(",", new[]
            {
                "EMPTY_FOLDER",
                NdImportCsv.Escape(folder.RelativePath),
                NdImportCsv.Escape(folder.FullPath),
                string.Empty
            });
            await writer.WriteLineAsync(row);
        }

        return emptyFolderWarnings;
    }

    private static string ResolveFolderPath(string relativePath, NdImportMappingMode mappingMode, string anchorFolderPath)
    {
        var anchor = (anchorFolderPath ?? string.Empty).Trim();
        if (mappingMode == NdImportMappingMode.Flatten)
        {
            return anchor;
        }

        var relativeFolder = Path.GetDirectoryName(relativePath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return relativeFolder;
        }

        if (string.IsNullOrWhiteSpace(relativeFolder))
        {
            return anchor;
        }

        return Path.Combine(anchor, relativeFolder);
    }

    private static bool TryGetFileCreationUtc(string fullPath, out DateTime createdUtc)
    {
        createdUtc = DateTime.MinValue;

        try
        {
            createdUtc = File.GetCreationTimeUtc(fullPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatLocalDate(DateTime utcValue)
    {
        return utcValue.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
