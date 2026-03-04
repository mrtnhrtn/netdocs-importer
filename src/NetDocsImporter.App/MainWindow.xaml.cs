using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NetDocsImporter.Core;

namespace NetDocsImporter.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _recentJobsRefreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public MainWindow()
        : this(new AppRuntimeOptions())
    {
    }

    public MainWindow(AppRuntimeOptions runtimeOptions)
    {
        _viewModel = new MainViewModel(runtimeOptions);
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _recentJobsRefreshTimer.Tick += OnRecentJobsRefreshTick;
    }

    public async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RecoverInterruptedDirectUploadsAsync();
        }
        catch
        {
            // Recovery is best-effort only.
        }

        await _viewModel.LoadRecentJobsAsync();
        await _viewModel.LoadNdImportSettingsAsync();
        _recentJobsRefreshTimer.Start();
    }

    public void OnClosed(object? sender, EventArgs e)
    {
        _recentJobsRefreshTimer.Stop();
        _recentJobsRefreshTimer.Tick -= OnRecentJobsRefreshTick;
    }

    private async void OnRecentJobsRefreshTick(object? sender, EventArgs e)
    {
        if (_viewModel.CurrentStep?.Key != StepKey.RecentJobs)
        {
            return;
        }

        try
        {
            await _viewModel.LoadRecentJobsAsync();
        }
        catch
        {
            // Best-effort periodic refresh only.
        }
    }

    public void OnToggleSettings(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleSettings();
    }

    public void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenSettings();
    }

    public void OnCloseSettings(object sender, RoutedEventArgs e)
    {
        _viewModel.CloseSettings();
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

    public async void OnExportDirectUploadLog(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"directupload-runlog-{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.ExportLastDirectUploadLogAsync(dialog.FileName);
    }

    public void OnOpenLastDirectUploadReport(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenLastDirectUploadReport();
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

    public async void OnRefreshDirectUploadPlan(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshDirectUploadPreflightAsync(forceRescan: true);
    }

    public async void OnRunDirectUpload(object sender, RoutedEventArgs e)
    {
        await _viewModel.RunDirectUploadAsync();
    }

    public void OnCancelDirectUpload(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelDirectUpload();
    }

    public void OnBrowseNdImportExecutable(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ndimport executable (ndimport.exe)|ndimport.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.NdImportPath = dialog.FileName;
        }
    }

    public async void OnConnectToNetDocuments(object sender, RoutedEventArgs e)
    {
        await _viewModel.ConnectToNetDocumentsAsync();
    }

    public async void OnDisconnectFromNetDocuments(object sender, RoutedEventArgs e)
    {
        await _viewModel.DisconnectFromNetDocumentsAsync();
    }

    public void OnNetDocumentsBootstrapClientSecretPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.NetDocumentsBootstrapClientSecret = passwordBox.Password;
        }
    }

    public void OnNetDocumentsBootstrapClientSecretLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox &&
            passwordBox.Password != _viewModel.NetDocumentsBootstrapClientSecret)
        {
            passwordBox.Password = _viewModel.NetDocumentsBootstrapClientSecret;
        }
    }

    public async void OnSaveNetDocumentsOAuthProfile(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveNetDocumentsOAuthProfileAsync();
    }

    public async void OnSyncNetDocumentsCabinets(object sender, RoutedEventArgs e)
    {
        await _viewModel.SyncNetDocumentsCabinetsAsync();
    }

    public async void OnSyncNetDocumentsAttributes(object sender, RoutedEventArgs e)
    {
        await _viewModel.SyncNetDocumentsAttributesAsync();
    }

    public async void OnViewLookupValues(object sender, RoutedEventArgs e)
    {
        await _viewModel.ViewSelectedLookupValuesAsync();
    }

    public async void OnLoadTargetContainers(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadNetDocumentsTargetContainersAsync();
    }

    public async void OnRefreshRecentTargets(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshRecentTargetsAsync();
    }

    public async void OnRefreshFavoriteTargets(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshFavoriteTargetsAsync();
    }

    public async void OnSearchWorkspaceTargets(object sender, RoutedEventArgs e)
    {
        await _viewModel.SearchWorkspaceTargetsAsync();
    }

    public void OnTargetBrowserTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TabControl tabControl ||
            tabControl.SelectedItem is not TabItem selectedTab)
        {
            return;
        }

        var tab = selectedTab.Header?.ToString() switch
        {
            "Recent" => NdTargetBrowserTab.Recent,
            "Favorites" => NdTargetBrowserTab.Favorites,
            "Go to Workspace" => NdTargetBrowserTab.GoToWorkspace,
            _ => NdTargetBrowserTab.Recent
        };

        _viewModel.SelectedTargetBrowserTab = tab;
    }

    public async void OnUseSelectedWorkspaceTarget(object sender, RoutedEventArgs e)
    {
        await _viewModel.UseSelectedWorkspaceSearchTargetAsync();
    }

    public async void OnWorkspaceSearchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView listView || listView.SelectedItem is null)
        {
            return;
        }

        await _viewModel.UseSelectedWorkspaceSearchTargetAsync();
    }

    public async void OnBrowseNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: NetDocumentsBrowseNodeView node })
        {
            return;
        }

        await _viewModel.ExpandBrowseNodeAsync(node);
    }

    public async void OnBrowseSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not NetDocumentsBrowseNodeView node || node.IsPlaceholder)
        {
            return;
        }

        _viewModel.SelectedBrowseNode = node;
        await _viewModel.SelectTargetFromBrowseNodeAsync();
    }

    public async void OnSelectTargetFromRecent(object sender, RoutedEventArgs e)
    {
        await _viewModel.SelectTargetFromRecentAsync();
    }

    public async void OnSelectTargetFromFavorite(object sender, RoutedEventArgs e)
    {
        await _viewModel.SelectTargetFromFavoriteAsync();
    }

    public async void OnToggleFavoriteForSelectedTarget(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleFavoriteForSelectedTargetAsync();
    }

    public async void OnConfirmTargetContainer(object sender, RoutedEventArgs e)
    {
        await _viewModel.ConfirmNetDocumentsTargetAsync();
    }

    public async void OnContinueToReviewScope(object sender, RoutedEventArgs e)
    {
        await _viewModel.ContinueToReviewScopeAsync();
    }

    public async void OnResyncAttributesForReviewScope(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResyncAttributesForReviewScopeAsync();
    }
}
