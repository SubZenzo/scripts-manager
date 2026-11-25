using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Markdig;
using Markdig.Wpf;

namespace ScriptsManager.App.Infrastructure;

/// <summary>Показ подробного описания скрипта (markdown) в <see cref="MarkdownViewer"/>.</summary>
public static class MarkdownView
{
    /// <summary>Разбор с таблицами, списками задач и т.п.</summary>
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder().UseSupportedExtensions().Build();

    /// <summary>Ссылки из описания — в браузере; только веб-адреса и почта, чтобы ссылка не запустила файл.</summary>
    public static void RegisterLinks() =>
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(Commands.Hyperlink, (_, e) =>
        {
            if (Uri.TryCreate(e.Parameter as string, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }));
}
