using System.Globalization;
using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.Tests;

public class NdImportCsvExporterTests
{
    [Fact]
    public async Task ExportAsync_WritesHeaderAndEscapedRow()
    {
        var tempRoot = CreateTempRoot();
        var reportsDir = Path.Combine(tempRoot, "reports");
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var rootFolderId = await InsertFolderAsync(store, jobId, tempRoot, string.Empty, null, 0, "include");
        var subFolderName = "Sub,Folder";
        var subFolderPath = Path.Combine(tempRoot, subFolderName);
        Directory.CreateDirectory(subFolderPath);
        var subFolderId = await InsertFolderAsync(store, jobId, subFolderPath, subFolderName, rootFolderId, 1, "include");

        var fileName = "Alpha,Report.txt";
        var filePath = Path.Combine(subFolderPath, fileName);
        await File.WriteAllTextAsync(filePath, "data");

        var creationUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(filePath, creationUtc);

        var modifiedUtc = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var fileRecord = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            filePath,
            Path.Combine(subFolderName, fileName),
            123,
            modifiedUtc,
            false,
            subFolderId,
            "inherit",
            null);
        await store.InsertFileAsync(fileRecord);

        var options = new NdImportExportOptions
        {
            IncludeAuditStamps = true,
            MappingMode = NdImportMappingMode.Mirror,
            AnchorFolderPath = "Cabinet,Root",
            ImportedBy = "Jane \"QA\", Jr."
        };

        var exporter = new NdImportCsvExporter(store);
        var result = await exporter.ExportAsync(jobId, reportsDir, options);

        var lines = await File.ReadAllLinesAsync(result.OutputPath);
        Assert.Equal(2, lines.Length);

        var expectedHeader = string.Join(",", new[]
        {
            "FULL PATH",
            "DOCUMENT NAME",
            "DOCUMENT EXTENSION",
            "FOLDER",
            "CREATED BY",
            "CREATED DATE",
            "LAST MODIFIED BY",
            "LAST MODIFIED DATE"
        });
        Assert.Equal(expectedHeader, lines[0]);

        var expectedCreated = creationUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var expectedModified = modifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var expectedFolder = Path.Combine("Cabinet,Root", subFolderName);
        var expectedImportedBy = "\"Jane \"\"QA\"\", Jr.\"";

        var expectedRow = string.Join(",", new[]
        {
            $"\"{filePath}\"",
            "\"Alpha,Report\"",
            "txt",
            $"\"{expectedFolder}\"",
            expectedImportedBy,
            expectedCreated,
            expectedImportedBy,
            expectedModified
        });

        Assert.Equal(expectedRow, lines[1]);

        var warningLines = await File.ReadAllLinesAsync(result.WarningsPath);
        Assert.Single(warningLines);
        Assert.Equal("TYPE,FIELD,VALUE,RELATIVE PATH,FULL PATH,SIZE BYTES,DETAILS", warningLines[0]);

        CleanupTempRoot(tempRoot);
    }

    [Fact]
    public async Task ExportAsync_FlattensFolderMappingToAnchor()
    {
        var tempRoot = CreateTempRoot();
        var reportsDir = Path.Combine(tempRoot, "reports");
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var rootFolderId = await InsertFolderAsync(store, jobId, "C:\\data", string.Empty, null, 0, "include");
        var deepFolderId = await InsertFolderAsync(store, jobId, "C:\\data\\deep\\more", Path.Combine("deep", "more"), rootFolderId, 1, "include");

        var fileRecord = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data\\deep\\more\\report.txt",
            Path.Combine("deep", "more", "report.txt"),
            456,
            new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            false,
            deepFolderId,
            "inherit",
            null);
        await store.InsertFileAsync(fileRecord);

        var options = new NdImportExportOptions
        {
            IncludeAuditStamps = false,
            MappingMode = NdImportMappingMode.Flatten,
            AnchorFolderPath = "CabinetRoot"
        };

        var exporter = new NdImportCsvExporter(store);
        var result = await exporter.ExportAsync(jobId, reportsDir, options);

        var lines = await File.ReadAllLinesAsync(result.OutputPath);
        Assert.Equal(2, lines.Length);

        var expectedHeader = "FULL PATH,DOCUMENT NAME,DOCUMENT EXTENSION,FOLDER";
        Assert.Equal(expectedHeader, lines[0]);

        var expectedRow = "C:\\data\\deep\\more\\report.txt,report,txt,CabinetRoot";
        Assert.Equal(expectedRow, lines[1]);

        CleanupTempRoot(tempRoot);
    }

    [Fact]
    public async Task ExportAsync_ExcludesFilesFromExcludedFolders()
    {
        var tempRoot = CreateTempRoot();
        var reportsDir = Path.Combine(tempRoot, "reports");
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var rootFolderId = await InsertFolderAsync(store, jobId, "C:\\data", string.Empty, null, 0, "include");
        var excludedFolderId = await InsertFolderAsync(store, jobId, "C:\\data\\excluded", "excluded", rootFolderId, 1, "exclude");
        var includedFolderId = await InsertFolderAsync(store, jobId, "C:\\data\\included", "included", rootFolderId, 1, "include");

        var excludedFile = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data\\excluded\\secret.txt",
            Path.Combine("excluded", "secret.txt"),
            100,
            DateTime.UtcNow,
            false,
            excludedFolderId,
            "inherit",
            null);
        await store.InsertFileAsync(excludedFile);

        var includedFile = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data\\included\\public.txt",
            Path.Combine("included", "public.txt"),
            200,
            DateTime.UtcNow,
            false,
            includedFolderId,
            "inherit",
            null);
        await store.InsertFileAsync(includedFile);

        var options = new NdImportExportOptions
        {
            IncludeAuditStamps = false,
            MappingMode = NdImportMappingMode.Mirror
        };

        var exporter = new NdImportCsvExporter(store);
        var result = await exporter.ExportAsync(jobId, reportsDir, options);

        var lines = await File.ReadAllLinesAsync(result.OutputPath);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("secret.txt", lines[1]);
        Assert.Contains("public.txt", lines[1]);

        CleanupTempRoot(tempRoot);
    }

    [Fact]
    public async Task ExportAsync_WritesWarningsForLargeFilesAndEmptyFolders()
    {
        var tempRoot = CreateTempRoot();
        var reportsDir = Path.Combine(tempRoot, "reports");
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var rootFolderId = await InsertFolderAsync(store, jobId, tempRoot, string.Empty, null, 0, "include");
        var emptyFolderId = await InsertFolderAsync(store, jobId, Path.Combine(tempRoot, "empty"), "empty", rootFolderId, 1, "include");
        Directory.CreateDirectory(Path.Combine(tempRoot, "empty"));

        var largeFolderId = await InsertFolderAsync(store, jobId, Path.Combine(tempRoot, "big"), "big", rootFolderId, 1, "include");
        Directory.CreateDirectory(Path.Combine(tempRoot, "big"));
        var largeFilePath = Path.Combine(tempRoot, "big", "huge.bin");
        await File.WriteAllTextAsync(largeFilePath, "data");

        var largeFile = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            largeFilePath,
            Path.Combine("big", "huge.bin"),
            2_000_000_000,
            DateTime.UtcNow,
            true,
            largeFolderId,
            "inherit",
            null);
        await store.InsertFileAsync(largeFile);

        var excludedFile = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            Path.Combine(tempRoot, "excluded.txt"),
            "excluded.txt",
            10,
            DateTime.UtcNow,
            false,
            rootFolderId,
            "exclude",
            "User excluded");
        await store.InsertFileAsync(excludedFile);

        var options = new NdImportExportOptions
        {
            IncludeAuditStamps = false,
            MappingMode = NdImportMappingMode.Mirror
        };

        var exporter = new NdImportCsvExporter(store);
        var result = await exporter.ExportAsync(jobId, reportsDir, options);

        var warningLines = await File.ReadAllLinesAsync(result.WarningsPath);
        Assert.Equal(4, warningLines.Length);
        Assert.Contains(warningLines, line => line.Contains("LARGE_FILE"));
        Assert.Contains(warningLines, line => line.Contains("huge.bin"));
        Assert.Contains(warningLines, line => line.Contains("2000000000"));
        Assert.Contains(warningLines, line => line.Contains("EMPTY_FOLDER"));
        Assert.Contains(warningLines, line => line.Contains("empty"));
        Assert.Contains(warningLines, line => line.Contains("EXCLUDED_FILE"));
        Assert.Contains(warningLines, line => line.Contains("excluded.txt"));

        CleanupTempRoot(tempRoot);
    }

    [Fact]
    public async Task ExportAsync_UsesSchemaForProfileColumnsAndWarnings()
    {
        var tempRoot = CreateTempRoot();
        var reportsDir = Path.Combine(tempRoot, "reports");
        var dbPath = Path.Combine(tempRoot, "jobs.db");
        var store = new JobStore(dbPath);
        await store.InitializeAsync();

        var jobId = Guid.NewGuid().ToString("N");
        await store.InsertJobAsync(new JobRecord(jobId, DateTime.UtcNow, "C:\\data", "Complete"));

        var rootFolderId = await InsertFolderAsync(store, jobId, "C:\\data", string.Empty, null, 0, "include", "override");

        var fileRecord = new FileRecord(
            Guid.NewGuid().ToString("N"),
            jobId,
            "C:\\data\\file.txt",
            "file.txt",
            100,
            DateTime.UtcNow,
            false,
            rootFolderId,
            "inherit",
            null);
        await store.InsertFileAsync(fileRecord);

        var payload = ProfilePayloadCodec.Serialize(new[]
        {
            new ProfileFieldEntry("1001", "2001", ProfileFieldMode.Code),
            new ProfileFieldEntry("1001", "9999", ProfileFieldMode.Code)
        });
        await store.UpsertFolderProfileAsync(jobId, rootFolderId, payload);

        var schema = new ProfileSchemaDictionary(
            "Cabinet",
            "1",
            new[]
            {
                new ProfileSchemaField(
                    "1001",
                    "Document Type",
                    new[] { new ProfileSchemaValue("2001", "Correspondence") })
            });

        var options = new NdImportExportOptions
        {
            IncludeAuditStamps = false,
            MappingMode = NdImportMappingMode.Mirror,
            IncludeProfileMetadata = true,
            ProfileSchema = schema
        };

        var exporter = new NdImportCsvExporter(store);
        var result = await exporter.ExportAsync(jobId, reportsDir, options);

        var lines = await File.ReadAllLinesAsync(result.OutputPath);
        Assert.Contains("Document Type", lines[0]);
        Assert.Contains("Correspondence", lines[1]);

        var warningLines = await File.ReadAllLinesAsync(result.WarningsPath);
        Assert.Contains("UNRESOLVED_VALUE", warningLines[1]);

        CleanupTempRoot(tempRoot);
    }

    private static async Task<string> InsertFolderAsync(
        JobStore store,
        string jobId,
        string fullPath,
        string relativePath,
        string? parentFolderId,
        int depth,
        string importMode,
        string profileMode = "inherit")
    {
        var folderId = Guid.NewGuid().ToString("N");
        var folder = new FolderRecord(
            folderId,
            jobId,
            fullPath,
            relativePath,
            parentFolderId,
            depth,
            true,
            false,
            DateTime.UtcNow,
            importMode,
            profileMode);

        await store.InsertFolderAsync(folder);
        return folderId;
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }
}
