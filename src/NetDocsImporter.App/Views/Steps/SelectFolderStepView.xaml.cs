using System.Windows;

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

    public void OnNetDocumentsClientSecretLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnNetDocumentsClientSecretLoaded(sender, e);
        }
    }

    public void OnNetDocumentsClientSecretPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnNetDocumentsClientSecretPasswordChanged(sender, e);
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

    public void OnSearchWorkspaces(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSearchWorkspaces(sender, e);
        }
    }

    public void OnLoadSelectedWorkspace(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnLoadSelectedWorkspace(sender, e);
        }
    }

    public void OnSelectWorkspaceAsTarget(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSelectWorkspaceAsTarget(sender, e);
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

    public void OnRefreshBrowseTree(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnRefreshBrowseTree(sender, e);
        }
    }

    public void OnBrowseTreeExpanded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnBrowseTreeExpanded(sender, e);
        }
    }

    public void OnBrowseTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnBrowseTreeSelectedItemChanged(sender, e);
        }
    }

    public void OnSelectTargetFromBrowseNode(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnSelectTargetFromBrowseNode(sender, e);
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
