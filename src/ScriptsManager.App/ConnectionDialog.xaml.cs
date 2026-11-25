using System.Windows;
using ScriptsManager.App.ViewModels;
using ScriptsManager.Core.Connections;

namespace ScriptsManager.App;

public partial class ConnectionDialog : Window
{
    private readonly ConnectionDialogViewModel _viewModel;

    public ConnectionDialog(ConnectionDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        PasswordBox.Password = viewModel.Password;
        Loaded += (_, _) => ServerBox.Focus();
        Closed += (_, _) => viewModel.Cancel();
    }

    public ConnectionChoice? Result { get; private set; }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordBox.Password;

    private void Databases_DropDownOpened(object? sender, EventArgs e)
    {
        if (_viewModel.Databases.Count == 0 && !_viewModel.IsBusy)
            _viewModel.LoadDatabasesCommand.Execute(null);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Validate() is { } error)
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Result = _viewModel.Apply();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось сохранить подключение: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }
}
