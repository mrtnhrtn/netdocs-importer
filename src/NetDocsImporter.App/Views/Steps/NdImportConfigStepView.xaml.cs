using System.Windows;
using System.Windows.Controls;

namespace NetDocsImporter.App.Views.Steps;

public partial class NdImportConfigStepView : System.Windows.Controls.UserControl
{
    public NdImportConfigStepView()
    {
        InitializeComponent();
    }

    public void OnExportNdImport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnExportNdImport(sender, e);
        }
    }

    public void OnLaunchNdImport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnLaunchNdImport(sender, e);
        }
    }
}
