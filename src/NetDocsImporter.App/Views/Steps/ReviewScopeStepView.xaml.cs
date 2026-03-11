using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace NetDocsImporter.App.Views.Steps;

public partial class ReviewScopeStepView : System.Windows.Controls.UserControl
{
    public ReviewScopeStepView()
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

    public void OnRefreshDirectUploadPlan(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnRefreshDirectUploadPlan(sender, e);
        }
    }

    public void OnRunDirectUpload(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnRunDirectUpload(sender, e);
        }
    }

    public void OnAddDirectUploadToQueue(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnAddDirectUploadToQueue(sender, e);
        }
    }

    public void OnScheduleDirectUpload(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnScheduleDirectUpload(sender, e);
        }
    }

    public void OnCancelDirectUpload(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnCancelDirectUpload(sender, e);
        }
    }

    public void OnOpenLastDirectUploadReport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnOpenLastDirectUploadReport(sender, e);
        }
    }

    public void OnBrowseExportDestination(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnBrowseExportDestination(sender, e);
        }
    }

    public void OnRefreshExportPreflight(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnRefreshExportPreflight(sender, e);
        }
    }

    public void OnRunExport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnRunExport(sender, e);
        }
    }

    public void OnCancelExport(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnCancelExport(sender, e);
        }
    }

    public void OnOpenLastExportManifest(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnOpenLastExportManifest(sender, e);
        }
    }

    public void OnFolderTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFolderTreeSelectedItemChanged(sender, e);
        }
    }

    public void OnFolderTreeRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFolderTreeRightButtonDown(sender, e);
        }
    }

    public void OnFolderTreeExpanded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFolderTreeExpanded(sender, e);
        }
    }

    public void OnFolderTreeCollapsed(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFolderTreeCollapsed(sender, e);
        }
    }

    public void OnSetImportInclude(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSetImportInclude(sender, e);
        }
    }

    public void OnSetImportExclude(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSetImportExclude(sender, e);
        }
    }

    public void OnSetImportInherit(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSetImportInherit(sender, e);
        }
    }

    public void OnApplyImportToChildren(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnApplyImportToChildren(sender, e);
        }
    }

    public void OnFilterAll(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFilterAll(sender, e);
        }
    }

    public void OnFilterIncluded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFilterIncluded(sender, e);
        }
    }

    public void OnFilterExcluded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFilterExcluded(sender, e);
        }
    }

    public void OnFilterOverrides(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFilterOverrides(sender, e);
        }
    }

    public void OnFilterLarge(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnFilterLarge(sender, e);
        }
    }

    public void OnResyncAttributes(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnResyncAttributesForReviewScope(sender, e);
        }
    }
}
