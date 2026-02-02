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
                IsLargeWarning INTEGER
            );

            CREATE INDEX IF NOT EXISTS IX_Files_JobId ON Files(JobId);
            CREATE INDEX IF NOT EXISTS IX_Files_RelativePath ON Files(RelativePath);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
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
            (FileId, JobId, FullPath, RelativePath, SizeBytes, ModifiedUtc, IsLargeWarning)
            VALUES ($fileId, $jobId, $fullPath, $relativePath, $sizeBytes, $modifiedUtc, $isLargeWarning);
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
            SELECT FileId, JobId, FullPath, RelativePath, SizeBytes, ModifiedUtc, IsLargeWarning
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
                reader.GetInt64(6) == 1));
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

    public JobFileWriter OpenFileWriter()
    {
        return new JobFileWriter(_connectionString);
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
            (FileId, JobId, FullPath, RelativePath, SizeBytes, ModifiedUtc, IsLargeWarning)
            VALUES ($fileId, $jobId, $fullPath, $relativePath, $sizeBytes, $modifiedUtc, $isLargeWarning);
            """;
        _command.Parameters.Add("$fileId", SqliteType.Text);
        _command.Parameters.Add("$jobId", SqliteType.Text);
        _command.Parameters.Add("$fullPath", SqliteType.Text);
        _command.Parameters.Add("$relativePath", SqliteType.Text);
        _command.Parameters.Add("$sizeBytes", SqliteType.Integer);
        _command.Parameters.Add("$modifiedUtc", SqliteType.Text);
        _command.Parameters.Add("$isLargeWarning", SqliteType.Integer);
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

        _command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _command.Dispose();
        _connection.Dispose();
    }
}
