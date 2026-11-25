using System.Windows;
using System.Windows.Interop;

namespace ScriptsManager.App.Infrastructure;

/// <summary>
/// Подгонка окна под рабочую область экрана: на маленьких экранах и при большом масштабе DPI
/// заданные в XAML 1200×750 не помещаются целиком, из-за чего нижняя часть окна уходит за экран.
/// </summary>
internal static class WindowFit
{
    /// <summary>
    /// Подписывает подгонку на открытие окна (нужен хэндл, поэтому в конструкторе его ещё нет).
    /// Вызывать сразу после InitializeComponent.
    /// </summary>
    public static void FitOnStartup(Window window) =>
        window.SourceInitialized += (_, _) => ClampToScreen(window);

    /// <summary>
    /// Ужимает окно до рабочей области монитора (никогда не увеличивает), а для центрирования
    /// по экрану центрирует его в этой области, чтобы окно не перекрывала панель задач.
    /// </summary>
    public static void ClampToScreen(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
            return;

        // Монитор берём по «якорю»: для дочернего окна — по владельцу (WPF центрирует его
        // именно там), для главного — по самому окну (оно ещё не показано и открылось на
        // основном мониторе). Win32 возвращает физические пиксели, TransformFromDevice
        // переводит их в единицы WDF (DIP), в которых заданы Width/Height окна.
        var anchor = window.Owner is null ? hwnd : new WindowInteropHelper(window.Owner).Handle;
        var work = System.Windows.Forms.Screen.FromHandle(anchor).WorkingArea;

        var toDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = toDevice.Transform(new Point(work.Left, work.Top));
        var size = toDevice.Transform(new Point(work.Width, work.Height));
        if (size.X <= 0 || size.Y <= 0)
            return;

        if (!double.IsNaN(window.Width))
            window.Width = Math.Min(window.Width, size.X);
        if (!double.IsNaN(window.Height))
            window.Height = Math.Min(window.Height, size.Y);

        // Положение задаём сами: WindowStartupLocation.CenterScreen/WPF мог бы поставить окно
        // уже после этой обработки и по своим расчётам.
        if (window.WindowStartupLocation == WindowStartupLocation.CenterScreen)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = topLeft.X + (size.X - window.ActualWidthOrWidth()) / 2;
            window.Top = topLeft.Y + (size.Y - window.ActualHeightOrHeight()) / 2;
        }
    }

    private static double ActualWidthOrWidth(this Window w) =>
        double.IsNaN(w.Width) ? w.ActualWidth : w.Width;

    private static double ActualHeightOrHeight(this Window w) =>
        double.IsNaN(w.Height) ? w.ActualHeight : w.Height;
}
