using System.Windows;
using System.Windows.Controls;
using ScriptsManager.App.ViewModels;

namespace ScriptsManager.App.Infrastructure;

public sealed class FieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? MultilineText { get; set; }
    public DataTemplate? Bool { get; set; }
    public DataTemplate? Date { get; set; }
    public DataTemplate? SingleList { get; set; }
    public DataTemplate? EditableList { get; set; }
    public DataTemplate? MultiList { get; set; }
    public DataTemplate? Range { get; set; }
    public DataTemplate? Path { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as ParameterFieldViewModel)?.Kind switch
        {
            FieldKind.MultilineText => MultilineText,
            FieldKind.Bool => Bool,
            FieldKind.Date => Date,
            FieldKind.SingleList => SingleList,
            FieldKind.EditableList => EditableList,
            FieldKind.MultiList => MultiList,
            FieldKind.Range => Range,
            FieldKind.Folder or FieldKind.File => Path,
            _ => Text,
        };
}
