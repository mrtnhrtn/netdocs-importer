namespace NetDocsImporter.Core;

public static class LogPathHelper
{
    public static string GetLogsDirectory()
    {
        return new AppPaths().LogsDirectory;
    }
}
