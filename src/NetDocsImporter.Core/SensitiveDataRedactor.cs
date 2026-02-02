using System.Text.RegularExpressions;

namespace NetDocsImporter.Core;

public static class SensitiveDataRedactor
{
    private static readonly Regex BearerTokenRegex = new(
        @"Bearer\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string RedactBearerTokens(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return BearerTokenRegex.Replace(input, "Bearer [REDACTED]");
    }
}
