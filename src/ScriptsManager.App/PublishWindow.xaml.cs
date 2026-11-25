using System.Windows;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.App.ViewModels;

namespace ScriptsManager.App;

public partial class PublishWindow : Window
{
    private readonly PublishViewModel _viewModel;

    public PublishWindow(PublishViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.FitOnStartup(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => MessageBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Validate() is { } error)
        {
            System.Windows.MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
