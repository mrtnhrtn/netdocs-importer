using System.Windows;
using System.Windows.Controls;

namespace NetDocsImporter.App.Views.Steps;

public partial class ProfilingStepView : System.Windows.Controls.UserControl
{
    public ProfilingStepView()
    {
        InitializeComponent();
    }

    public void OnProfileInheritChecked(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnProfileInheritChecked(sender, e);
        }
    }

    public void OnProfileOverrideChecked(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnProfileOverrideChecked(sender, e);
        }
    }

    public void OnAddProfileField(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnAddProfileField(sender, e);
        }
    }

    public void OnRemoveProfileField(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnRemoveProfileField(sender, e);
        }
    }

    public void OnApplyProfileToChildren(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnApplyProfileToChildren(sender, e);
        }
    }

    public void OnLoadSchema(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.OnLoadSchema(sender, e);
        }
    }

    public void OnNetDocumentsClientSecretLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && DataContext is MainViewModel viewModel)
        {
            if (passwordBox.Password != viewModel.NetDocumentsClientSecret)
            {
                passwordBox.Password = viewModel.NetDocumentsClientSecret;
            }
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
}
