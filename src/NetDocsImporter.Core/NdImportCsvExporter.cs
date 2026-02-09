using System.Globalization;
using System.Diagnostics;
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

    public bool IncludeAllCabinetAttributes { get; set; } = true;

    public bool ExportLookupKeys { get; set; } = true;

    public bool ValidateLookupKeys { get; set; } = true;

    public EffectiveProfileDefaults? EffectiveProfileDefaults { get; set; }

    public bool UseNdImportDateFormat { get; set; }

    public string NdImportDateFormat { get; set; } = string.Empty;
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

public sealed class PreExportWarningItem
{
    public PreExportWarningItem(string type, string path, string detail)
    {
        Type = type;
        Path = path;
        Detail = detail;
    }

    public string Type { get; }

    public string Path { get; }

    public string Detail { get; }
}

public sealed class NdImportWarningPreviewResult
{
    public NdImportWarningPreviewResult(
        int largeFileWarnings,
        int emptyFolderWarnings,
        IReadOnlyList<PreExportWarningItem> warnings)
    {
        LargeFileWarnings = largeFileWarnings;
        EmptyFolderWarnings = emptyFolderWarnings;
        Warnings = warnings;
    }

    public int LargeFileWarnings { get; }

    public int EmptyFolderWarnings { get; }

    public IReadOnlyList<PreExportWarningItem> Warnings { get; }
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
        var diagnosticsDirectory = Path.Combine(reportsDirectory, "diagnostics");
        Directory.CreateDirectory(diagnosticsDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var outputPath = Path.Combine(reportsDirectory, $"ndimport-{jobId}-{timestamp}.csv");
        var warningsPath = Path.Combine(diagnosticsDirectory, $"ndimport-{jobId}-{timestamp}-warnings.csv");

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

        var includeProfileMetadata = options.IncludeProfileMetadata || (options.EffectiveProfileDefaults?.HasValues ?? false);

        var profileColumns = includeProfileMetadata
            ? BuildProfileColumns(
                folders,
                folderProfiles,
                options.ProfileSchema,
                options.EffectiveProfileDefaults,
                options.IncludeAllCabinetAttributes)
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

        var effectiveProfiles = includeProfileMetadata
            ? BuildEffectiveProfiles(folders, folderProfiles)
            : new Dictionary<string, IReadOnlyList<ProfileFieldEntry>>(StringComparer.OrdinalIgnoreCase);

        if (options.EffectiveProfileDefaults?.HasValues == true)
        {
            Trace.WriteLine($"Applying effective profile defaults to export: columns={options.EffectiveProfileDefaults.ValuesByAttributeId.Count}");
        }

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

            if (includeProfileMetadata)
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
                    row.Add(NdImportCsv.Escape(FormatLocalDate(createdUtc, options.UseNdImportDateFormat, options.NdImportDateFormat)));
                }
                else
                {
                    accessDeniedWarnings++;
                    row.Add(string.Empty);
                }

                row.Add(NdImportCsv.Escape(importedBy));
                row.Add(NdImportCsv.Escape(FormatLocalDate(file.ModifiedUtc, options.UseNdImportDateFormat, options.NdImportDateFormat)));
            }

            await writer.WriteLineAsync(string.Join(",", row));
        }

        var excludedWarnings = allFiles
            .Where(f => string.Equals(f.ImportMode, "exclude", StringComparison.OrdinalIgnoreCase))
            .Select(file => new ExportWarning(
                "EXCLUDED_FILE",
                string.Empty,
                string.Empty,
                file.RelativePath,
                file.FullPath,
                file.SizeBytes.ToString(CultureInfo.InvariantCulture),
                file.ImportReason ?? "User excluded"))
            .ToList();
        warnings.AddRange(excludedWarnings);

        var structuralWarnings = await BuildStructuralWarningsAsync(jobId, allFiles, folders, cancellationToken);
        warnings.AddRange(structuralWarnings.Warnings);

        largeFileWarnings = structuralWarnings.LargeFileWarnings;
        var emptyFolderWarnings = structuralWarnings.EmptyFolderWarnings;
        await WriteWarningsReportAsync(warningsPath, warnings, cancellationToken);
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

    public async Task<NdImportWarningPreviewResult> PreviewWarningsAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        await _jobStore.InitializeAsync(cancellationToken);
        var allFiles = await _jobStore.GetFilesForJobAsync(jobId, cancellationToken);
        var folders = await _jobStore.GetFoldersForJobAsync(jobId, cancellationToken);
        var structuralWarnings = await BuildStructuralWarningsAsync(jobId, allFiles, folders, cancellationToken);

        var items = structuralWarnings.Warnings
            .Where(w => string.Equals(w.Type, "LARGE_FILE", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(w.Type, "EMPTY_FOLDER", StringComparison.OrdinalIgnoreCase))
            .Select(w => new PreExportWarningItem(
                w.Type,
                string.IsNullOrWhiteSpace(w.RelativePath) ? w.FullPath : w.RelativePath,
                w.Details))
            .ToList();

        return new NdImportWarningPreviewResult(
            structuralWarnings.LargeFileWarnings,
            structuralWarnings.EmptyFolderWarnings,
            items);
    }

    private async Task<StructuralWarningSummary> BuildStructuralWarningsAsync(
        string jobId,
        IReadOnlyList<FileRecord> allFiles,
        IReadOnlyList<FolderRecord> folders,
        CancellationToken cancellationToken)
    {
        var counts = await _jobStore.GetFolderImportCountsForJobAsync(jobId, cancellationToken);
        var countsByFolder = counts.ToDictionary(c => c.FolderId, c => c, StringComparer.OrdinalIgnoreCase);

        var emptyFolderWarnings = 0;
        var warnings = new List<ExportWarning>();

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

        return new StructuralWarningSummary(
            warnings.Count(w => string.Equals(w.Type, "LARGE_FILE", StringComparison.OrdinalIgnoreCase)),
            emptyFolderWarnings,
            warnings);
    }

    private static async Task WriteWarningsReportAsync(
        string warningsPath,
        IReadOnlyList<ExportWarning> warnings,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(warningsPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        await writer.WriteLineAsync("TYPE,FIELD,VALUE,RELATIVE PATH,FULL PATH,SIZE BYTES,DETAILS");

        foreach (var warning in warnings)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    }

    private static IReadOnlyList<string> BuildProfileColumns(
        IReadOnlyList<FolderRecord> folders,
        IReadOnlyDictionary<string, string> folderProfiles,
        ProfileSchemaDictionary? schema,
        EffectiveProfileDefaults? effectiveDefaults,
        bool includeAllCabinetAttributes)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (schema is not null && includeAllCabinetAttributes)
        {
            foreach (var field in schema.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name))
                {
                    continue;
                }

                if (seen.Add(field.Name))
                {
                    columns.Add(field.Name);
                }
            }
        }

        if (effectiveDefaults?.HasValues == true)
        {
            foreach (var value in effectiveDefaults.ValuesByAttributeId.Values)
            {
                if (string.IsNullOrWhiteSpace(value.AttributeName))
                {
                    continue;
                }

                if (seen.Add(value.AttributeName))
                {
                    columns.Add(value.AttributeName);
                }
            }
        }

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
        var valuesByColumn = BuildDefaultValuesByColumn(options.EffectiveProfileDefaults, options.ExportLookupKeys);
        var explicitColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var column = ResolveFieldName(entry, schema);
            if (string.IsNullOrWhiteSpace(column))
            {
                continue;
            }

            if (explicitColumns.Contains(column))
            {
                continue;
            }

            var value = ResolveFieldValue(entry, options, file, warnings);
            valuesByColumn[column] = value;
            explicitColumns.Add(column);
        }

        var values = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            values.Add(NdImportCsv.Escape(valuesByColumn.TryGetValue(column, out var value) ? value : string.Empty));
        }

        return values;
    }

    private static Dictionary<string, string> BuildDefaultValuesByColumn(EffectiveProfileDefaults? effectiveDefaults, bool exportLookupKeys)
    {
        var valuesByColumn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (effectiveDefaults?.HasValues != true)
        {
            return valuesByColumn;
        }

        foreach (var value in effectiveDefaults.ValuesByAttributeId.Values)
        {
            if (string.IsNullOrWhiteSpace(value.AttributeName))
            {
                continue;
            }

            var preferred = exportLookupKeys
                ? (string.IsNullOrWhiteSpace(value.RawValue) ? value.DisplayValue : value.RawValue)
                : (string.IsNullOrWhiteSpace(value.DisplayValue) ? value.RawValue : value.DisplayValue);
            valuesByColumn[value.AttributeName] = EscapeInlineSemicolons(preferred);
        }

        return valuesByColumn;
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
            var resolved = ResolveSingleValue(
                entry,
                field,
                token,
                options.ValidateLookupKeys,
                options.ExportLookupKeys,
                file,
                warnings);
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
        bool exportLookupKeys,
        FileRecord file,
        List<ExportWarning> warnings)
    {
        if (field.IsLookup)
        {
            if (entry.Mode == ProfileFieldMode.Label)
            {
                if (field.ValuesByLabel.TryGetValue(token, out var codeFromLabel))
                {
                    return exportLookupKeys ? codeFromLabel : token;
                }

                if (field.ValuesByCode.TryGetValue(token, out var labelFromCode))
                {
                    return exportLookupKeys ? token : labelFromCode;
                }

                if (!validateLookupKeys)
                {
                    return token;
                }
            }
            else
            {
                if (field.ValuesByCode.TryGetValue(token, out var strictLabel))
                {
                    return exportLookupKeys ? token : strictLabel;
                }

                if (!validateLookupKeys)
                {
                    return token;
                }

                if (field.ValuesByLabel.TryGetValue(token, out var codeFromLabel))
                {
                    return exportLookupKeys ? codeFromLabel : token;
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
            if (field.ValuesByCode.Count == 0 && field.ValuesByLabel.Count == 0)
            {
                return token;
            }

            if (field.ValuesByCode.TryGetValue(token, out var label))
            {
                return exportLookupKeys ? token : label;
            }

            if (field.ValuesByLabel.TryGetValue(token, out var code))
            {
                return exportLookupKeys ? code : token;
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

        if (field.ValuesByLabel.TryGetValue(token, out var resolvedCode))
        {
            return exportLookupKeys ? resolvedCode : token;
        }

        if (!string.IsNullOrWhiteSpace(token) &&
            field.ValuesByLabel.Count > 0 &&
            !field.ValuesByLabel.ContainsKey(token) &&
            !field.ValuesByCode.ContainsKey(token))
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

    private sealed record StructuralWarningSummary(
        int LargeFileWarnings,
        int EmptyFolderWarnings,
        IReadOnlyList<ExportWarning> Warnings);

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

    private static string FormatLocalDate(DateTime utcValue, bool useNdImportDateFormat, string ndImportDateFormat = "")
    {
        var localValue = utcValue.ToLocalTime();
        if (!string.IsNullOrWhiteSpace(ndImportDateFormat))
        {
            return ndImportDateFormat.Trim().ToUpperInvariant() switch
            {
                "DMY" => localValue.ToString("d/M/yyyy H:mm:ss", CultureInfo.InvariantCulture),
                "YMD" => localValue.ToString("yyyy/M/d H:mm:ss", CultureInfo.InvariantCulture),
                "MDY" => localValue.ToString("M/d/yyyy H:mm:ss", CultureInfo.InvariantCulture),
                _ => localValue.ToString("d/M/yyyy H:mm:ss", CultureInfo.InvariantCulture)
            };
        }

        if (useNdImportDateFormat)
        {
            return localValue.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);
        }

        return localValue.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
