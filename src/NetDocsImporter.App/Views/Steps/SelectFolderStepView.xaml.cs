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

    public void OnConnectToNetDocuments(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnConnectToNetDocuments(sender, e);
        }
    }

    public void OnSyncNetDocumentsCabinets(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSyncNetDocumentsCabinets(sender, e);
        }
    }

    public void OnLoadTargetContainers(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnLoadTargetContainers(sender, e);
        }
    }

    public void OnConfirmTargetContainer(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnConfirmTargetContainer(sender, e);
        }
    }

    public void OnRefreshRecentTargets(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnRefreshRecentTargets(sender, e);
        }
    }

    public void OnRefreshFavoriteTargets(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnRefreshFavoriteTargets(sender, e);
        }
    }

    public void OnSearchWorkspaceTargets(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSearchWorkspaceTargets(sender, e);
        }
    }

    public void OnTargetBrowserTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnTargetBrowserTabSelectionChanged(sender, e);
        }
    }

    public void OnUseSelectedWorkspaceTarget(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnUseSelectedWorkspaceTarget(sender, e);
        }
    }

    public void OnWorkspaceSearchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnWorkspaceSearchSelectionChanged(sender, e);
        }
    }

    public void OnSelectTargetFromRecent(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSelectTargetFromRecent(sender, e);
        }
    }

    public void OnSelectTargetFromFavorite(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSelectTargetFromFavorite(sender, e);
        }
    }

    public void OnToggleFavoriteForSelectedTarget(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnToggleFavoriteForSelectedTarget(sender, e);
        }
    }

    public void OnContinueToReviewScope(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnContinueToReviewScope(sender, e);
        }
    }
}
