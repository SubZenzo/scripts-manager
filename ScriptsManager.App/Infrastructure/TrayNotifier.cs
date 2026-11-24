using System.Windows.Forms;

namespace ScriptsManager.App.Infrastructure;

/// <summary>
/// Уведомление Windows (в Windows 10 — всплывающее в углу экрана и в центре уведомлений), пока окно неактивно.
/// Значок в трее появляется с первым уведомлением и убирается при закрытии приложения.
/// </summary>
public sealed class TrayNotifier : IDisposable
{
    private NotifyIcon? _icon;
    private Action? _clicked;

    public void Show(string title, string text, bool error, Action clicked)
    {
        _clicked = clicked;
        if (_icon is null)
        {
            _icon = new NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                Text = " Scripts Manager",
            };
            _icon.BalloonTipClicked += (_, _) => _clicked?.Invoke();
            _icon.MouseClick += (_, _) => _clicked?.Invoke();
        }

        _icon.Visible = true;
        _icon.ShowBalloonTip(10_000, title, text, error ? ToolTipIcon.Error : ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_icon is null)
            return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
