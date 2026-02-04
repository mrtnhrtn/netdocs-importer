using System.Windows;
using System.Windows.Input;
using NetDocsImporter.Core;

namespace NetDocsImporter.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadRecentJobsAsync();
        await _viewModel.LoadNdImportSettingsAsync();
    }

    private async void OnSelectFolder(object sender, RoutedEventArgs e)
    {
        await _viewModel.SelectFolderAndScanAsync();
    }

    private void OnCancelScan(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelScan();
    }

    private async void OnLoadRecentJobs(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadRecentJobsAsync();
    }

    private async void OnStartImport(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartImportAsync();
    }

    private void OnPauseImport(object sender, RoutedEventArgs e)
    {
        _viewModel.PauseImport();
    }

    private void OnResumeImport(object sender, RoutedEventArgs e)
    {
        _viewModel.ResumeImport();
    }

    private void OnCancelImport(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelImport();
    }

    private async void OnFolderTreeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.TreeViewItem item)
        {
            return;
        }

        if (item.DataContext is FolderNodeViewModel node)
        {
            await _viewModel.ExpandFolderNodeAsync(node);
        }
    }

    private void OnFolderTreeCollapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.TreeViewItem item)
        {
            return;
        }

        if (item.DataContext is FolderNodeViewModel node)
        {
            node.CancelLoading();
        }
    }

    private void OnFolderTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node)
        {
            _viewModel.SelectFolderNode(node);
        }
    }

    private void OnFolderTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private async void OnSetImportInherit(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedFolderImportModeAsync("inherit");
    }

    private async void OnSetImportInclude(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedFolderImportModeAsync("include");
    }

    private async void OnSetImportExclude(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedFolderImportModeAsync("exclude");
    }

    private async void OnApplyImportToChildren(object sender, RoutedEventArgs e)
    {
        await _viewModel.ApplyImportModeToChildrenAsync();
    }

    private async void OnProfileInheritChecked(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedProfileModeAsync("inherit");
    }

    private async void OnProfileOverrideChecked(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedProfileModeAsync("override");
    }

    private void OnAddProfileField(object sender, RoutedEventArgs e)
    {
        _viewModel.AddProfileField();
    }

    private void OnRemoveProfileField(object sender, RoutedEventArgs e)
    {
        _viewModel.RemoveSelectedProfileField(_viewModel.SelectedProfileField);
    }

    private async void OnApplyProfileToChildren(object sender, RoutedEventArgs e)
    {
        await _viewModel.ApplyProfileToChildrenAsync();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenLogsFolder();
    }

    private void OnOpenReports(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenReportsFolder();
    }

    private void OnFilterAll(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "All";
    }

    private void OnFilterIncluded(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "Included";
    }

    private void OnFilterExcluded(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "Excluded";
    }

    private void OnFilterLarge(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "Large";
    }

    private async void OnExportNdImport(object sender, RoutedEventArgs e)
    {
        await _viewModel.ExportNdImportListAsync();
    }

    private async void OnLaunchNdImport(object sender, RoutedEventArgs e)
    {
        await _viewModel.LaunchNdImportAsync();
    }
}
