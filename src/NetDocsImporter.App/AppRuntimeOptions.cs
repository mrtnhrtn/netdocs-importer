using System;
using System.Linq;

namespace NetDocsImporter.App;

/// <summary>
/// Represents application startup flags that influence runtime behavior.
/// </summary>
public sealed class AppRuntimeOptions
{
    /// <summary>
    /// Gets a value indicating whether developer-only features should be enabled.
    /// </summary>
    public bool IsDeveloperMode { get; init; }

    /// <summary>
    /// Parses command-line arguments into runtime options.
    /// </summary>
    /// <param name="args">Raw command-line arguments.</param>
    /// <returns>Resolved runtime options.</returns>
    public static AppRuntimeOptions FromArgs(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return new AppRuntimeOptions();
        }

        var isDev = args.Any(a =>
            string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/dev", StringComparison.OrdinalIgnoreCase));

        return new AppRuntimeOptions
        {
            IsDeveloperMode = isDev
        };
    }
}
