using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NetDocsImporter.Data;

public sealed class JobStore
{
    private readonly string _connectionString;

    public JobStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Jobs (
                JobId TEXT PRIMARY KEY,
                CreatedUtc TEXT,
                SourceRoot TEXT,
                Status TEXT
            );

            CREATE TABLE IF NOT EXISTS Files (
                FileId TEXT PRIMARY KEY,
                JobId TEXT,
                FullPath TEXT,
                RelativePath TEXT,
                SizeBytes INTEGER,
                ModifiedUtc TEXT,
                IsLargeWarning INTEGER,
                FolderId TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Transfers (
                TransferId TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                FileId TEXT NOT NULL,
                Attempt INTEGER NOT NULL,
                Status TEXT NOT NULL,
                StartedUtc TEXT NULL,
                FinishedUtc TEXT NULL,
                DurationMs INTEGER NULL,
                Error TEXT NULL,
                WorkerId INTEGER NULL,
                SimulatedDelayMs INTEGER NULL,
                HttpStatus INTEGER NULL,
                ResponseSnippet TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Folders (
                FolderId TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                FullPath TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                ParentFolderId TEXT NULL,
                Depth INTEGER NOT NULL,
                IsIncluded INTEGER NOT NULL DEFAULT 1,
                IsOverride INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL,
                ImportMode TEXT NOT NULL DEFAULT 'inherit',
                ProfileMode TEXT NOT NULL DEFAULT 'inherit'
            );

            CREATE TABLE IF NOT EXISTS FolderRules (
                RuleId TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                FolderId TEXT NOT NULL,
                RuleType TEXT NOT NULL,
                Scope TEXT NOT NULL,
                Notes TEXT NULL,
                CreatedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS FolderProfiles (
                ProfileId TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                FolderId TEXT NOT NULL,
                PayloadJson TEXT NULL,
                UpdatedUtc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Files_JobId ON Files(JobId);
            CREATE INDEX IF NOT EXISTS IX_Files_RelativePath ON Files(RelativePath);
            CREATE INDEX IF NOT EXISTS IX_Transfers_JobId ON Transfers(JobId);
            CREATE INDEX IF NOT EXISTS IX_Transfers_FileId ON Transfers(FileId);
            CREATE INDEX IF NOT EXISTS IX_Folders_JobId ON Folders(JobId);
            CREATE INDEX IF NOT EXISTS IX_Folders_ParentFolderId ON Folders(ParentFolderId);
            CREATE INDEX IF NOT EXISTS IX_Folders_RelativePath ON Folders(RelativePath);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnExistsAsync(connection, "Files", "FolderId", "TEXT NULL", cancellationToken);
        await EnsureColumnExistsAsync(connection, "Folders", "ImportMode", "TEXT NOT NULL DEFAULT 'inherit'", cancellationToken);
        await EnsureColumnExistsAsync(connection, "Folders", "ProfileMode", "TEXT NOT NULL DEFAULT 'inherit'", cancellationToken);
        await EnsureColumnExistsAsync(connection, "FolderProfiles", "FolderId", "TEXT NOT NULL", cancellationToken);

        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_Files_FolderId ON Files(FolderId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_FolderProfiles_FolderId ON FolderProfiles(FolderId);
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var fixImportMode = connection.CreateCommand();
        fixImportMode.CommandText = "UPDATE Folders SET ImportMode = 'inherit' WHERE ImportMode IS NULL;";
        await fixImportMode.ExecuteNonQueryAsync(cancellationToken);

        await using var fixProfileMode = connection.CreateCommand();
        fixProfileMode.CommandText = "UPDATE Folders SET ProfileMode = 'inherit' WHERE ProfileMode IS NULL;";
        await fixProfileMode.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertJobAsync(JobRecord job, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Jobs (JobId, CreatedUtc, SourceRoot, Status)
            VALUES ($jobId, $createdUtc, $sourceRoot, $status);
            """;

        command.Parameters.AddWithValue("$jobId", job.JobId);
        command.Parameters.AddWithValue("$createdUtc", ToUtcString(job.CreatedUtc));
        command.Parameters.AddWithValue("$sourceRoot", job.SourceRoot);
        command.Parameters.AddWithValue("$status", job.Status);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateJobStatusAsync(string jobId, string status, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Jobs
            SET Status = $status
            WHERE JobId = $jobId;
            """;

        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$jobId", jobId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertFileAsync(FileRecord file, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Files
            (FileId, JobId, FullPath, RelativePath, SizeBytes, ModifiedUtc, IsLargeWarning, FolderId)
            VALUES ($fileId, $jobId, $fullPath, $relativePath, $sizeBytes, $modifiedUtc, $isLargeWarning, $folderId);
            """;

        BindFileParameters(command, file);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobId, CreatedUtc, SourceRoot, Status
            FROM Jobs
            WHERE JobId = $jobId;
            """;

        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JobRecord(
            reader.GetString(0),
            ParseUtc(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3));
    }

    public async Task<IReadOnlyList<FileRecord>> GetFilesForJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var results = new List<FileRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FileId, JobId, FullPath, RelativePath, SizeBytes, ModifiedUtc, IsLargeWarning, FolderId
            FROM Files
            WHERE JobId = $jobId
            ORDER BY RelativePath;
            """;

        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                ParseUtc(reader.GetString(5)),
                reader.GetInt64(6) == 1,
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    public async Task<IReadOnlyList<FolderRecord>> GetFoldersForJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var results = new List<FolderRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FolderId, JobId, FullPath, RelativePath, ParentFolderId, Depth, IsIncluded, IsOverride, CreatedUtc, ImportMode, ProfileMode
            FROM Folders
            WHERE JobId = $jobId
            ORDER BY Depth, RelativePath;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FolderRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt64(6) == 1,
                reader.GetInt64(7) == 1,
                ParseUtc(reader.GetString(8)),
                reader.GetString(9),
                reader.GetString(10)));
        }

        return results;
    }

    public async Task<IReadOnlyList<JobSummary>> GetRecentJobsAsync(int count, CancellationToken cancellationToken = default)
    {
        var results = new List<JobSummary>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.JobId,
                   j.CreatedUtc,
                   j.SourceRoot,
                   j.Status,
                   COUNT(f.FileId) AS FileCount,
                   COALESCE(SUM(f.SizeBytes), 0) AS TotalBytes,
                   COALESCE(SUM(f.IsLargeWarning), 0) AS LargeWarnings
            FROM Jobs j
            LEFT JOIN Files f ON f.JobId = j.JobId
            GROUP BY j.JobId, j.CreatedUtc, j.SourceRoot, j.Status
            ORDER BY j.CreatedUtc DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", count);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new JobSummary(
                reader.GetString(0),
                ParseUtc(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<string, TransferState>> GetTransferStatesByFileAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, TransferState>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TransferId, FileId, Status, Attempt
            FROM Transfers
            WHERE JobId = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var transferId = reader.GetString(0);
            var fileId = reader.GetString(1);
            var status = reader.GetString(2);
            var attempt = reader.GetInt32(3);
            results[fileId] = new TransferState(transferId, fileId, status, attempt);
        }

        return results;
    }

    public async Task UpsertTransferQueuedAsync(TransferRecord transfer, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Transfers
            (TransferId, JobId, FileId, Attempt, Status, StartedUtc, FinishedUtc, DurationMs, Error, WorkerId, SimulatedDelayMs, HttpStatus, ResponseSnippet)
            VALUES ($transferId, $jobId, $fileId, $attempt, $status, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
            ON CONFLICT(TransferId) DO UPDATE SET
                Attempt = $attempt,
                Status = $status,
                StartedUtc = NULL,
                FinishedUtc = NULL,
                DurationMs = NULL,
                Error = NULL,
                WorkerId = NULL,
                SimulatedDelayMs = NULL,
                HttpStatus = NULL,
                ResponseSnippet = NULL;
            """;

        command.Parameters.AddWithValue("$transferId", transfer.TransferId);
        command.Parameters.AddWithValue("$jobId", transfer.JobId);
        command.Parameters.AddWithValue("$fileId", transfer.FileId);
        command.Parameters.AddWithValue("$attempt", transfer.Attempt);
        command.Parameters.AddWithValue("$status", transfer.Status);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateTransferRunningAsync(
        string transferId,
        int attempt,
        DateTime startedUtc,
        int workerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Transfers
            SET Status = $status,
                Attempt = $attempt,
                StartedUtc = $startedUtc,
                WorkerId = $workerId
            WHERE TransferId = $transferId;
            """;

        command.Parameters.AddWithValue("$status", "Running");
        command.Parameters.AddWithValue("$attempt", attempt);
        command.Parameters.AddWithValue("$startedUtc", ToUtcString(startedUtc));
        command.Parameters.AddWithValue("$workerId", workerId);
        command.Parameters.AddWithValue("$transferId", transferId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateTransferFinishedAsync(
        string transferId,
        string status,
        DateTime finishedUtc,
        long durationMs,
        string? error,
        int? httpStatus,
        string? responseSnippet,
        int? simulatedDelayMs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Transfers
            SET Status = $status,
                FinishedUtc = $finishedUtc,
                DurationMs = $durationMs,
                Error = $error,
                HttpStatus = $httpStatus,
                ResponseSnippet = $responseSnippet,
                SimulatedDelayMs = $simulatedDelayMs
            WHERE TransferId = $transferId;
            """;

        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$finishedUtc", ToUtcString(finishedUtc));
        command.Parameters.AddWithValue("$durationMs", durationMs);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$httpStatus", (object?)httpStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$responseSnippet", (object?)responseSnippet ?? DBNull.Value);
        command.Parameters.AddWithValue("$simulatedDelayMs", (object?)simulatedDelayMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$transferId", transferId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkQueuedTransfersCanceledAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Transfers
            SET Status = $status,
                FinishedUtc = $finishedUtc
            WHERE JobId = $jobId
              AND Status = $queued;
            """;

        command.Parameters.AddWithValue("$status", "Canceled");
        command.Parameters.AddWithValue("$finishedUtc", ToUtcString(DateTime.UtcNow));
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$queued", "Queued");

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TransferStatusCounts> GetTransferCountsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        long total = 0;
        long queued = 0;
        long running = 0;
        long succeeded = 0;
        long failed = 0;
        long canceled = 0;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Status, COUNT(*)
            FROM Transfers
            WHERE JobId = $jobId
            GROUP BY Status;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var status = reader.GetString(0);
            var count = reader.GetInt64(1);

            total += count;
            switch (status)
            {
                case "Queued":
                    queued = count;
                    break;
                case "Running":
                    running = count;
                    break;
                case "Succeeded":
                    succeeded = count;
                    break;
                case "Failed":
                    failed = count;
                    break;
                case "Canceled":
                    canceled = count;
                    break;
            }
        }

        return new TransferStatusCounts(total, queued, running, succeeded, failed, canceled);
    }

    public async Task<IReadOnlyList<TransferSummary>> GetLatestTransfersAsync(
        string jobId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TransferSummary>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.TransferId,
                   t.FileId,
                   f.RelativePath,
                   t.Status,
                   t.Attempt,
                   t.DurationMs,
                   t.Error
            FROM Transfers t
            LEFT JOIN Files f ON f.FileId = t.FileId
            WHERE t.JobId = $jobId
            ORDER BY t.FinishedUtc DESC, t.StartedUtc DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TransferSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return results;
    }

    public JobFileWriter OpenFileWriter()
    {
        return new JobFileWriter(_connectionString);
    }

    public JobFolderWriter OpenFolderWriter()
    {
        return new JobFolderWriter(_connectionString);
    }

    public async Task InsertFolderAsync(FolderRecord folder, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Folders
            (FolderId, JobId, FullPath, RelativePath, ParentFolderId, Depth, IsIncluded, IsOverride, CreatedUtc, ImportMode, ProfileMode)
            VALUES ($folderId, $jobId, $fullPath, $relativePath, $parentFolderId, $depth, $isIncluded, $isOverride, $createdUtc, $importMode, $profileMode);
            """;

        BindFolderParameters(command, folder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FolderRecord?> GetRootFolderAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FolderId, JobId, FullPath, RelativePath, ParentFolderId, Depth, IsIncluded, IsOverride, CreatedUtc, ImportMode, ProfileMode
            FROM Folders
            WHERE JobId = $jobId
              AND ParentFolderId IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FolderRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt64(6) == 1,
            reader.GetInt64(7) == 1,
            ParseUtc(reader.GetString(8)),
            reader.IsDBNull(9) ? "inherit" : reader.GetString(9),
            reader.IsDBNull(10) ? "inherit" : reader.GetString(10));
    }

    public async Task<IReadOnlyList<FolderRecord>> GetChildFoldersAsync(
        string jobId,
        string? parentFolderId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FolderRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = parentFolderId is null
            ? """
              SELECT FolderId, JobId, FullPath, RelativePath, ParentFolderId, Depth, IsIncluded, IsOverride, CreatedUtc, ImportMode, ProfileMode
              FROM Folders
              WHERE JobId = $jobId AND ParentFolderId IS NULL
              ORDER BY RelativePath;
              """
            : """
              SELECT FolderId, JobId, FullPath, RelativePath, ParentFolderId, Depth, IsIncluded, IsOverride, CreatedUtc, ImportMode, ProfileMode
              FROM Folders
              WHERE JobId = $jobId AND ParentFolderId = $parentFolderId
              ORDER BY RelativePath;
              """;

        command.Parameters.AddWithValue("$jobId", jobId);
        if (parentFolderId is not null)
        {
            command.Parameters.AddWithValue("$parentFolderId", parentFolderId);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FolderRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt64(6) == 1,
                reader.GetInt64(7) == 1,
                ParseUtc(reader.GetString(8)),
                reader.IsDBNull(9) ? "inherit" : reader.GetString(9),
                reader.IsDBNull(10) ? "inherit" : reader.GetString(10)));
        }

        return results;
    }

    public async Task<IReadOnlyList<FileRecord>> GetChildFilesAsync(
        string jobId,
        string folderId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FileRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FileId, JobId, FullPath, RelativePath, SizeBytes, ModifiedUtc, IsLargeWarning, FolderId
            FROM Files
            WHERE JobId = $jobId AND FolderId = $folderId
            ORDER BY RelativePath
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                ParseUtc(reader.GetString(5)),
                reader.GetInt64(6) == 1,
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    public async Task UpdateFolderOverrideAsync(
        string folderId,
        bool isOverride,
        bool isIncluded,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Folders
            SET IsOverride = $isOverride,
                IsIncluded = $isIncluded
            WHERE FolderId = $folderId;
            """;

        command.Parameters.AddWithValue("$isOverride", isOverride ? 1 : 0);
        command.Parameters.AddWithValue("$isIncluded", isIncluded ? 1 : 0);
        command.Parameters.AddWithValue("$folderId", folderId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateFolderImportModeAsync(string folderId, string importMode, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Folders
            SET ImportMode = $importMode
            WHERE FolderId = $folderId;
            """;

        command.Parameters.AddWithValue("$importMode", importMode);
        command.Parameters.AddWithValue("$folderId", folderId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateFolderProfileModeAsync(string folderId, string profileMode, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Folders
            SET ProfileMode = $profileMode
            WHERE FolderId = $folderId;
            """;

        command.Parameters.AddWithValue("$profileMode", profileMode);
        command.Parameters.AddWithValue("$folderId", folderId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetFolderProfilePayloadAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PayloadJson
            FROM FolderProfiles
            WHERE FolderId = $folderId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$folderId", folderId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result == DBNull.Value ? null : result as string;
    }

    public async Task UpsertFolderProfileAsync(string jobId, string folderId, string? payloadJson, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FolderProfiles (ProfileId, JobId, FolderId, PayloadJson, UpdatedUtc)
            VALUES ($profileId, $jobId, $folderId, $payloadJson, $updatedUtc)
            ON CONFLICT(FolderId) DO UPDATE SET
                PayloadJson = $payloadJson,
                UpdatedUtc = $updatedUtc;
            """;

        command.Parameters.AddWithValue("$profileId", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$payloadJson", (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", ToUtcString(DateTime.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetEffectiveFolderProfilePayloadAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE folder_chain(FolderId, ParentFolderId, ProfileMode) AS (
                SELECT FolderId, ParentFolderId, ProfileMode
                FROM Folders
                WHERE FolderId = $folderId
                UNION ALL
                SELECT f.FolderId, f.ParentFolderId, f.ProfileMode
                FROM Folders f
                JOIN folder_chain fc ON f.FolderId = fc.ParentFolderId
            )
            SELECT fp.PayloadJson
            FROM folder_chain fc
            JOIN FolderProfiles fp ON fp.FolderId = fc.FolderId
            WHERE fc.ProfileMode = 'override'
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$folderId", folderId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result == DBNull.Value ? null : result as string;
    }

    public async Task ApplyProfileToDescendantsAsync(
        string jobId,
        string folderId,
        string? payloadJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = connection.BeginTransaction();

        var descendantIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                WITH RECURSIVE folder_tree(FolderId, ParentFolderId) AS (
                    SELECT FolderId, ParentFolderId
                    FROM Folders
                    WHERE ParentFolderId = $folderId
                    UNION ALL
                    SELECT f.FolderId, f.ParentFolderId
                    FROM Folders f
                    JOIN folder_tree ft ON f.ParentFolderId = ft.FolderId
                )
                SELECT FolderId FROM folder_tree;
                """;
            command.Parameters.AddWithValue("$folderId", folderId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                descendantIds.Add(reader.GetString(0));
            }
        }

        foreach (var id in descendantIds)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Folders
                SET ProfileMode = 'override'
                WHERE FolderId = $folderId AND ProfileMode = 'inherit';
                """;
            update.Parameters.AddWithValue("$folderId", id);
            var rows = await update.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
            {
                await using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO FolderProfiles (ProfileId, JobId, FolderId, PayloadJson, UpdatedUtc)
                    VALUES ($profileId, $jobId, $folderId, $payloadJson, $updatedUtc)
                    ON CONFLICT(FolderId) DO UPDATE SET
                        PayloadJson = $payloadJson,
                        UpdatedUtc = $updatedUtc;
                    """;

                upsert.Parameters.AddWithValue("$profileId", Guid.NewGuid().ToString("N"));
                upsert.Parameters.AddWithValue("$jobId", jobId);
                upsert.Parameters.AddWithValue("$folderId", id);
                upsert.Parameters.AddWithValue("$payloadJson", (object?)payloadJson ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$updatedUtc", ToUtcString(DateTime.UtcNow));
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ApplyImportModeToDescendantsAsync(
        string jobId,
        string folderId,
        string importMode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE folder_tree(FolderId, ParentFolderId) AS (
                SELECT FolderId, ParentFolderId
                FROM Folders
                WHERE ParentFolderId = $folderId
                UNION ALL
                SELECT f.FolderId, f.ParentFolderId
                FROM Folders f
                JOIN folder_tree ft ON f.ParentFolderId = ft.FolderId
            )
            UPDATE Folders
            SET ImportMode = $importMode
            WHERE FolderId IN (SELECT FolderId FROM folder_tree)
              AND ImportMode = 'inherit';
            """;

        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$importMode", importMode);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddFolderRuleAsync(
        string jobId,
        string folderId,
        string ruleType,
        string scope,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FolderRules
            (RuleId, JobId, FolderId, RuleType, Scope, Notes, CreatedUtc)
            VALUES ($ruleId, $jobId, $folderId, $ruleType, $scope, $notes, $createdUtc);
            """;

        command.Parameters.AddWithValue("$ruleId", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$ruleType", ruleType);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", ToUtcString(DateTime.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(long totalFiles, long totalBytes, long largeFiles, long excludedFolders)> GetFolderSummaryAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE folder_tree(FolderId, ParentFolderId, EffectiveIncluded) AS (
                SELECT FolderId,
                       ParentFolderId,
                       CASE
                           WHEN ImportMode = 'exclude' THEN 0
                           WHEN ImportMode = 'include' THEN 1
                           ELSE 1
                       END
                FROM Folders
                WHERE FolderId = $folderId
                UNION ALL
                SELECT f.FolderId,
                       f.ParentFolderId,
                       CASE
                           WHEN f.ImportMode = 'exclude' THEN 0
                           WHEN f.ImportMode = 'include' THEN 1
                           ELSE ft.EffectiveIncluded
                       END
                FROM Folders f
                JOIN folder_tree ft ON f.ParentFolderId = ft.FolderId
            )
            SELECT
                (SELECT COUNT(files.FileId)
                 FROM Files files
                 JOIN folder_tree ft2 ON files.FolderId = ft2.FolderId) AS TotalFiles,
                (SELECT COALESCE(SUM(files.SizeBytes), 0)
                 FROM Files files
                 JOIN folder_tree ft2 ON files.FolderId = ft2.FolderId) AS TotalBytes,
                (SELECT COALESCE(SUM(files.IsLargeWarning), 0)
                 FROM Files files
                 JOIN folder_tree ft2 ON files.FolderId = ft2.FolderId) AS LargeFiles,
                (SELECT COUNT(*) FROM folder_tree WHERE EffectiveIncluded = 0) AS ExcludedFolders;
            """;

        command.Parameters.AddWithValue("$folderId", folderId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0, 0, 0);
        }

        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    public async Task<(long included, long excluded)> GetImportSelectionCountsAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE folder_tree(FolderId, ParentFolderId, EffectiveIncluded) AS (
                SELECT FolderId,
                       ParentFolderId,
                       CASE
                           WHEN ImportMode = 'exclude' THEN 0
                           WHEN ImportMode = 'include' THEN 1
                           ELSE 1
                       END
                FROM Folders
                WHERE JobId = $jobId AND ParentFolderId IS NULL
                UNION ALL
                SELECT f.FolderId,
                       f.ParentFolderId,
                       CASE
                           WHEN f.ImportMode = 'exclude' THEN 0
                           WHEN f.ImportMode = 'include' THEN 1
                           ELSE ft.EffectiveIncluded
                       END
                FROM Folders f
                JOIN folder_tree ft ON f.ParentFolderId = ft.FolderId
            )
            SELECT
                COALESCE(SUM(CASE WHEN files.FileId IS NOT NULL AND ft.EffectiveIncluded = 1 THEN 1 ELSE 0 END), 0) AS IncludedFiles,
                COALESCE(SUM(CASE WHEN files.FileId IS NOT NULL AND ft.EffectiveIncluded = 0 THEN 1 ELSE 0 END), 0) AS ExcludedFiles
            FROM folder_tree ft
            LEFT JOIN Files files ON files.FolderId = ft.FolderId;
            """;

        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public async Task<IReadOnlyList<FolderImportCounts>> GetFolderImportCountsForJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FolderImportCounts>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE folder_tree(FolderId, ParentFolderId, EffectiveIncluded) AS (
                SELECT FolderId,
                       ParentFolderId,
                       CASE
                           WHEN ImportMode = 'exclude' THEN 0
                           WHEN ImportMode = 'include' THEN 1
                           ELSE 1
                       END
                FROM Folders
                WHERE JobId = $jobId AND ParentFolderId IS NULL
                UNION ALL
                SELECT f.FolderId,
                       f.ParentFolderId,
                       CASE
                           WHEN f.ImportMode = 'exclude' THEN 0
                           WHEN f.ImportMode = 'include' THEN 1
                           ELSE ft.EffectiveIncluded
                       END
                FROM Folders f
                JOIN folder_tree ft ON f.ParentFolderId = ft.FolderId
            ),
            direct_files AS (
                SELECT FolderId, COUNT(*) AS DirectFiles
                FROM Files
                GROUP BY FolderId
            ),
            descendants(AncestorId, DescendantId) AS (
                SELECT FolderId, FolderId FROM folder_tree
                UNION ALL
                SELECT d.AncestorId, ft.FolderId
                FROM descendants d
                JOIN folder_tree ft ON ft.ParentFolderId = d.DescendantId
            )
            SELECT ft.FolderId,
                   CASE WHEN ft.EffectiveIncluded = 1 THEN COALESCE(df.DirectFiles, 0) ELSE 0 END AS IncludedDirectFiles,
                   COALESCE(SUM(CASE WHEN ft2.EffectiveIncluded = 1 THEN COALESCE(df2.DirectFiles, 0) ELSE 0 END), 0) AS IncludedDescendantFiles,
                   ft.EffectiveIncluded
            FROM folder_tree ft
            LEFT JOIN direct_files df ON df.FolderId = ft.FolderId
            LEFT JOIN descendants d ON d.AncestorId = ft.FolderId
            LEFT JOIN folder_tree ft2 ON ft2.FolderId = d.DescendantId
            LEFT JOIN direct_files df2 ON df2.FolderId = ft2.FolderId
            GROUP BY ft.FolderId, df.DirectFiles, ft.EffectiveIncluded
            ORDER BY ft.FolderId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FolderImportCounts(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3) == 1));
        }

        return results;
    }

    public async Task<IReadOnlyList<FileRecord>> GetIncludedFilesForJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FileRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE folder_tree(FolderId, ParentFolderId, EffectiveIncluded) AS (
                SELECT FolderId,
                       ParentFolderId,
                       CASE
                           WHEN ImportMode = 'exclude' THEN 0
                           WHEN ImportMode = 'include' THEN 1
                           ELSE 1
                       END
                FROM Folders
                WHERE JobId = $jobId AND ParentFolderId IS NULL
                UNION ALL
                SELECT f.FolderId,
                       f.ParentFolderId,
                       CASE
                           WHEN f.ImportMode = 'exclude' THEN 0
                           WHEN f.ImportMode = 'include' THEN 1
                           ELSE ft.EffectiveIncluded
                       END
                FROM Folders f
                JOIN folder_tree ft ON f.ParentFolderId = ft.FolderId
            )
            SELECT files.FileId, files.JobId, files.FullPath, files.RelativePath, files.SizeBytes, files.ModifiedUtc, files.IsLargeWarning, files.FolderId
            FROM folder_tree ft
            JOIN Files files ON files.FolderId = ft.FolderId
            WHERE ft.EffectiveIncluded = 1
            ORDER BY files.RelativePath;
            """;

        command.Parameters.AddWithValue("$jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                ParseUtc(reader.GetString(5)),
                reader.GetInt64(6) == 1,
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    private static void BindFileParameters(SqliteCommand command, FileRecord file)
    {
        command.Parameters.AddWithValue("$fileId", file.FileId);
        command.Parameters.AddWithValue("$jobId", file.JobId);
        command.Parameters.AddWithValue("$fullPath", file.FullPath);
        command.Parameters.AddWithValue("$relativePath", file.RelativePath);
        command.Parameters.AddWithValue("$sizeBytes", file.SizeBytes);
        command.Parameters.AddWithValue("$modifiedUtc", ToUtcString(file.ModifiedUtc));
        command.Parameters.AddWithValue("$isLargeWarning", file.IsLargeWarning ? 1 : 0);
        command.Parameters.AddWithValue("$folderId", (object?)file.FolderId ?? DBNull.Value);
    }

    private static void BindFolderParameters(SqliteCommand command, FolderRecord folder)
    {
        command.Parameters.AddWithValue("$folderId", folder.FolderId);
        command.Parameters.AddWithValue("$jobId", folder.JobId);
        command.Parameters.AddWithValue("$fullPath", folder.FullPath);
        command.Parameters.AddWithValue("$relativePath", folder.RelativePath);
        command.Parameters.AddWithValue("$parentFolderId", (object?)folder.ParentFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$depth", folder.Depth);
        command.Parameters.AddWithValue("$isIncluded", folder.IsIncluded ? 1 : 0);
        command.Parameters.AddWithValue("$isOverride", folder.IsOverride ? 1 : 0);
        command.Parameters.AddWithValue("$createdUtc", ToUtcString(folder.CreatedUtc));
        command.Parameters.AddWithValue("$importMode", folder.ImportMode);
        command.Parameters.AddWithValue("$profileMode", folder.ProfileMode);
    }

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToUtcString(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}

public sealed class JobFileWriter : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _command;

    public JobFileWriter(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        _command = _connection.CreateCommand();
        _command.CommandText = """
            INSERT OR REPLACE INTO Files
            (FileId, JobId, FullPath, RelativePath, SizeBytes, ModifiedUtc, IsLargeWarning, FolderId)
            VALUES ($fileId, $jobId, $fullPath, $relativePath, $sizeBytes, $modifiedUtc, $isLargeWarning, $folderId);
            """;
        _command.Parameters.Add("$fileId", SqliteType.Text);
        _command.Parameters.Add("$jobId", SqliteType.Text);
        _command.Parameters.Add("$fullPath", SqliteType.Text);
        _command.Parameters.Add("$relativePath", SqliteType.Text);
        _command.Parameters.Add("$sizeBytes", SqliteType.Integer);
        _command.Parameters.Add("$modifiedUtc", SqliteType.Text);
        _command.Parameters.Add("$isLargeWarning", SqliteType.Integer);
        _command.Parameters.Add("$folderId", SqliteType.Text);
    }

    public void Insert(FileRecord file)
    {
        _command.Parameters["$fileId"].Value = file.FileId;
        _command.Parameters["$jobId"].Value = file.JobId;
        _command.Parameters["$fullPath"].Value = file.FullPath;
        _command.Parameters["$relativePath"].Value = file.RelativePath;
        _command.Parameters["$sizeBytes"].Value = file.SizeBytes;
        _command.Parameters["$modifiedUtc"].Value = file.ModifiedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        _command.Parameters["$isLargeWarning"].Value = file.IsLargeWarning ? 1 : 0;
        _command.Parameters["$folderId"].Value = (object?)file.FolderId ?? DBNull.Value;

        _command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _command.Dispose();
        _connection.Dispose();
    }
}

public sealed class JobFolderWriter : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _command;

    public JobFolderWriter(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        _command = _connection.CreateCommand();
        _command.CommandText = """
            INSERT OR REPLACE INTO Folders
            (FolderId, JobId, FullPath, RelativePath, ParentFolderId, Depth, IsIncluded, IsOverride, CreatedUtc, ImportMode, ProfileMode)
            VALUES ($folderId, $jobId, $fullPath, $relativePath, $parentFolderId, $depth, $isIncluded, $isOverride, $createdUtc, $importMode, $profileMode);
            """;

        _command.Parameters.Add("$folderId", SqliteType.Text);
        _command.Parameters.Add("$jobId", SqliteType.Text);
        _command.Parameters.Add("$fullPath", SqliteType.Text);
        _command.Parameters.Add("$relativePath", SqliteType.Text);
        _command.Parameters.Add("$parentFolderId", SqliteType.Text);
        _command.Parameters.Add("$depth", SqliteType.Integer);
        _command.Parameters.Add("$isIncluded", SqliteType.Integer);
        _command.Parameters.Add("$isOverride", SqliteType.Integer);
        _command.Parameters.Add("$createdUtc", SqliteType.Text);
        _command.Parameters.Add("$importMode", SqliteType.Text);
        _command.Parameters.Add("$profileMode", SqliteType.Text);
    }

    public void Insert(FolderRecord folder)
    {
        _command.Parameters["$folderId"].Value = folder.FolderId;
        _command.Parameters["$jobId"].Value = folder.JobId;
        _command.Parameters["$fullPath"].Value = folder.FullPath;
        _command.Parameters["$relativePath"].Value = folder.RelativePath;
        _command.Parameters["$parentFolderId"].Value = (object?)folder.ParentFolderId ?? DBNull.Value;
        _command.Parameters["$depth"].Value = folder.Depth;
        _command.Parameters["$isIncluded"].Value = folder.IsIncluded ? 1 : 0;
        _command.Parameters["$isOverride"].Value = folder.IsOverride ? 1 : 0;
        _command.Parameters["$createdUtc"].Value = folder.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        _command.Parameters["$importMode"].Value = folder.ImportMode;
        _command.Parameters["$profileMode"].Value = folder.ProfileMode;

        _command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _command.Dispose();
        _connection.Dispose();
    }
}
