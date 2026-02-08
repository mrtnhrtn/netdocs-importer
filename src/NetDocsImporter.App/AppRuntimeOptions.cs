using System;
using System.Linq;

namespace NetDocsImporter.App;

public sealed class AppRuntimeOptions
{
    public bool IsDeveloperMode { get; init; }

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
