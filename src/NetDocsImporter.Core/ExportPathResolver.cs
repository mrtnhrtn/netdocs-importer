using System.Security.Cryptography;
using System.Text;

namespace NetDocsImporter.Core;

public sealed class ExportPathResolver
{
    private const int DefaultMaxRelativePathLength = 220;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly int _maxRelativePathLength;

    public ExportPathResolver(int maxRelativePathLength = DefaultMaxRelativePathLength)
    {
        _maxRelativePathLength = Math.Max(80, maxRelativePathLength);
    }

    public string ResolveRelativePath(IReadOnlyList<string> sourceSegments, string fileName, string stableId, string? fileExtension = null)
    {
        var safeSegments = sourceSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Select(SanitizePathSegment)
            .Where(segment => segment.Length > 0)
            .ToList();

        var safeFileName = EnsureFileNameExtension(SanitizeFileName(fileName), fileExtension);
        if (safeFileName.Length == 0)
        {
            safeFileName = "document";
        }

        var candidate = BuildRelativePath(safeSegments, safeFileName);
        if (candidate.Length <= _maxRelativePathLength)
        {
            return candidate;
        }

        var existingExtension = Path.GetExtension(safeFileName);
        var stem = Path.GetFileNameWithoutExtension(safeFileName);
        var hashSuffix = "-" + ComputeStableHash(stableId);
        var maxStemLength = Math.Max(8, 60 - hashSuffix.Length);
        if (stem.Length > maxStemLength)
        {
            stem = stem[..maxStemLength];
        }

        safeFileName = $"{stem}{hashSuffix}{existingExtension}";

        while (safeSegments.Count > 0)
        {
            candidate = BuildRelativePath(safeSegments, safeFileName);
            if (candidate.Length <= _maxRelativePathLength)
            {
                return candidate;
            }

            safeSegments.RemoveAt(0);
        }

        return safeFileName.Length <= _maxRelativePathLength
            ? safeFileName
            : safeFileName[.._maxRelativePathLength];
    }

    public string ResolveCollision(string relativePath, string stableId)
    {
        var directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
        var extension = Path.GetExtension(relativePath);
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        var suffix = "-" + ComputeStableHash(stableId);
        var resolvedFileName = $"{stem}{suffix}{extension}";
        return string.IsNullOrWhiteSpace(directory)
            ? resolvedFileName
            : $"{directory}/{resolvedFileName}";
    }

    public string ResolveRelativeDirectoryPath(IReadOnlyList<string> sourceSegments)
    {
        var safeSegments = sourceSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Select(SanitizePathSegment)
            .Where(segment => segment.Length > 0)
            .ToList();

        return safeSegments.Count == 0
            ? string.Empty
            : string.Join('/', safeSegments);
    }

    private static string BuildRelativePath(IReadOnlyList<string> segments, string fileName)
    {
        if (segments.Count == 0)
        {
            return fileName;
        }

        return string.Join('/', segments) + "/" + fileName;
    }

    private static string SanitizeFileName(string value)
    {
        return SanitizePathSegment(value);
    }

    private static string EnsureFileNameExtension(string fileName, string? extension)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        if (!string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            return fileName;
        }

        var normalizedExtension = NormalizeExtension(extension);
        return string.IsNullOrWhiteSpace(normalizedExtension)
            ? fileName
            : $"{fileName}.{normalizedExtension}";
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var normalized = extension.Trim().TrimStart('.');
        return normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? string.Empty
            : normalized;
    }

    private static string SanitizePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (ch < 32 || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var normalized = builder.ToString().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "_";
        }

        return ReservedNames.Contains(normalized) ? $"_{normalized}" : normalized;
    }

    private static string ComputeStableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes[..6]).ToLowerInvariant();
    }
}
