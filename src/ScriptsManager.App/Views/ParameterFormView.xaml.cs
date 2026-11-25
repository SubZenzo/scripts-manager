using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ScriptsManager.App.ViewModels;

namespace ScriptsManager.App.Views;

public partial class ParameterFormView : UserControl
{
    public ParameterFormView() => InitializeComponent();

    // Calendar после клика оставляет у себя захват мыши, и следующий клик вне попапа теряется.
    private void Calendar_PreviewMouseUp(object sender, MouseButtonEventArgs e) => ReleaseCapture((Calendar)sender);

    private void CalendarPopup_Closed(object sender, EventArgs e)
    {
        if (((Popup)sender).Child is Calendar calendar)
            ReleaseCapture(calendar);
    }

    private static void ReleaseCapture(Calendar calendar)
    {
        if (Mouse.Captured is DependencyObject captured && (captured == calendar || calendar.IsAncestorOf(captured)))
            Mouse.Capture(null);
    }

    private void Databases_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionSlotViewModel slot })
            slot.LoadDatabasesCommand.Execute(null);
    }

    // В длинном списке сразу можно печатать поиск.
    private void DropPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: Border { Child: DockPanel panel } } && panel.Children[0] is Grid { Children: [TextBox filter, ..] })
            Dispatcher.BeginInvoke(() => { if (filter.IsVisible) filter.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // Открываем календарь на месяце выбранной даты, а не на текущем.
    private void Calendar_Loaded(object sender, RoutedEventArgs e)
    {
        var calendar = (Calendar)sender;
        if (calendar.SelectedDate is { } date)
            calendar.DisplayDate = date;
    }
}
