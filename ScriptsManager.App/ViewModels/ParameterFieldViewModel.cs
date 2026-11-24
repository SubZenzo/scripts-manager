using System.Globalization;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.Core.Manifests;
using Microsoft.Win32;

namespace ScriptsManager.App.ViewModels;

public enum FieldKind { Text, MultilineText, Bool, Date, SingleList, EditableList, MultiList, Folder, File, Range }

public sealed partial class ListItemViewModel(ListItem item) : ObservableObject
{
    public string Key { get; } = item.Key;
    public string Value { get; } = item.Value;

    [ObservableProperty]
    private bool _isChecked;

    public override string ToString() => Value;
}

/// <summary>Поле формы параметров. Значения хранятся строками, как в манифесте и пресетах.</summary>
public sealed partial class ParameterFieldViewModel : ObservableObject
{
    // Пока поле не меняли и не нажимали «Запустить», ошибку «обязательный параметр» не показываем.
    private bool _touched;
    private bool _loading;

    public ParameterFieldViewModel(ScriptParameter parameter)
    {
        Parameter = parameter;
        Kind = parameter.Type switch
        {
            ParameterType.String => parameter.Multiline ? FieldKind.MultilineText : FieldKind.Text,
            ParameterType.Text => parameter.Multiline ? FieldKind.MultilineText : FieldKind.Text,
            ParameterType.Bool => FieldKind.Bool,
            ParameterType.Date or ParameterType.DateTime => FieldKind.Date,
            ParameterType.List when parameter.Multi => FieldKind.MultiList,
            ParameterType.List when parameter.Editable => FieldKind.EditableList,
            ParameterType.List => FieldKind.SingleList,
            ParameterType.Folder => FieldKind.Folder,
            ParameterType.File => FieldKind.File,
            ParameterType.Range => FieldKind.Range,
            _ => FieldKind.Text,
        };

        var items = parameter.Items.Select(i => new ListItemViewModel(i)).ToList();
        if (Kind == FieldKind.SingleList && !parameter.Required)
            items.Insert(0, new ListItemViewModel(new ListItem("", "(не задано)")));
        foreach (var item in items)
            item.PropertyChanged += (_, _) => Changed();
        Items = items;
        FilteredItems = new ListCollectionView(items)
        {
            Filter = i => ItemFilter.Trim() is not { Length: > 0 } filter
                || ((ListItemViewModel)i).Value.Contains(filter, StringComparison.CurrentCultureIgnoreCase),
        };

        SetValues(parameter.Default);
    }

    /// <summary>Элементы списка с multi, отфильтрованные поиском в выпадающей панели.</summary>
    public ListCollectionView FilteredItems { get; }

    /// <summary>Поиск по длинному списку; короткий и так виден целиком.</summary>
    public bool ShowItemFilter => Items.Count > 8;

    [ObservableProperty]
    private string _itemFilter = "";

    partial void OnItemFilterChanged(string value) => FilteredItems.Refresh();

    [ObservableProperty]
    private bool _isDropDownOpen;

    partial void OnIsDropDownOpenChanged(bool value)
    {
        if (value)
            ItemFilter = "";
    }

    /// <summary>Выбранные значения списка с multi одной строкой.</summary>
    public string Summary => Items.Where(i => i.IsChecked).Select(i => i.Value).ToList() switch
    {
        [] => "",
        var all when all.Count == Items.Count && all.Count > 1 => $"все ({all.Count})",
        var all => string.Join(", ", all),
    };

    public ScriptParameter Parameter { get; }
    public FieldKind Kind { get; }
    public string Name => Parameter.Name;
    public string Label => Parameter.Label;
    public bool Required => Parameter.Required;
    public string? Help => Parameter.Help;
    public bool HasTime => Parameter.Type == ParameterType.DateTime;
    public IReadOnlyList<ListItemViewModel> Items { get; }

    public string? Placeholder => Parameter.Type switch
    {
        ParameterType.Date => "yyyy-MM-dd или today-7",
        ParameterType.DateTime => "yyyy-MM-dd HH:mm или now-1h",
        ParameterType.Int => "целое число",
        ParameterType.Decimal => "число",
        ParameterType.Guid => "00000000-0000-0000-0000-000000000000",
        _ => null,
    };

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool? _checked;

    [ObservableProperty]
    private ListItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isCalendarOpen;

    [ObservableProperty]
    private string? _error;

    /// <summary>Вычисленное значение выражения даты (today-7 → дата).</summary>
    [ObservableProperty]
    private string? _hint;

    partial void OnTextChanged(string value) => Changed();
    partial void OnCheckedChanged(bool? value) => Changed();
    partial void OnSelectedItemChanged(ListItemViewModel? value) => Changed();

    /// <summary>Дата для календаря; выбор в календаре сохраняет время из поля.</summary>
    public DateTime? CalendarDate
    {
        get => ParameterValues.TryEvaluateDate(Text, DateTime.Now, out var d) ? d.Date : null;
        set
        {
            if (value is not { } date)
                return;
            var time = HasTime && ParameterValues.TryEvaluateDate(Text, DateTime.Now, out var current) ? current.TimeOfDay : TimeSpan.Zero;
            Text = ParameterValues.FormatDate(date.Date + time, HasTime);
            IsCalendarOpen = false;
        }
    }

    public IReadOnlyList<string> GetValues() => Kind switch
    {
        FieldKind.Bool => Checked is { } b ? [b ? "true" : "false"] : [],
        FieldKind.SingleList => SelectedItem is { } item ? [item.Key] : [],
        FieldKind.EditableList => [Items.FirstOrDefault(i => i.Value == Text)?.Key ?? Text],
        FieldKind.MultiList => Items.Where(i => i.IsChecked).Select(i => i.Key).ToList(),
        _ => [Text],
    };

    public void SetValues(IReadOnlyList<string> values)
    {
        _loading = true;
        var first = values.FirstOrDefault();
        switch (Kind)
        {
            case FieldKind.Bool:
                Checked = first is null ? (Required ? false : null) : bool.TryParse(first, out var b) && b;
                break;
            case FieldKind.SingleList:
                SelectedItem = Items.FirstOrDefault(i => i.Key == (first ?? ""));
                break;
            case FieldKind.EditableList:
                Text = Items.FirstOrDefault(i => i.Key == first)?.Value ?? first ?? "";
                break;
            case FieldKind.MultiList:
                foreach (var item in Items)
                    item.IsChecked = values.Contains(item.Key);
                break;
            default:
                Text = first ?? "";
                break;
        }

        _loading = false;
        _touched = false;
        Changed(byUser: false);
    }

    /// <summary>Проверяет поле и показывает ошибку, даже если поле не меняли.</summary>
    public bool Validate()
    {
        _touched = true;
        Changed(byUser: false);
        return Error is null;
    }

    private void Changed(bool byUser = true)
    {
        if (_loading)
            return;

        _touched |= byUser;
        var values = GetValues();
        var error = ParameterValues.ValidateField(Parameter, values);
        Error = !_touched && values.All(string.IsNullOrWhiteSpace) ? null : error;

        Hint = Kind switch
        {
            FieldKind.Date when error is null && Text.Trim() is [var c, ..] && char.IsLetter(c)
                && ParameterValues.TryEvaluateDate(Text, DateTime.Now, out var date) =>
                "= " + date.ToString(HasTime ? "dd.MM.yyyy HH:mm:ss" : "dd.MM.yyyy", CultureInfo.InvariantCulture),
            FieldKind.Range when error is null && !string.IsNullOrWhiteSpace(Text) => RangeHint(Text),
            _ => null,
        };
        OnPropertyChanged(nameof(CalendarDate));
        OnPropertyChanged(nameof(Summary));
    }

    // «33 номера», повторы — предупреждением: скрипт обойдёт такой номер дважды.
    private static string? RangeHint(string text)
    {
        if (ParameterValues.TryParseRange(text, out var numbers) is not null)
            return null;
        var count = numbers.Count;
        var word = (count % 100) is >= 11 and <= 14 ? "номеров" : (count % 10) switch { 1 => "номер", 2 or 3 or 4 => "номера", _ => "номеров" };
        var repeated = numbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        return $"{count} {word}" + (repeated.Count == 0 ? ""
            : "; повторяются: " + string.Join(", ", repeated.Take(10)) + (repeated.Count > 10 ? $" и ещё {repeated.Count - 10}" : ""));
    }

    public bool IsThreeState => !Required;

    [RelayCommand]
    private void CheckAll(string? value)
    {
        _loading = true;
        // С поиском — только найденные.
        foreach (var item in FilteredItems.Cast<ListItemViewModel>())
            item.IsChecked = value == "true";
        _loading = false;
        Changed();
    }

    [RelayCommand]
    private void Browse()
    {
        if (Kind == FieldKind.Folder)
        {
            var dialog = new OpenFolderDialog { Title = Label };
            if (Directory.Exists(Text))
                dialog.InitialDirectory = Text;
            if (dialog.ShowDialog() == true)
                Text = dialog.FolderName;
        }
        else
        {
            var dialog = new OpenFileDialog { Title = Label, CheckFileExists = false };
            if (Parameter.Filter is { } filter)
                dialog.Filter = filter;
            if (File.Exists(Text))
                dialog.FileName = Text;
            if (dialog.ShowDialog() == true)
                Text = dialog.FileName;
        }
    }
}
