using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.Core.Runs;

namespace ScriptsManager.App.ViewModels;

/// <summary>Окно с таблицей результата в одном из форматов «Копировать как».</summary>
public sealed partial class TableTextViewModel : ObservableObject
{
    /// <summary>Формат, в котором копировали или смотрели в последний раз.</summary>
    public static TableFormat LastFormat { get; set; } = TableExport.Formats[0];

    private readonly TableSlice _all;
    private readonly TableSlice? _selection;

    /// <param name="selection">Выделенные строки и столбцы; null — выделена одна ячейка или ничего.</param>
    public TableTextViewModel(string title, TableSlice all, TableSlice? selection)
    {
        Title = title;
        _all = all;
        _selection = selection;
        _onlySelection = selection is not null;
        _format = LastFormat;
    }

    public string Title { get; }
    public IReadOnlyList<TableFormat> Formats => TableExport.Formats;
    public bool HasSelection => _selection is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private TableFormat _format;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private bool _onlySelection;

    public string Text => Format.Render(OnlySelection && _selection is not null ? _selection : _all);

    partial void OnFormatChanged(TableFormat value) => LastFormat = value;

    [RelayCommand]
    private void Copy() => ClipboardText.Set(Text);
}
