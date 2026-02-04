using System.Windows;
using System.Windows.Controls;

namespace NetDocsImporter.App.Views.Steps;

public partial class RunImportStepView : System.Windows.Controls.UserControl
{
    public RunImportStepView()
    {
        InitializeComponent();
    }

    public void OnStartImport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnStartImport(sender, e);
        }
    }

    public void OnPauseImport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnPauseImport(sender, e);
        }
    }

    public void OnResumeImport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnResumeImport(sender, e);
        }
    }

    public void OnCancelImport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnCancelImport(sender, e);
        }
    }

    public void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnOpenLogs(sender, e);
        }
    }

    public void OnOpenReports(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnOpenReports(sender, e);
        }
    }
}
