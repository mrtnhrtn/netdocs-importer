using System.Windows;
using System.Windows.Controls;

namespace NetDocsImporter.App.Views.Steps;

public partial class SelectFolderStepView : System.Windows.Controls.UserControl
{
    public SelectFolderStepView()
    {
        InitializeComponent();
    }

    public void OnSelectFolder(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSelectFolder(sender, e);
        }
    }

    public void OnCancelScan(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnCancelScan(sender, e);
        }
    }
}
