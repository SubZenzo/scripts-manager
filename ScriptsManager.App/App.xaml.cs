using System.Windows;
using System.Windows.Threading;

namespace ScriptsManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    // Непредвиденная ошибка не должна закрывать приложение с введёнными параметрами.
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow, e.Exception.ToString(), "Непредвиденная ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
