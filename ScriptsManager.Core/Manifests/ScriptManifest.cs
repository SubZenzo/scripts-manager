namespace ScriptsManager.Core.Manifests;

public sealed record ScriptManifest
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required RunnerKind Runner { get; init; }

    /// <summary>Полный путь к файлу скрипта.</summary>
    public required string ScriptPath { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<OutputKind> Output { get; init; } = [OutputKind.Grid, OutputKind.Log];
    public bool Confirm { get; init; }

    /// <summary>На вкладке «Результат» можно запустить скрипт ещё раз с теми же параметрами.</summary>
    public bool Rerun { get; init; }
    public IReadOnlyList<ConnectionSlot> Connections { get; init; } = [];
    public IReadOnlyList<ScriptParameter> Parameters { get; init; } = [];
}

public sealed record ConnectionSlot(string Name, DbKind Type, ConnectionScope Scope)
{
    public ConnectionTarget? Default { get; init; }
}

public sealed record ConnectionTarget(string Server, int? Port = null, string? Database = null);

public sealed record ScriptParameter(string Name, ParameterType Type)
{
    public string Label { get; init; } = Name;
    public string? Help { get; init; }
    public bool Required { get; init; } = true;

    /// <summary>Значение по умолчанию: пусто — нет, несколько значений — только для <see cref="Multi"/>.</summary>
    public IReadOnlyList<string> Default { get; init; } = [];

    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public bool Multiline { get; init; }
    public bool Multi { get; init; }
    public bool Editable { get; init; }
    public IReadOnlyList<ListItem> Items { get; init; } = [];

    /// <summary>Фильтр диалога выбора файла, например <c>SQL|*.sql</c>.</summary>
    public string? Filter { get; init; }
}

/// <summary>Элемент списка: показывается <see cref="Value"/>, передаётся <see cref="Key"/>.</summary>
public sealed record ListItem(string Key, string Value);

/// <summary>Пресет: значения параметров (как в форме) и подключения; хранится в файле пресетов скрипта.</summary>
public sealed record ScriptPreset(string Name)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Values { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyDictionary<string, ConnectionTarget> Connections { get; init; } =
        new Dictionary<string, ConnectionTarget>();
}
