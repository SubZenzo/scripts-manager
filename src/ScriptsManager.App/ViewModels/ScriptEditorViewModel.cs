using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.Core.Authoring;
using ScriptsManager.Core.Catalog;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Settings;
using Microsoft.Win32;

namespace ScriptsManager.App.ViewModels;

public sealed record RunnerOption(RunnerKind Runner, string Name);

public sealed record DbOption(DbKind Kind, string Name);

/// <summary>Подключение в окне добавления/правки скрипта.</summary>
public sealed partial class ConnectionEditViewModel(ConnectionSlot original, bool fromManifest) : ObservableObject
{
    public ConnectionSlot Original { get; } = original;
    public string? OriginalName { get; } = fromManifest ? original.Name : null;

    [ObservableProperty]
    private string _name = original.Name;

    [ObservableProperty]
    private DbKind _type = original.Type;

    /// <summary>Выбирается только сервер (без базы).</summary>
    [ObservableProperty]
    private bool _serverOnly = original.Scope == ConnectionScope.Server;
}

/// <summary>Окно добавления скрипта (из файла или вставкой текста) и правки уже добавленного.</summary>
public sealed partial class ScriptEditorViewModel : ObservableObject
{
    private readonly ScriptEntry? _entry;
    private readonly string? _powerShell;
    private string? _originalScript;
    private readonly IReadOnlyList<string> _allTags;
    private bool _fileNameEdited;
    private bool _settingFileName;

    /// <summary>Новый скрипт.</summary>
    /// <param name="folder">Папка по умолчанию: корень или его подпапка.</param>
    public ScriptEditorViewModel(IReadOnlyList<ScriptRootSettings> roots, string? folder, AppSettings settings, IReadOnlyList<string> allTags)
    {
        _allTags = allTags;
        _powerShell = settings.PowerShellPath;
        Roots = roots;
        _selectedRoot = roots.FirstOrDefault(r => folder is not null && IsUnder(folder, r.Path)) ?? roots.FirstOrDefault();
        _folder = _selectedRoot is not null && folder is not null ? Relative(_selectedRoot.Path, folder) : "";
        UpdateFolders();
    }

    /// <summary>Манифест для файла скрипта, у которого его нет (<see cref="ScriptEntry.IsUnmanaged"/>).</summary>
    public static ScriptEditorViewModel ForUnmanaged(ScriptEntry entry, AppSettings settings, IReadOnlyList<string> allTags)
    {
        var editor = new ScriptEditorViewModel([], Path.GetDirectoryName(entry.ScriptPath), settings, allTags)
        {
            UnmanagedScript = entry.ScriptPath,
            UnmanagedManifestPath = entry.ManifestPath,
        };
        editor.LoadExtended(entry.DescriptionPath);
        return editor;
    }

    /// <summary>Правка скрипта с разобранным манифестом.</summary>
    public ScriptEditorViewModel(ScriptEntry entry, AppSettings settings, IReadOnlyList<string> allTags)
    {
        _allTags = allTags;
        var m = entry.Manifest ?? throw new ArgumentException("Манифест с ошибками", nameof(entry));
        _entry = entry;
        _powerShell = settings.PowerShellPath;
        Roots = [];
        _title = m.Title;
        _description = m.Description ?? "";
        LoadExtended(entry.DescriptionPath);
        foreach (var tag in m.Tags)
            TagList.Add(tag);
        _confirm = m.Confirm;
        _rerun = m.Rerun;
        _outputGrid = m.Output.Contains(OutputKind.Grid);
        _outputLog = m.Output.Contains(OutputKind.Log);
        _runner = m.Runner;
        try
        {
            _originalScript = ScriptFiles.Read(m.ScriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _message = $"Не удалось прочитать скрипт: {ex.Message}";
        }
        _scriptText = _originalScript ?? "";

        foreach (var p in m.Parameters)
            Parameters.Add(new ParameterEditViewModel(p, fromManifest: true));
        foreach (var c in m.Connections)
            Connections.Add(new ConnectionEditViewModel(c, fromManifest: true));
        SelectedParameter = Parameters.FirstOrDefault();
    }

    public bool IsNew => _entry is null;

    /// <summary>Файл скрипта без манифеста, для которого создаётся манифест.</summary>
    public string? UnmanagedScript { get; private init; }

    /// <summary>Куда записать манифест для <see cref="UnmanagedScript"/>.</summary>
    private string? UnmanagedManifestPath { get; init; }

    /// <summary>Можно выбрать, куда сохранить: только для нового скрипта, которого ещё нет в папке.</summary>
    public bool IsLocationEditable => IsNew && UnmanagedScript is null;

    public string WindowTitle =>
        UnmanagedScript is not null ? $"Манифест для скрипта — {Path.GetFileName(UnmanagedScript)}"
        : IsNew ? "Добавление скрипта" : $"Изменение скрипта — {_entry!.Title}";
    public string? ManifestPath => _entry?.ManifestPath;

    public static IReadOnlyList<RunnerOption> Runners { get; } =
        Enum.GetValues<RunnerKind>().Select(r => new RunnerOption(r, r.Name())).ToList();

    public static IReadOnlyList<DbOption> DbKinds { get; } =
        Enum.GetValues<DbKind>().Select(k => new DbOption(k, k.Name())).ToList();

    public static IReadOnlyList<TypeOption> Types => ParameterEditViewModel.Types;

    public IReadOnlyList<ScriptRootSettings> Roots { get; }
    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<ParameterEditViewModel> Parameters { get; } = [];
    public ObservableCollection<ConnectionEditViewModel> Connections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    private ScriptRootSettings? _selectedRoot;

    /// <summary>Подпапка внутри корня, например «Работа с очередями».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    private string _folder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    private string _fileName = "";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _description = "";

    /// <summary>Подробное описание (markdown), файл рядом с манифестом.</summary>
    [ObservableProperty]
    private string _extendedDescription = "";

    private string? _originalExtended;

    private void LoadExtended(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            ExtendedDescription = _originalExtended = ScriptFiles.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Не удалось прочитать описание: {ex.Message}";
        }
    }

    public ObservableCollection<string> TagList { get; } = [];

    /// <summary>Теги других скриптов, которых у этого ещё нет.</summary>
    public IReadOnlyList<string> SuggestedTags =>
        _allTags.Where(t => !TagList.Contains(t, StringComparer.CurrentCultureIgnoreCase)).ToList();

    /// <summary>Вводимый тег.</summary>
    [ObservableProperty]
    private string _newTag = "";

    [ObservableProperty]
    private bool _confirm;

    [ObservableProperty]
    private bool _rerun;

    [ObservableProperty]
    private bool _outputGrid = true;

    [ObservableProperty]
    private bool _outputLog = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath), nameof(Syntax))]
    private RunnerKind _runner = RunnerKind.TSql;

    [ObservableProperty]
    private string _scriptText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ParameterEditViewModel? _selectedParameter;

    [ObservableProperty]
    private ConnectionEditViewModel? _selectedConnection;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSelection => SelectedParameter is not null;
    public string Syntax => Runner.Highlighting();

    /// <summary>Файл скрипта (манифест и пресеты — в .manifests и .presets папки со скриптами).</summary>
    public string? TargetPath => UnmanagedScript is not null ? UnmanagedScript
        : IsNew && SelectedRoot is not null && FileName.Trim().Length > 0
        ? Path.Combine(SelectedRoot.Path, Folder.Trim(), FileName.Trim() + Runner.FileExtension())
        : _entry?.ScriptPath;

    partial void OnSelectedRootChanged(ScriptRootSettings? value) => UpdateFolders();

    partial void OnTitleChanged(string value)
    {
        if (IsNew && !_fileNameEdited)
        {
            _settingFileName = true;
            FileName = value.Trim().Length > 0 ? ScriptFiles.FileName(value) : "";
            _settingFileName = false;
        }
    }

    partial void OnFileNameChanged(string value)
    {
        if (!_settingFileName)
            _fileNameEdited = true;
    }

    partial void OnRunnerChanged(RunnerKind oldValue, RunnerKind newValue)
    {
        // Подключение, созданное по умолчанию для SQL, следует за выбранным языком.
        if (Connections is [{ OriginalName: null } only] && only.Name == "connection")
        {
            if (newValue is RunnerKind.TSql or RunnerKind.PlPgSql)
                only.Type = newValue == RunnerKind.TSql ? DbKind.MsSql : DbKind.Postgres;
        }
    }

    /// <summary>Загрузить скрипт из файла (диалог).</summary>
    [RelayCommand]
    private Task BrowseFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Файл скрипта",
            Filter = "Скрипты (*.sql;*.ps1;*.py)|*.sql;*.ps1;*.py|Все файлы|*.*",
        };
        return dialog.ShowDialog() == true ? LoadFileAsync(dialog.FileName) : Task.CompletedTask;
    }

    public async Task LoadFileAsync(string path)
    {
        string text;
        try
        {
            text = ScriptFiles.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Не удалось прочитать файл: {ex.Message}";
            return;
        }

        if (IsNew)
        {
            Runner = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".ps1" => RunnerKind.PowerShell,
                ".py" => RunnerKind.Python,
                _ => ScriptAnalyzer.LooksLikePostgres(text) ? RunnerKind.PlPgSql : RunnerKind.TSql,
            };
            var name = Path.GetFileNameWithoutExtension(path);
            if (Title.Trim().Length == 0)
                Title = name;
            _fileNameEdited = false;
            _settingFileName = true;
            FileName = ScriptFiles.FileName(name);
            _settingFileName = false;
        }

        ScriptText = text;
        // Файл без манифеста перезаписывается, только если текст изменился.
        if (string.Equals(path, UnmanagedScript, StringComparison.OrdinalIgnoreCase))
            _originalScript = text;
        await AnalyzeAsync();
    }

    /// <summary>Текст вставлен в пустое поле: угадать язык и найти параметры.</summary>
    public async Task OnPastedAsync(bool wasEmpty)
    {
        if (IsNew && wasEmpty)
            Runner = Guess(ScriptText);
        await AnalyzeAsync();
    }

    /// <summary>Найти параметры в тексте скрипта и добавить те, которых ещё нет в списке.</summary>
    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (ScriptText.Trim().Length == 0 || IsBusy)
            return;

        IsBusy = true;
        Message = Runner == RunnerKind.PowerShell ? "Разбор param()…" : null;
        try
        {
            var analysis = await ScriptAnalyzer.AnalyzeAsync(Runner, ScriptText, _powerShell);
            var added = 0;
            foreach (var found in analysis.Parameters)
            {
                // @слот_server и @слот_database передаются для дополнительных подключений.
                if (Connections.Any(c => found.Parameter.Name.Equals(c.Name + "_server", StringComparison.OrdinalIgnoreCase)
                                         || found.Parameter.Name.Equals(c.Name + "_database", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var existing = Parameters.FirstOrDefault(p => p.Name.Equals(found.Parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.Source = found.Source;
                    continue;
                }

                // При правке найденное в скрипте не переносится само: переменная в нём уже что-то значит.
                // Объявленное в блоке --#region Params — уже параметр.
                Parameters.Add(new ParameterEditViewModel(found.Parameter, fromManifest: false, found.Source, found.Note)
                    { Include = IsNew || found.InParamsRegion });
                added++;
            }

            foreach (var name in analysis.Connections.Where(n => !Connections.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
                Connections.Add(new ConnectionEditViewModel(new ConnectionSlot(name, DbKind.MsSql, ConnectionScope.Database), false));
            if (IsNew && Connections.Count == 0 && Runner is RunnerKind.TSql or RunnerKind.PlPgSql)
                AddDefaultConnection();

            SelectedParameter ??= Parameters.FirstOrDefault();
            Message = analysis.Error
                ?? (added > 0 ? $"Найдено новых параметров: {added}"
                    : analysis.Parameters.Count > 0 ? "Новых параметров в скрипте нет" : NothingFoundHint());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string NothingFoundHint() => Runner switch
    {
        RunnerKind.TSql => "Параметры не найдены: ищутся DECLARE в начале скрипта",
        RunnerKind.PlPgSql => "Параметры не найдены: ищутся @имя, current_setting('param.имя') и DECLARE блока DO",
        RunnerKind.PowerShell => "Параметры не найдены: ищется блок param()",
        _ => "Параметры не найдены: ищутся вызовы add_argument",
    };

    /// <summary>Добавить введённый тег (несколько — через запятую).</summary>
    [RelayCommand]
    private void AddTag()
    {
        foreach (var tag in NewTag.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            // Регистр — как у уже существующего тега, чтобы фильтр не двоился.
            var name = _allTags.FirstOrDefault(t => t.Equals(tag, StringComparison.CurrentCultureIgnoreCase)) ?? tag;
            if (!TagList.Contains(name, StringComparer.CurrentCultureIgnoreCase))
                TagList.Add(name);
        }
        NewTag = "";
        OnPropertyChanged(nameof(SuggestedTags));
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (tag is not null && TagList.Remove(tag))
            OnPropertyChanged(nameof(SuggestedTags));
    }

    [RelayCommand]
    private void AddParameter()
    {
        var n = 1;
        while (Parameters.Any(p => p.Name.Equals($"param{n}", StringComparison.OrdinalIgnoreCase)))
            n++;
        var p = new ParameterEditViewModel(new ScriptParameter($"param{n}", ParameterType.String), fromManifest: false);
        Parameters.Add(p);
        SelectedParameter = p;
    }

    [RelayCommand]
    private void RemoveParameter()
    {
        if (SelectedParameter is not { } p)
            return;
        var index = Parameters.IndexOf(p);
        Parameters.Remove(p);
        SelectedParameter = Parameters.Count == 0 ? null : Parameters[Math.Min(index, Parameters.Count - 1)];
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedParameter is not { } p)
            return;
        var index = Parameters.IndexOf(p);
        if (index + delta < 0 || index + delta >= Parameters.Count)
            return;
        Parameters.Move(index, index + delta);
        SelectedParameter = p;
    }

    [RelayCommand]
    private void AddConnection()
    {
        if (Connections.Count == 0)
        {
            AddDefaultConnection();
            return;
        }
        var n = 2;
        while (Connections.Any(c => c.Name.Equals($"connection{n}", StringComparison.OrdinalIgnoreCase)))
            n++;
        Connections.Add(new ConnectionEditViewModel(new ConnectionSlot($"connection{n}", DbKind.MsSql, ConnectionScope.Database), false));
    }

    [RelayCommand]
    private void RemoveConnection()
    {
        if (SelectedConnection is { } c)
            Connections.Remove(c);
    }

    private void AddDefaultConnection() =>
        Connections.Add(new ConnectionEditViewModel(
            new ConnectionSlot("connection", Runner == RunnerKind.PlPgSql ? DbKind.Postgres : DbKind.MsSql, ConnectionScope.Database), false));

    /// <summary>Проверить и записать скрипт с манифестом; возвращает путь манифеста или ошибку.</summary>
    public async Task<(string? ManifestPath, string? Error)> SaveAsync(Func<string, bool> confirmOverwrite)
    {
        if (IsBusy)
            return (null, null);
        if (Title.Trim().Length == 0)
            return (null, "Укажите название скрипта");
        if (ScriptText.Trim().Length == 0)
            return (null, "Скрипт пустой: загрузите файл или вставьте текст");

        var parameters = new List<ScriptParameter>();
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Parameters.Where(p => p.Include))
        {
            var (p, error) = row.Build();
            if (p is null)
            {
                SelectedParameter = row;
                return (null, error);
            }
            if (parameters.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"Параметр «{p.Name}» указан несколько раз");
            parameters.Add(p);
            if (row.OriginalName is { } old)
                renames[old] = p.Name;
        }

        var connections = new List<ConnectionSlot>();
        var connectionRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Connections)
        {
            var name = row.Name.Trim();
            if (!ScriptAnalyzer.IdentifierRegex().IsMatch(name))
                return (null, $"Подключение «{name}»: имя из латиницы, цифр и _, не с цифры");
            if (connections.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"Подключение «{name}» указано несколько раз");
            if (parameters.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"Имя «{name}» занято и подключением, и параметром");
            var scope = row.ServerOnly ? ConnectionScope.Server : ConnectionScope.Database;
            var slot = row.Original with { Name = name, Type = row.Type, Scope = scope };
            // Сервер по умолчанию от другой СУБД не подходит.
            if (row.Type != row.Original.Type)
                slot = slot with { Default = null };
            else if (scope == ConnectionScope.Server && slot.Default is { } d)
                slot = slot with { Default = d with { Database = null } };
            connections.Add(slot);
            if (row.OriginalName is { } old)
                connectionRenames[old] = name;
        }
        if (Runner is RunnerKind.TSql or RunnerKind.PlPgSql && connections.Count == 0)
            return (null, "SQL-скрипту нужно подключение");

        var output = new List<OutputKind>();
        if (OutputGrid) output.Add(OutputKind.Grid);
        if (OutputLog) output.Add(OutputKind.Log);
        if (output.Count == 0)
            return (null, "Выберите хотя бы один вид вывода");

        string manifestPath, scriptPath;
        if (_entry is { Manifest: { } existing })
        {
            manifestPath = _entry.ManifestPath;
            scriptPath = existing.ScriptPath;
        }
        else if (UnmanagedScript is not null)
        {
            if (!Path.GetExtension(UnmanagedScript).Equals(Runner.FileExtension(), StringComparison.OrdinalIgnoreCase))
                return (null, $"Язык {Runner.Name()} не подходит к файлу {Path.GetFileName(UnmanagedScript)}");
            manifestPath = UnmanagedManifestPath!;
            scriptPath = UnmanagedScript;
            if (File.Exists(manifestPath) && !confirmOverwrite($"Манифест {manifestPath} уже есть. Заменить?"))
                return (null, null);
        }
        else
        {
            if (SelectedRoot is null)
                return (null, "Выберите папку со скриптами");
            var fileName = FileName.Trim();
            if (fileName.Length == 0 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return (null, "Имя файла пустое или содержит недопустимые символы");
            var folder = Folder.Trim().Trim('\\', '/');
            if (folder.Split('\\', '/').Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                return (null, "Подпапка указана неверно");

            scriptPath = Path.GetFullPath(Path.Combine(SelectedRoot.Path, folder, fileName + Runner.FileExtension()));
            manifestPath = ScriptLayout.ManifestPath(SelectedRoot.Path, scriptPath);
            var busy = new[] { scriptPath, manifestPath }.Where(File.Exists).ToList();
            if (busy.Count > 0 && !confirmOverwrite($"Уже есть {string.Join(" и ", busy)}. Заменить?"))
                return (null, null);
        }

        // T-SQL: переменные-параметры приложение передаёт само; их объявления собираются в блок --#region Params,
        // который при запуске пропускается, — так скрипт можно запустить и вне приложения.
        // PowerShell и Python: значения приходят аргументами, и скрипт должен их объявить — недостающие дописываются
        // в param() и argparse. PL/pgSQL берёт значения сам (@имя, current_setting); в блок DO их передать нельзя,
        // поэтому переменные DECLARE параметров читают current_setting, а недостающие дописываются.
        string text;
        switch (Runner)
        {
            case RunnerKind.TSql:
                text = TSqlParamsRegion.Update(ScriptText, parameters, connections.Skip(1).Select(c => c.Name));
                break;
            case RunnerKind.PowerShell when parameters.Count > 0:
                IsBusy = true;
                Message = "Проверка param()…";
                ScriptAnalysis analysis;
                try
                {
                    analysis = await ScriptAnalyzer.AnalyzeAsync(Runner, ScriptText, _powerShell);
                }
                finally
                {
                    IsBusy = false;
                    Message = null;
                }
                if (analysis.ParamBlock is not { } block)
                    return (null, (analysis.Error ?? "Не удалось разобрать param()") + ". Исправьте скрипт или объявите параметры в param() вручную");
                text = ScriptParamsWriter.PowerShell(ScriptText, Missing(analysis), block);
                break;
            case RunnerKind.PlPgSql:
                text = PgDoBlock.Update(ScriptText, parameters);
                break;
            case RunnerKind.Python:
                text = ScriptParamsWriter.Python(ScriptText, parameters, Missing(ScriptAnalyzer.Analyze(Runner, ScriptText)));
                break;
            default:
                text = ScriptText;
                break;
        }

        List<ScriptParameter> Missing(ScriptAnalysis found) =>
            parameters.Where(p => !found.Parameters.Any(d => d.Parameter.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))).ToList();

        var manifest = new ScriptManifest
        {
            Title = Title.Trim(),
            Description = Description.Trim().Length > 0 ? Description.Trim() : null,
            Runner = Runner,
            ScriptPath = scriptPath,
            Tags = TagList.ToList(),
            Output = output,
            Confirm = Confirm,
            Rerun = Rerun,
            Connections = connections,
            Parameters = parameters,
        };

        try
        {
            ScriptFiles.Save(manifest, manifestPath, _originalScript is null || text != _originalScript ? text : null);
            var extendedPath = ScriptLayout.DescriptionPath(manifestPath);
            if (ExtendedDescription != _originalExtended && (ExtendedDescription.Trim().Length > 0 || File.Exists(extendedPath)))
                ScriptFiles.SaveDescription(extendedPath, ExtendedDescription);

            // Пресеты следуют за переименованием параметров и подключений.
            if (_entry is not null && File.Exists(_entry.PresetsPath))
            {
                var presets = PresetFile.Load(_entry.PresetsPath);
                var renamed = presets.Presets.Select(p => RenamePreset(p, renames, connectionRenames, parameters, connections)).ToList();
                if (presets.Issues.Count == 0 && PresetFile.Write(renamed) != PresetFile.Write(presets.Presets))
                    PresetFile.Save(_entry.PresetsPath, renamed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Не удалось сохранить: {ex.Message}");
        }

        return (manifestPath, null);
    }

    /// <summary>Пресет после переименования и удаления параметров и подключений.</summary>
    private static ScriptPreset RenamePreset(ScriptPreset preset, Dictionary<string, string> renames, Dictionary<string, string> connectionRenames,
        List<ScriptParameter> parameters, List<ConnectionSlot> connections)
    {
        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, v) in preset.Values)
        {
            if (renames.TryGetValue(name, out var newName) && parameters.FirstOrDefault(p => p.Name == newName) is { } p
                && v.All(x => ParameterValues.Validate(p, x) is null) && (v.Count <= 1 || p.Multi))
                values[newName] = v;
        }

        var targets = new Dictionary<string, ConnectionTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, t) in preset.Connections)
        {
            if (connectionRenames.TryGetValue(name, out var newName) && connections.FirstOrDefault(c => c.Name == newName) is { } slot)
                targets[newName] = slot.Scope == ConnectionScope.Server ? t with { Database = null } : t;
        }

        return preset with { Values = values, Connections = targets };
    }

    private static RunnerKind Guess(string text)
    {
        var t = text.TrimStart();
        if (t.StartsWith("param", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<#") || text.Contains("$env:") || text.Contains("Write-Host"))
            return RunnerKind.PowerShell;
        if (text.Contains("import ") || text.Contains("def ") || text.Contains("argparse"))
            return RunnerKind.Python;
        return ScriptAnalyzer.LooksLikePostgres(text) ? RunnerKind.PlPgSql : RunnerKind.TSql;
    }

    private void UpdateFolders()
    {
        Folders.Clear();
        if (SelectedRoot is null || !Directory.Exists(SelectedRoot.Path))
            return;
        Folders.Add("");
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(SelectedRoot.Path, "*", SearchOption.AllDirectories)
                         .Select(d => Relative(SelectedRoot.Path, d))
                         .Where(d => !d.Split('\\').Any(part => part.StartsWith('.')))
                         .Order(StringComparer.CurrentCultureIgnoreCase))
                Folders.Add(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? "" : relative;
    }

    private static bool IsUnder(string path, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
