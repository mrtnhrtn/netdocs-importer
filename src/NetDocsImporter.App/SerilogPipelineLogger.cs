using NetDocsImporter.Core;
using Serilog;

namespace NetDocsImporter.App;

public sealed class SerilogPipelineLogger : IPipelineLogger
{
    public void Info(string message, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log.Information("{Message} {@Data}", message, data);
    }

    public void Error(string message, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log.Error("{Message} {@Data}", message, data);
    }
}
