using System.Windows;
using System.Windows.Controls;

namespace NetDocsImporter.App.Views.Steps;

public partial class RecentJobsStepView : System.Windows.Controls.UserControl
{
    public RecentJobsStepView()
    {
        InitializeComponent();
    }

    public void OnLoadRecentJobs(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnLoadRecentJobs(sender, e);
        }
    }

    public void OnExportDirectUploadLog(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnExportDirectUploadLog(sender, e);
        }
    }
}
