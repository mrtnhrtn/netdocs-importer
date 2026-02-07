using System.Windows;
using System.Windows.Controls;
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

    public async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadRecentJobsAsync();
        await _viewModel.LoadNdImportSettingsAsync();
    }

    public async void OnSelectFolder(object sender, RoutedEventArgs e)
    {
        await _viewModel.SelectFolderAndScanAsync();
    }

    public void OnCancelScan(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelScan();
    }

    public async void OnLoadRecentJobs(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadRecentJobsAsync();
    }

    public async void OnStartImport(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartImportAsync();
    }

    public void OnPauseImport(object sender, RoutedEventArgs e)
    {
        _viewModel.PauseImport();
    }

    public void OnResumeImport(object sender, RoutedEventArgs e)
    {
        _viewModel.ResumeImport();
    }

    public void OnCancelImport(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelImport();
    }

    public async void OnFolderTreeExpanded(object sender, RoutedEventArgs e)
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

    public void OnFolderTreeCollapsed(object sender, RoutedEventArgs e)
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

    public void OnFolderTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node)
        {
            _viewModel.SelectFolderNode(node);
        }
    }

    public void OnFolderTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    public async void OnSetImportInherit(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedFolderImportModeAsync("inherit");
    }

    public async void OnSetImportInclude(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedFolderImportModeAsync("include");
    }

    public async void OnSetImportExclude(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedFolderImportModeAsync("exclude");
    }

    public async void OnApplyImportToChildren(object sender, RoutedEventArgs e)
    {
        await _viewModel.ApplyImportModeToChildrenAsync();
    }

    public async void OnProfileInheritChecked(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedProfileModeAsync("inherit");
    }

    public async void OnProfileOverrideChecked(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedProfileModeAsync("override");
    }

    public void OnAddProfileField(object sender, RoutedEventArgs e)
    {
        _viewModel.AddProfileField();
    }

    public void OnRemoveProfileField(object sender, RoutedEventArgs e)
    {
        _viewModel.RemoveSelectedProfileField(_viewModel.SelectedProfileField);
    }

    public async void OnApplyProfileToChildren(object sender, RoutedEventArgs e)
    {
        await _viewModel.ApplyProfileToChildrenAsync();
    }

    public async void OnLoadSchema(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Schema files (*.json;*.csv)|*.json;*.csv|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.LoadSchemaAsync(dialog.FileName);
    }

    public void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenLogsFolder();
    }

    public void OnOpenReports(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenReportsFolder();
    }

    public void OnFilterAll(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "All";
    }

    public void OnFilterIncluded(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "Included";
    }

    public void OnFilterExcluded(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "Excluded";
    }

    public void OnFilterOverrides(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "Overrides";
    }

    public void OnFilterLarge(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFileFilter = "Large";
    }

    public async void OnExportNdImport(object sender, RoutedEventArgs e)
    {
        await _viewModel.ExportNdImportListAsync();
    }

    public async void OnLaunchNdImport(object sender, RoutedEventArgs e)
    {
        await _viewModel.LaunchNdImportAsync();
    }

    public void OnNetDocumentsClientSecretPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.NetDocumentsClientSecret = passwordBox.Password;
        }
    }

    public async void OnConnectToNetDocuments(object sender, RoutedEventArgs e)
    {
        await _viewModel.ConnectToNetDocumentsAsync();
    }

}
