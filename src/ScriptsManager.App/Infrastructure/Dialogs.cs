using System.Windows;
using System.Windows.Controls;
using ScriptsManager.App.ViewModels;
using ScriptsManager.Core.Connections;

namespace ScriptsManager.App.Infrastructure;

public static class Dialogs
{
    private const string Caption = "Scripts Manager";

    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    public static bool Confirm(string message) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            : MessageBox.Show(message, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void Info(string message)
    {
        if (Owner is { } owner)
            MessageBox.Show(owner, message, Caption, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(message, Caption, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Значение ячейки целиком: можно выделить и скопировать.</summary>
    public static void ShowText(string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(6),
        };
        var window = new Window
        {
            Title = title,
            Content = box,
            Width = 800,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Owner = Owner,
        };
        window.ShowDialog();
    }

    /// <summary>Окно публикации; false — отмена.</summary>
    public static bool Publish(PublishViewModel viewModel) =>
        new PublishWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    /// <summary>Окно подключения; null — отмена.</summary>
    public static ConnectionChoice? EditConnection(ConnectionDialogViewModel viewModel)
    {
        var window = new ConnectionDialog(viewModel) { Owner = Owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    /// <summary>Запрашивает строку; null — отмена.</summary>
    public static string? AskText(string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(3, 2, 3, 2) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Owner = Owner,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true ? box.Text : null;
    }
}
