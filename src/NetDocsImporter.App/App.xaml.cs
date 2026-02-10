using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NetDocsImporter.Core;
using Serilog;

namespace NetDocsImporter.App;

public partial class App : System.Windows.Application
{
    private string? _logFilePath;

    protected override void OnStartup(StartupEventArgs e)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        var runtimeOptions = AppRuntimeOptions.FromArgs(e.Args);
        base.OnStartup(e);

        ConfigureLogging();
        HookGlobalExceptionHandlers();

        var mainWindow = new MainWindow(runtimeOptions);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private void ConfigureLogging()
    {
        var logsDirectory = LogPathHelper.GetLogsDirectory();
        _logFilePath = Path.Combine(logsDirectory, "app.log");
        var tracePath = Path.Combine(logsDirectory, "trace.log");
        PruneTraceLog(tracePath, TimeSpan.FromDays(7));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(_logFilePath, rollingInterval: RollingInterval.Day, shared: true)
            .CreateLogger();

        Trace.Listeners.Clear();
        Trace.Listeners.Add(new TimestampedTextWriterTraceListener(tracePath));
        Trace.AutoFlush = true;
    }

    private static void PruneTraceLog(string tracePath, TimeSpan retention)
    {
        if (!File.Exists(tracePath))
        {
            return;
        }

        var cutoff = DateTime.Now.Subtract(retention);
        var tempPath = $"{tracePath}.prune";
        try
        {
            using var reader = new StreamReader(tracePath);
            using var writer = new StreamWriter(tempPath, false);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length < 19 ||
                    !DateTime.TryParseExact(
                        line[..19],
                        "yyyy-MM-dd HH:mm:ss",
                        null,
                        System.Globalization.DateTimeStyles.None,
                        out var timestamp))
                {
                    writer.WriteLine(line);
                    continue;
                }

                if (timestamp >= cutoff)
                {
                    writer.WriteLine(line);
                }
            }

            writer.Flush();
            File.Copy(tempPath, tracePath, overwrite: true);
            File.Delete(tempPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void HookGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
        var logPath = HandleException(exception, "AppDomain.UnhandledException");
        ShowFriendlyMessage(logPath);

        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = HandleException(e.Exception, "Application.DispatcherUnhandledException");
        ShowFriendlyMessage(logPath);

        if (!IsSafeToContinue(e.Exception))
        {
            e.Handled = false;
            Shutdown();
            return;
        }

        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var logPath = HandleException(e.Exception, "TaskScheduler.UnobservedTaskException");
        ShowFriendlyMessage(logPath);

        e.SetObserved();
        Shutdown();
    }

    private string HandleException(Exception exception, string source)
    {
        var safeDetails = SensitiveDataRedactor.RedactBearerTokens(exception.ToString());

        try
        {
            Log.Error("Unhandled exception ({Source}): {Exception}", source, safeDetails);
            Log.CloseAndFlush();
            return _logFilePath ?? string.Empty;
        }
        catch
        {
            return WriteFallbackCrashFile(source, safeDetails);
        }
    }

    private static bool IsSafeToContinue(Exception exception)
    {
        return exception is OperationCanceledException;
    }

    private string WriteFallbackCrashFile(string source, string details)
    {
        var logsDirectory = LogPathHelper.GetLogsDirectory();
        var fileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var path = Path.Combine(logsDirectory, fileName);

        try
        {
            var content = $"Source: {source}{Environment.NewLine}{details}";
            File.WriteAllText(path, content);
        }
        catch
        {
            return path;
        }

        return path;
    }

    private void ShowFriendlyMessage(string logPath)
    {
        var safePath = string.IsNullOrWhiteSpace(logPath) ? "the logs directory" : logPath;
        var message =
            $"The application encountered an unexpected error and needs to close. A log file has been written to: {safePath}";

        if (Dispatcher.CheckAccess())
        {
            System.Windows.MessageBox.Show(message, "NetDocs Importer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Dispatcher.Invoke(() =>
            System.Windows.MessageBox.Show(message, "NetDocs Importer", MessageBoxButton.OK, MessageBoxImage.Error));
    }
}
