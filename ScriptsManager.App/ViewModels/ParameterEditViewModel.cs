using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptsManager.Core.Authoring;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.App.ViewModels;

public sealed record TypeOption(ParameterType Type, string Name);

/// <summary>Параметр в окне добавления/правки скрипта.</summary>
public sealed partial class ParameterEditViewModel : ObservableObject
{
    public static IReadOnlyList<TypeOption> Types { get; } =
    [
        new(ParameterType.String, "Строка"),
        new(ParameterType.Text, "Многострочный текст"),
        new(ParameterType.Int, "Целое число"),
        new(ParameterType.Decimal, "Дробное число"),
        new(ParameterType.Range, "Номера и диапазоны"),
        new(ParameterType.Bool, "Да/нет"),
        new(ParameterType.Date, "Дата"),
        new(ParameterType.DateTime, "Дата и время"),
        new(ParameterType.Guid, "GUID"),
        new(ParameterType.List, "Список"),
        new(ParameterType.Folder, "Путь к папке"),
        new(ParameterType.File, "Путь к файлу"),
    ];

    /// <param name="original">Параметр из манифеста или найденный в скрипте; его поля, которых нет в окне, сохраняются.</param>
    /// <param name="fromManifest">Параметр уже был в манифесте: по <see cref="OriginalName"/> переименовываются значения пресетов.</param>
    public ParameterEditViewModel(ScriptParameter original, bool fromManifest, string? source = null, string? note = null)
    {
        Original = original;
        OriginalName = fromManifest ? original.Name : null;
        Source = source;
        Note = note;
        _include = true;
        _name = original.Name;
        _label = original.Label == original.Name ? "" : original.Label;
        _help = original.Help ?? "";
        _type = original.Type;
        _required = original.Required;
        _defaultText = string.Join("; ", original.Default);
        _min = original.Min?.ToString(CultureInfo.InvariantCulture) ?? "";
        _max = original.Max?.ToString(CultureInfo.InvariantCulture) ?? "";
        _multiline = original.Multiline;
        _multi = original.Multi;
        _editable = original.Editable;
        _itemsText = string.Join(Environment.NewLine, original.Items.Select(i => i.Key == i.Value ? i.Key : $"{i.Key} = {i.Value}"));
        _filter = original.Filter ?? "";
    }

    public ScriptParameter Original { get; }
    public string? OriginalName { get; }

    /// <summary>Откуда найден в скрипте (для T-SQL — объявление, которое уберётся из скрипта).</summary>
    public string? Source { get; set; }

    [ObservableProperty]
    private string? _note;

    /// <summary>Переносить ли в параметры: снятая отметка — переменная остаётся в скрипте как есть.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private bool _include;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string _name;

    /// <summary>Приглашение в форме; пусто — имя.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string _label;

    [ObservableProperty]
    private string _help;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption), nameof(IsNumber), nameof(IsList), nameof(IsFile), nameof(IsString), nameof(DefaultHint))]
    private ParameterType _type;

    [ObservableProperty]
    private bool _required;

    [ObservableProperty]
    private string _defaultText;

    [ObservableProperty]
    private string _min;

    [ObservableProperty]
    private string _max;

    [ObservableProperty]
    private bool _multiline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultHint))]
    private bool _multi;

    [ObservableProperty]
    private bool _editable;

    /// <summary>Элементы списка: по одному в строке, «ключ = подпись» или просто значение.</summary>
    [ObservableProperty]
    private string _itemsText;

    [ObservableProperty]
    private string _filter;

    public bool IsNumber => Type is ParameterType.Int or ParameterType.Decimal or ParameterType.Range;
    public bool IsList => Type == ParameterType.List;
    public bool IsFile => Type == ParameterType.File;
    public bool IsString => Type == ParameterType.String;

    public string Caption =>
        $"{Name} — {Types.First(t => t.Type == Type).Name}" + (Label.Length > 0 && Label != Name ? $" «{Label}»" : "");

    public string DefaultHint => Type switch
    {
        ParameterType.Date or ParameterType.DateTime => "Дата (2026-01-31 или 31.01.2026) или выражение: now, today, today-7d, now-1h",
        ParameterType.Bool => "true или false",
        ParameterType.List when Multi => "Ключи через «;»",
        ParameterType.List => "Ключ элемента списка",
        ParameterType.Range => "Числа и диапазоны через запятую: 1, 5, 2..4, 6..10",
        _ => "",
    };

    partial void OnTypeChanged(ParameterType value)
    {
        if (value == ParameterType.Text)
            Multiline = true;
        if (value == ParameterType.Bool && DefaultText.Length == 0)
            DefaultText = "false";
    }

    /// <summary>Параметр для манифеста; ошибка — текстом.</summary>
    public (ScriptParameter? Parameter, string? Error) Build()
    {
        var name = Name.Trim();
        if (!ScriptAnalyzer.IdentifierRegex().IsMatch(name))
            return (null, $"Имя «{name}»: латиница, цифры и _, не с цифры");

        decimal? min = null, max = null;
        if (IsNumber)
        {
            if (Min.Trim().Length > 0)
            {
                if (!ParameterValues.TryParseDecimal(Min.Trim(), out var v)) return (null, $"{name}: минимум — не число");
                min = v;
            }
            if (Max.Trim().Length > 0)
            {
                if (!ParameterValues.TryParseDecimal(Max.Trim(), out var v)) return (null, $"{name}: максимум — не число");
                max = v;
            }
        }

        var items = new List<ListItem>();
        if (IsList)
        {
            foreach (var line in ItemsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                var eq = line.IndexOf('=');
                var item = eq > 0 ? new ListItem(line[..eq].Trim(), line[(eq + 1)..].Trim()) : new ListItem(line, line);
                if (items.Any(i => i.Key == item.Key))
                    return (null, $"{name}: элемент «{item.Key}» указан несколько раз");
                items.Add(item);
            }
            if (items.Count == 0 && !Editable)
                return (null, $"{name}: у списка нет элементов (или разрешите ввод своего значения)");
        }

        var p = Original with
        {
            Label = Label.Trim().Length > 0 ? Label.Trim() : name,
            Help = Help.Trim().Length > 0 ? Help.Trim() : null,
            Required = Required,
            Min = min,
            Max = max,
            Multiline = Type == ParameterType.Text || (Type == ParameterType.String && Multiline),
            Multi = IsList && Multi,
            Editable = IsList && Editable,
            Items = IsList ? items : [],
            Filter = IsFile && Filter.Trim().Length > 0 ? Filter.Trim() : null,
            Default = [],
        };
        p = p with { Name = name, Type = Type };

        var values = (p.Multi ? DefaultText.Split(';') : [DefaultText]).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        foreach (var value in values)
        {
            if (ParameterValues.Validate(p, value) is { } error)
                return (null, $"{name}, значение по умолчанию: {error}");
        }

        return (p with { Default = values }, null);
    }
}
