namespace NetDocsImporter.App.Views.Steps;

public partial class RecentJobsStepView : System.Windows.Controls.UserControl
{
    public RecentJobsStepView()
    {
        InitializeComponent();
    }

    public void OnDismissQueueNotice(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ClearQueueStartupNotice();
        }
    }
}
