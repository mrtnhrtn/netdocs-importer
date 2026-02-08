using System.Diagnostics;

namespace NetDocsImporter.App;

public sealed class TimestampedTextWriterTraceListener : TextWriterTraceListener
{
    public TimestampedTextWriterTraceListener(string fileName) : base(fileName)
    {
    }

    public override void WriteLine(string? message)
    {
        base.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
    }
}
