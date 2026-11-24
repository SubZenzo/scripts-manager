using System.Windows;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.App.ViewModels;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.FitOnStartup(this);
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public AppSettings? Result { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Validate() is { } error)
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = _viewModel.ToSettings();
        DialogResult = true;
    }
}
