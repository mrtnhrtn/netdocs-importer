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

    public bool IncludeProfileMetadata { get; set; }

    public ProfileSchemaDictionary? ProfileSchema { get; set; }

    public bool ValidateLookupKeys { get; set; } = true;
}

public sealed class NdImportExportResult
{
    public NdImportExportResult(
        string outputPath,
        string warningsPath,
        int totalFiles,
        int largeFileWarnings,
        int emptyFolderWarnings,
        int unresolvedFieldWarnings,
        int unresolvedValueWarnings,
        int fileIncludeOverrides,
        int fileExcludeOverrides,
        int accessDeniedWarnings)
    {
        OutputPath = outputPath;
        WarningsPath = warningsPath;
        TotalFiles = totalFiles;
        LargeFileWarnings = largeFileWarnings;
        EmptyFolderWarnings = emptyFolderWarnings;
        UnresolvedFieldWarnings = unresolvedFieldWarnings;
        UnresolvedValueWarnings = unresolvedValueWarnings;
        FileIncludeOverrides = fileIncludeOverrides;
        FileExcludeOverrides = fileExcludeOverrides;
        AccessDeniedWarnings = accessDeniedWarnings;
    }

    public string OutputPath { get; }

    public string WarningsPath { get; }

    public int TotalFiles { get; }

    public int LargeFileWarnings { get; }

    public int EmptyFolderWarnings { get; }

    public int UnresolvedFieldWarnings { get; }

    public int UnresolvedValueWarnings { get; }

    public int FileIncludeOverrides { get; }

    public int FileExcludeOverrides { get; }

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
        var allFiles = await _jobStore.GetFilesForJobAsync(jobId, cancellationToken);
        var folders = await _jobStore.GetFoldersForJobAsync(jobId, cancellationToken);
        var folderProfiles = await _jobStore.GetFolderProfilesForJobAsync(jobId, cancellationToken);

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

        var profileColumns = options.IncludeProfileMetadata
            ? BuildProfileColumns(folders, folderProfiles, options.ProfileSchema)
            : new List<string>();

        header.AddRange(profileColumns);

        if (options.IncludeAuditStamps)
        {
            header.Add("CREATED BY");
            header.Add("CREATED DATE");
            header.Add("LAST MODIFIED BY");
            header.Add("LAST MODIFIED DATE");
        }

        await writer.WriteLineAsync(string.Join(",", header));

        var effectiveProfiles = options.IncludeProfileMetadata
            ? BuildEffectiveProfiles(folders, folderProfiles)
            : new Dictionary<string, IReadOnlyList<ProfileFieldEntry>>(StringComparer.OrdinalIgnoreCase);

        var warnings = new List<ExportWarning>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            if (options.IncludeProfileMetadata)
            {
                if (!effectiveProfiles.TryGetValue(file.FolderId ?? string.Empty, out var profileEntries))
                {
                    profileEntries = Array.Empty<ProfileFieldEntry>();
                }

                var profileValues = BuildProfileValues(profileColumns, profileEntries, options, file, warnings);
                row.AddRange(profileValues);
            }

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

        largeFileWarnings = allFiles.Count(f => f.IsLargeWarning || f.SizeBytes > LargeFileThresholdBytes);
        var emptyFolderWarnings = await WriteWarningsReportAsync(jobId, warningsPath, allFiles, folders, warnings, cancellationToken);
        var unresolvedFieldWarnings = warnings.Count(w => w.Type == "UNRESOLVED_FIELD");
        var unresolvedValueWarnings = warnings.Count(w => w.Type == "UNRESOLVED_VALUE");
        var fileIncludeOverrides = allFiles.Count(f => string.Equals(f.ImportMode, "include", StringComparison.OrdinalIgnoreCase));
        var fileExcludeOverrides = allFiles.Count(f => string.Equals(f.ImportMode, "exclude", StringComparison.OrdinalIgnoreCase));
        return new NdImportExportResult(
            outputPath,
            warningsPath,
            files.Count,
            largeFileWarnings,
            emptyFolderWarnings,
            unresolvedFieldWarnings,
            unresolvedValueWarnings,
            fileIncludeOverrides,
            fileExcludeOverrides,
            accessDeniedWarnings);
    }

    private async Task<int> WriteWarningsReportAsync(
        string jobId,
        string warningsPath,
        IReadOnlyList<FileRecord> allFiles,
        IReadOnlyList<FolderRecord> folders,
        List<ExportWarning> warnings,
        CancellationToken cancellationToken)
    {
        var counts = await _jobStore.GetFolderImportCountsForJobAsync(jobId, cancellationToken);
        var countsByFolder = counts.ToDictionary(c => c.FolderId, c => c, StringComparer.OrdinalIgnoreCase);

        var emptyFolderWarnings = 0;

        await using var stream = new FileStream(warningsPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        await writer.WriteLineAsync("TYPE,FIELD,VALUE,RELATIVE PATH,FULL PATH,SIZE BYTES,DETAILS");

        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.SizeBytes <= LargeFileThresholdBytes && !file.IsLargeWarning)
            {
                continue;
            }

            warnings.Add(new ExportWarning(
                "LARGE_FILE",
                string.Empty,
                string.Empty,
                file.RelativePath,
                file.FullPath,
                file.SizeBytes.ToString(CultureInfo.InvariantCulture),
                "File exceeds 1.8GB"));
        }

        foreach (var file in allFiles.Where(f => string.Equals(f.ImportMode, "exclude", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(new ExportWarning(
                "EXCLUDED_FILE",
                string.Empty,
                string.Empty,
                file.RelativePath,
                file.FullPath,
                file.SizeBytes.ToString(CultureInfo.InvariantCulture),
                file.ImportReason ?? "User excluded"));
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
            warnings.Add(new ExportWarning(
                "EMPTY_FOLDER",
                string.Empty,
                string.Empty,
                folder.RelativePath,
                folder.FullPath,
                string.Empty,
                "No included files"));
        }

        foreach (var warning in warnings)
        {
            var row = string.Join(",", new[]
            {
                warning.Type,
                NdImportCsv.Escape(warning.Field),
                NdImportCsv.Escape(warning.Value),
                NdImportCsv.Escape(warning.RelativePath),
                NdImportCsv.Escape(warning.FullPath),
                warning.SizeBytes,
                NdImportCsv.Escape(warning.Details)
            });
            await writer.WriteLineAsync(row);
        }

        return emptyFolderWarnings;
    }

    private static IReadOnlyList<string> BuildProfileColumns(
        IReadOnlyList<FolderRecord> folders,
        IReadOnlyDictionary<string, string> folderProfiles,
        ProfileSchemaDictionary? schema)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var effectiveProfiles = BuildEffectiveProfiles(folders, folderProfiles);
        foreach (var profile in effectiveProfiles.Values)
        {
            foreach (var entry in profile)
            {
                var columnName = ResolveFieldName(entry, schema);
                if (string.IsNullOrWhiteSpace(columnName))
                {
                    continue;
                }

                if (seen.Add(columnName))
                {
                    columns.Add(columnName);
                }
            }
        }

        return columns;
    }

    private static Dictionary<string, IReadOnlyList<ProfileFieldEntry>> BuildEffectiveProfiles(
        IReadOnlyList<FolderRecord> folders,
        IReadOnlyDictionary<string, string> folderProfiles)
    {
        var map = new Dictionary<string, FolderRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            map[folder.FolderId] = folder;
        }

        var ordered = folders.OrderBy(f => f.Depth).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var effective = new Dictionary<string, IReadOnlyList<ProfileFieldEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in ordered)
        {
            if (string.Equals(folder.ProfileMode, "override", StringComparison.OrdinalIgnoreCase) &&
                folderProfiles.TryGetValue(folder.FolderId, out var payload))
            {
                effective[folder.FolderId] = ProfilePayloadCodec.Deserialize(payload);
                continue;
            }

            if (folder.ParentFolderId is not null && effective.TryGetValue(folder.ParentFolderId, out var parentProfile))
            {
                effective[folder.FolderId] = parentProfile;
            }
            else
            {
                effective[folder.FolderId] = Array.Empty<ProfileFieldEntry>();
            }
        }

        return effective;
    }

    private static List<string> BuildProfileValues(
        IReadOnlyList<string> columns,
        IReadOnlyList<ProfileFieldEntry> entries,
        NdImportExportOptions options,
        FileRecord file,
        List<ExportWarning> warnings)
    {
        var schema = options.ProfileSchema;
        var valuesByColumn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var column = ResolveFieldName(entry, schema);
            if (string.IsNullOrWhiteSpace(column))
            {
                continue;
            }

            var value = ResolveFieldValue(entry, options, file, warnings);
            if (!valuesByColumn.ContainsKey(column))
            {
                valuesByColumn[column] = value;
            }
        }

        var values = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            values.Add(NdImportCsv.Escape(valuesByColumn.TryGetValue(column, out var value) ? value : string.Empty));
        }

        return values;
    }

    private static string ResolveFieldName(ProfileFieldEntry entry, ProfileSchemaDictionary? schema)
    {
        if (schema is null)
        {
            return entry.Field;
        }

        if (entry.Mode == ProfileFieldMode.Code)
        {
            return schema.TryResolveFieldName(entry.Field, out var name) ? name : entry.Field;
        }

        return schema.TryResolveCanonicalFieldName(entry.Field, out var canonicalName)
            ? canonicalName
            : entry.Field;
    }

    private static string ResolveFieldValue(
        ProfileFieldEntry entry,
        NdImportExportOptions options,
        FileRecord file,
        List<ExportWarning> warnings)
    {
        var schema = options.ProfileSchema;
        if (schema is null)
        {
            return EscapeInlineSemicolons(entry.Value);
        }

        if (!schema.TryGetField(entry.Field, out var field))
        {
            warnings.Add(new ExportWarning(
                "UNRESOLVED_FIELD",
                entry.Field,
                entry.Value,
                file.RelativePath,
                file.FullPath,
                string.Empty,
                "Field not found in schema"));
            return EscapeInlineSemicolons(entry.Value);
        }

        var tokens = field.IsMultiValue
            ? SplitMultiValueTokens(entry.Value)
            : new List<string> { entry.Value.Trim() };

        var transformed = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            var resolved = ResolveSingleValue(entry, field, token, options.ValidateLookupKeys, file, warnings);
            transformed.Add(EscapeInlineSemicolons(resolved));
        }

        if (!field.IsMultiValue)
        {
            return transformed.Count == 0 ? string.Empty : transformed[0];
        }

        return string.Join("; ", transformed.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ResolveSingleValue(
        ProfileFieldEntry entry,
        ProfileSchemaField field,
        string token,
        bool validateLookupKeys,
        FileRecord file,
        List<ExportWarning> warnings)
    {
        if (field.IsLookup)
        {
            if (entry.Mode == ProfileFieldMode.Label)
            {
                if (field.ValuesByLabel.ContainsKey(token))
                {
                    return token;
                }

                if (field.ValuesByCode.TryGetValue(token, out var labelFromCode))
                {
                    return labelFromCode;
                }
            }
            else
            {
                if (!validateLookupKeys && field.ValuesByCode.TryGetValue(token, out var laxLabel))
                {
                    return laxLabel;
                }

                if (!validateLookupKeys)
                {
                    return token;
                }

                if (field.ValuesByCode.TryGetValue(token, out var strictLabel))
                {
                    return strictLabel;
                }
            }

            warnings.Add(new ExportWarning(
                "UNRESOLVED_VALUE",
                field.Name,
                token,
                file.RelativePath,
                file.FullPath,
                string.Empty,
                "Lookup key not found in synced metadata"));
            return token;
        }

        if (entry.Mode == ProfileFieldMode.Code)
        {
            if (field.ValuesByCode.TryGetValue(token, out var label))
            {
                return label;
            }

            warnings.Add(new ExportWarning(
                "UNRESOLVED_VALUE",
                field.Name,
                token,
                file.RelativePath,
                file.FullPath,
                string.Empty,
                "Value code not found in schema"));
            return token;
        }

        if (!string.IsNullOrWhiteSpace(token) && field.ValuesByLabel.Count > 0 && !field.ValuesByLabel.ContainsKey(token))
        {
            warnings.Add(new ExportWarning(
                "UNRESOLVED_VALUE",
                field.Name,
                token,
                file.RelativePath,
                file.FullPath,
                string.Empty,
                "Value label not found in schema"));
        }

        return token;
    }

    private static List<string> SplitMultiValueTokens(string value)
    {
        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
    }

    private static string EscapeInlineSemicolons(string value)
    {
        return value.Replace(";", "{;}", StringComparison.Ordinal);
    }

    private sealed record ExportWarning(
        string Type,
        string Field,
        string Value,
        string RelativePath,
        string FullPath,
        string SizeBytes,
        string Details);

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
