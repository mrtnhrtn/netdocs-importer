namespace NetDocsImporter.Core;

public interface IPipelineLogger
{
    void Info(string message, IReadOnlyDictionary<string, object?>? data = null);

    void Error(string message, IReadOnlyDictionary<string, object?>? data = null);
}

public sealed class NullPipelineLogger : IPipelineLogger
{
    public void Info(string message, IReadOnlyDictionary<string, object?>? data = null)
    {
    }

    public void Error(string message, IReadOnlyDictionary<string, object?>? data = null)
    {
    }
}
