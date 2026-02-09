using System;
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

    public void OnBrowseNdImportExecutable(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnBrowseNdImportExecutable(sender, e);
        }
    }

    public void OnNdImportPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not PasswordBox passwordBox)
        {
            return;
        }

        if (!string.Equals(viewModel.NdImportPassword, passwordBox.Password, StringComparison.Ordinal))
        {
            viewModel.NdImportPassword = passwordBox.Password;
        }
    }

    public void OnNdImportPasswordLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not PasswordBox passwordBox)
        {
            return;
        }

        if (!string.Equals(passwordBox.Password, viewModel.NdImportPassword, StringComparison.Ordinal))
        {
            passwordBox.Password = viewModel.NdImportPassword;
        }
    }
}
