using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace ScriptsManager.App.Infrastructure;

/// <summary>Привязка текста и подсветки к AvalonEdit (у <see cref="TextEditor.Text"/> нет привязки).</summary>
public static class CodeViewer
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(CodeViewer),
        new PropertyMetadata(null, (d, e) => ((TextEditor)d).Text = (string?)e.NewValue ?? ""));

    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.RegisterAttached(
        "Syntax", typeof(string), typeof(CodeViewer),
        new PropertyMetadata(null, (d, e) => ((TextEditor)d).SyntaxHighlighting =
            e.NewValue is string name ? HighlightingManager.Instance.GetDefinition(name) : null));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);
    public static string? GetSyntax(DependencyObject d) => (string?)d.GetValue(SyntaxProperty);
    public static void SetSyntax(DependencyObject d, string? value) => d.SetValue(SyntaxProperty, value);
}
