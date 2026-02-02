namespace NetDocsImporter.Core;

public sealed class AppPaths
{
    public AppPaths(string? baseDataDirectory = null, string? executablePath = null)
    {
        BaseDataDirectory = baseDataDirectory ?? ResolveBaseDataDirectory(executablePath);
        LogsDirectory = Path.Combine(BaseDataDirectory, "logs");
        ReportsDirectory = Path.Combine(BaseDataDirectory, "reports");

        EnsureDirectories();
    }

    public string BaseDataDirectory { get; }

    public string LogsDirectory { get; }

    public string ReportsDirectory { get; }

    public string DatabasePath => Path.Combine(BaseDataDirectory, "jobs.db");

    public static string ResolveBaseDataDirectory(string? executablePath = null)
    {
        var path = executablePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.ProcessPath;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var exeDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
            var portableFlag = Path.Combine(exeDirectory, "portable.flag");
            if (File.Exists(portableFlag))
            {
                return Path.GetFullPath(Path.Combine(exeDirectory, "data"));
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Local application data folder is not available.");
        }

        return Path.Combine(localAppData, "NetDocsImporter");
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ReportsDirectory);
    }
}
