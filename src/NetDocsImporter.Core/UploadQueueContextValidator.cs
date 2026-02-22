namespace NetDocsImporter.Core;

public static class UploadQueueContextValidator
{
    public static bool TryValidate(
        UploadQueueSnapshot snapshot,
        string currentRepositoryId,
        string currentCabinetId,
        string currentApiBaseUrl,
        out string error)
    {
        if (snapshot is null)
        {
            error = "Job snapshot is invalid.";
            return false;
        }

        var snapshotRepositoryId = NormalizeToken(snapshot.RepositoryId);
        var snapshotCabinetId = NormalizeToken(snapshot.CabinetId);
        var currentRepository = NormalizeToken(currentRepositoryId);
        var currentCabinet = NormalizeToken(currentCabinetId);
        var snapshotContextRepository = NormalizeToken(snapshot.PlanContext.RepositoryId);
        var snapshotContextCabinet = NormalizeToken(snapshot.PlanContext.CabinetId);
        var snapshotApiBase = NormalizeUrl(snapshot.PlanContext.ApiBaseUrl);
        var currentApiBase = NormalizeUrl(currentApiBaseUrl);

        if (!string.Equals(snapshotRepositoryId, currentRepository, StringComparison.OrdinalIgnoreCase))
        {
            error = BuildMismatchError("repository", snapshotRepositoryId, currentRepository);
            return false;
        }

        if (!string.Equals(snapshotCabinetId, currentCabinet, StringComparison.OrdinalIgnoreCase))
        {
            error = BuildMismatchError("cabinet", snapshotCabinetId, currentCabinet);
            return false;
        }

        if (!string.Equals(snapshotContextRepository, snapshotRepositoryId, StringComparison.OrdinalIgnoreCase))
        {
            error = BuildMismatchError("snapshot context repository", snapshotContextRepository, snapshotRepositoryId);
            return false;
        }

        if (!string.Equals(snapshotContextCabinet, snapshotCabinetId, StringComparison.OrdinalIgnoreCase))
        {
            error = BuildMismatchError("snapshot context cabinet", snapshotContextCabinet, snapshotCabinetId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshotApiBase) ||
            !string.Equals(snapshotApiBase, currentApiBase, StringComparison.OrdinalIgnoreCase))
        {
            error = BuildMismatchError("API base", snapshotApiBase, currentApiBase);
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string BuildMismatchError(string dimension, string expected, string actual)
    {
        return $"Queue context mismatch ({dimension}): snapshot='{FormatToken(expected)}' current='{FormatToken(actual)}'.";
    }

    private static string NormalizeToken(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string NormalizeUrl(string? value)
    {
        var normalized = NormalizeToken(value);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return normalized.TrimEnd('/');
    }

    private static string FormatToken(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }
}
