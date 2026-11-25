using System.Windows;
using System.Windows.Controls;
using ScriptsManager.App.ViewModels;

namespace ScriptsManager.App.Infrastructure;

/// <summary>
/// Показывает лог запуска в TextBox, дописывая новые строки без перерисовки всего текста.
/// Подписка живёт, пока TextBox на экране: при переключении вкладок текст синхронизируется заново.
/// </summary>
public static class LogBox
{
    public static readonly DependencyProperty RunProperty = DependencyProperty.RegisterAttached(
        "Run", typeof(RunViewModel), typeof(LogBox), new PropertyMetadata(null, OnRunChanged));

    private static readonly DependencyProperty SubscriptionProperty = DependencyProperty.RegisterAttached(
        "Subscription", typeof(Subscription), typeof(LogBox));

    public static RunViewModel? GetRun(DependencyObject d) => (RunViewModel?)d.GetValue(RunProperty);
    public static void SetRun(DependencyObject d, RunViewModel? value) => d.SetValue(RunProperty, value);

    private static void OnRunChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (TextBox)d;
        if (box.GetValue(SubscriptionProperty) is not Subscription subscription)
        {
            subscription = new Subscription(box);
            box.SetValue(SubscriptionProperty, subscription);
            box.Loaded += (_, _) => subscription.Attach();
            box.Unloaded += (_, _) => subscription.Detach();
        }

        subscription.Detach();
        subscription.Run = e.NewValue as RunViewModel;
        if (box.IsLoaded)
            subscription.Attach();
    }

    private sealed class Subscription(TextBox box)
    {
        private bool _attached;

        public RunViewModel? Run { get; set; }

        public void Attach()
        {
            if (_attached || Run is null)
                return;
            box.Text = Run.LogText;
            box.ScrollToEnd();
            Run.LogAppended += Append;
            _attached = true;
        }

        public void Detach()
        {
            if (_attached && Run is not null)
                Run.LogAppended -= Append;
            _attached = false;
        }

        private void Append(string text)
        {
            box.AppendText(text);
            box.ScrollToEnd();
        }
    }
}
