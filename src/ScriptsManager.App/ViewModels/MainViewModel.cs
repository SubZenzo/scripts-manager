using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.Core.Catalog;
using ScriptsManager.Core.Git;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly ConnectionService _connections;
    private readonly Dictionary<string, FormState> _formStates = new(StringComparer.OrdinalIgnoreCase);
    private List<(ScriptRootSettings Settings, ScriptFolder Folder)> _roots = [];

    // Git по папкам со скриптами (ключ — путь папки).
    private readonly Dictionary<string, GitRepository> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GitStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _gitErrors = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _gitUpdate;

    private TreeNodeViewModel? _selectedNode;
    private DateTime _selectedManifestTime;

    // Скрипт, который выбрать после перестроения дерева (только что добавленный или изменённый).
    private string? _pendingSelection;

    public MainViewModel(SettingsStore store, ConnectionService connections)
    {
        _store = store;
        _connections = connections;
        try
        {
            Settings = store.Load();
        }
        catch (Exception ex)
        {
            Settings = new AppSettings();
            Status = $"Не удалось прочитать настройки {store.FilePath}: {ex.Message}";
        }

        Runs = new RunRegistry(connections, () => Settings);
        Runs.Changed += OnRunsChanged;
        Runs.Finished += OnRunFinished;
        _toastTimer.Tick += (_, _) => CloseToast();
        Refresh();
    }

    /// <summary>Активно ли окно: завершённый запуск на экране считается просмотренным, только если на него смотрят.</summary>
    public Func<bool> IsWindowActive { get; set; } = () => true;

    /// <summary>Запуск завершился — окно мигает на панели задач, если неактивно.</summary>
    public event Action<RunViewModel>? RunFinished;

    public bool HasRuns => Runs.All.Count > 0;

    public string RunsHeader => Runs.Running.Count() is var running and > 0 ? $"Запуски — выполняется: {running}" : "Запуски";

    private void OnRunsChanged()
    {
        ApplyRunMarks();
        SelectedScript?.RunsChanged();
        // Результат посмотрели или удалили — сообщение больше не нужно.
        if (ToastRun is { } toast && (toast.IsSeen || !Runs.All.Contains(toast)))
            CloseToast();
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(RunsHeader));
    }

    private void OnRunFinished(RunViewModel run)
    {
        Status = FinishedText(run) + (run.Connection.Length > 0 ? $" · {run.Connection}" : "");
        if (IsWindowActive())
            SelectedScript?.MarkSeen();
        // Результат уже на экране — сообщать не о чем.
        if (!run.IsSeen)
        {
            ToastRun = run;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
        RunFinished?.Invoke(run);
    }

    public static string FinishedText(RunViewModel run) => $"«{run.DisplayName}»: {run.StateName} за {run.Elapsed:hh\\:mm\\:ss}";

    // Сообщение в углу окна о завершённом запуске: «Открыть» — к результату; само скрывается через 15 с.
    private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToastText))]
    private RunViewModel? _toastRun;

    public string? ToastText => ToastRun is { } run ? FinishedText(run) : null;

    [RelayCommand]
    private void OpenToast()
    {
        if (ToastRun is { } run)
            OpenRun(run);
        CloseToast();
    }

    [RelayCommand]
    private void CloseToast()
    {
        _toastTimer.Stop();
        ToastRun = null;
    }

    /// <summary>Своё название запуска, чтобы отличать результаты (например, по серверу).</summary>
    public void RenameRun(RunViewModel run)
    {
        if (Dialogs.AskText("Название запуска", $"Название запуска «{run.ScriptTitle}» ({run.Details}); пусто — название скрипта:",
                run.Name ?? "") is not { } name)
            return;
        run.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public void DeleteRun(RunViewModel run)
    {
        if (run.IsRunning)
        {
            Dialogs.Info("Запуск ещё выполняется — остановите его, затем удалите результат");
            return;
        }
        if (!Dialogs.Confirm($"Удалить результат запуска «{run.DisplayName}» ({run.Details})?"))
            return;
        if (ToastRun == run)
            CloseToast();
        Runs.Remove(run);
    }

    /// <summary>Окно снова активно — запуск, который сейчас на экране, посмотрели.</summary>
    public void WindowActivated() => SelectedScript?.MarkSeen();

    /// <summary>Отметки в дереве: выполняется или завершился, а результат ещё не смотрели.</summary>
    private void ApplyRunMarks()
    {
        foreach (var node in Nodes.SelectMany(Descendants).Where(n => n.Script is not null))
        {
            var runs = Runs.For(node.Script!.ManifestPath);
            node.RunMark = runs.Any(r => r.IsRunning) ? RunState.Running
                : runs.FirstOrDefault(r => !r.IsSeen)?.State;
        }
    }

    /// <summary>Открыть запуск из списка: выбрать скрипт и показать результат.</summary>
    public void OpenRun(RunViewModel run)
    {
        var node = FindScriptNode(run.ManifestPath);
        if (node is null && (SearchText.Length > 0 || TagFilters.Any(t => t.IsChecked)))
        {
            // Скрипт скрыт поиском или тегами.
            SearchText = "";
            ClearTags();
            node = FindScriptNode(run.ManifestPath);
        }
        if (node is null)
        {
            Status = $"Скрипта «{run.ScriptTitle}» больше нет в дереве";
            return;
        }

        node.IsSelected = true;
        if (!ReferenceEquals(_selectedNode, node))
            Select(node);
        if (SelectedScript is { } details)
        {
            details.CurrentRun = run;
            details.SelectedTabIndex = ScriptDetailsViewModel.ResultTabIndex;
            details.MarkSeen();
        }
    }

    private TreeNodeViewModel? FindScriptNode(string manifestPath) =>
        Nodes.SelectMany(Descendants).FirstOrDefault(n => n.Script?.ManifestPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) == true);

    [RelayCommand]
    private void RemoveFinishedRuns() => Runs.RemoveFinished();

    public AppSettings Settings { get; private set; }
    public RunRegistry Runs { get; }
    public ObservableCollection<TreeNodeViewModel> Nodes { get; } = [];
    public bool HasNoRoots => Settings.Roots.Count == 0;

    /// <summary>Открыть окно настроек; возвращает новые настройки или null.</summary>
    public Func<AppSettings, AppSettings?>? EditSettings { get; set; }

    /// <summary>Открыть окно добавления/правки скрипта; возвращает путь сохранённого манифеста или null.</summary>
    public Func<ScriptEditorViewModel, string?>? EditScriptDialog { get; set; }

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private ScriptDetailsViewModel? _selectedScript;

    [ObservableProperty]
    private string _status = "";

    /// <summary>Все теги скриптов; отмеченные фильтруют дерево (нужны все отмеченные сразу).</summary>
    public ObservableCollection<TagFilterViewModel> TagFilters { get; } = [];

    public bool HasTags => TagFilters.Count > 0;

    partial void OnSearchTextChanged(string value) => BuildTree();

    [RelayCommand]
    private void ClearTags()
    {
        foreach (var tag in TagFilters)
            tag.SetChecked(false);
        BuildTree();
    }

    private void UpdateTags(IEnumerable<ScriptEntry> scripts)
    {
        var checkedTags = TagFilters.Where(t => t.IsChecked).Select(t => t.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        TagFilters.Clear();
        foreach (var name in AllTags(scripts))
        {
            var tag = new TagFilterViewModel(name, BuildTree);
            tag.SetChecked(checkedTags.Contains(name));
            TagFilters.Add(tag);
        }
        OnPropertyChanged(nameof(HasTags));
    }

    private static List<string> AllTags(IEnumerable<ScriptEntry> scripts) =>
        scripts.SelectMany(s => s.Manifest?.Tags ?? [])
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public void Select(TreeNodeViewModel? node)
    {
        // Повторный выбор того же узла (из списка запусков) не пересоздаёт карточку.
        if (node is not null && ReferenceEquals(node, _selectedNode) && SelectedScript is not null)
            return;
        // Введённые значения сохраняются на время работы приложения при переходе между скриптами.
        if (SelectedScript?.Form is { } form)
            _formStates[SelectedScript.ManifestPath] = form.GetState();

        _selectedNode = node;
        SelectedScript = node?.Script is { } entry
            ? new ScriptDetailsViewModel(entry, _connections, Runs, _formStates.GetValueOrDefault(entry.ManifestPath))
            : null;
        _selectedManifestTime = SelectedScript is { } details ? ManifestTime(details.ManifestPath) : default;
    }

    private static DateTime ManifestTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;

    /// <summary>Перечитать папки со скриптами и узнать об изменениях на сервере.</summary>
    [RelayCommand]
    private void Refresh() => Reload(fetch: true);

    /// <param name="fetch">Ещё и git fetch — может занять время и спросить учётные данные.</param>
    private void Reload(bool fetch)
    {
        var selected = _pendingSelection ?? SelectedScript?.ManifestPath;
        if (SelectedScript is { } current && ManifestTime(current.ManifestPath) != _selectedManifestTime)
        {
            // Манифест изменился (pull, правка) — введённые значения не переносим, иначе не видно новых значений
            // по умолчанию. Карточку убираем сразу: при очистке дерева TreeView снимает выбор и сохранил бы её значения.
            _formStates.Remove(current.ManifestPath);
            SelectedScript = null;
            _pendingSelection ??= current.ManifestPath;
        }

        _roots = Settings.Roots.Select(r => (r, ScriptCatalog.LoadRoot(r))).ToList();
        var scripts = _roots.SelectMany(r => Flatten(r.Folder)).ToList();
        UpdateTags(scripts);
        BuildTree();
        // Карточка — по перечитанному манифесту. Сами: TreeView не сообщает о выборе нового узла, пока тот в свёрнутой папке.
        if (selected is not null && FindScriptNode(selected) is { } node)
            Select(node);

        var unmanaged = scripts.Count(s => s.IsUnmanaged);
        Status = $"Скриптов: {scripts.Count}, с ошибками в манифесте: {scripts.Count(s => s.Result.HasErrors && !s.IsUnmanaged)}"
                 + (unmanaged > 0 ? $", без манифеста: {unmanaged}" : "");
        OnPropertyChanged(nameof(HasNoRoots));
        _ = UpdateGitAsync(fetch);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (EditSettings?.Invoke(Settings) is not { } updated)
            return;

        try
        {
            _store.Save(updated);
        }
        catch (Exception ex)
        {
            Status = $"Не удалось сохранить настройки: {ex.Message}";
        }

        Settings = updated;
        Refresh();
    }

    /// <summary>git pull --ff-only для папки со скриптами.</summary>
    [RelayCommand]
    private async Task PullAsync(TreeNodeViewModel? root)
    {
        if (root is null || !_repos.TryGetValue(root.RootPath, out var repo))
            return;

        Status = $"{root.Name}: получение изменений…";
        try
        {
            await repo.PullAsync();
            Status = $"{root.Name}: изменения получены";
        }
        catch (GitException ex)
        {
            Status = $"{root.Name}: не удалось получить изменения";
            Dialogs.ShowText("git pull", ex.Message);
        }

        Reload(fetch: false);
    }

    /// <summary>Commit выбранных изменённых файлов папки и push.</summary>
    [RelayCommand]
    private async Task PublishAsync(TreeNodeViewModel? root)
    {
        if (root is null || !_repos.TryGetValue(root.RootPath, out var repo))
            return;

        GitStatus status;
        try
        {
            status = await repo.StatusAsync();
        }
        catch (GitException ex)
        {
            Dialogs.ShowText("git status", ex.Message);
            return;
        }

        var presets = new Dictionary<string, (IReadOnlyList<ScriptPreset> Published, IReadOnlyList<ScriptPreset> Current, int Changes)>(
            StringComparer.OrdinalIgnoreCase);
        var changes = await PublishItemsAsync(repo, root.RootPath, status, presets);
        if (changes.Count == 0)
        {
            Dialogs.Info($"В папке «{root.Name}» нет неопубликованных изменений");
            return;
        }

        var publish = new PublishViewModel(root.Name, status.Branch, changes);
        if (!Dialogs.Publish(publish))
            return;

        // Отмечены все изменения пресетов файла — файл целиком, иначе — опубликованные пресеты плюс отмеченные.
        var files = publish.SelectedFiles.ToList();
        var partial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, names) in publish.SelectedPresets)
        {
            var p = presets[file];
            if (names.Count == p.Changes)
                files.Add(file);
            else
                partial[file] = PresetFile.Write(PresetChanges.Apply(p.Published, p.Current, names));
        }

        Status = $"{root.Name}: публикация…";
        try
        {
            var log = await repo.CommitAndPushAsync(files, publish.Message.Trim(), partial);
            Status = $"{root.Name}: опубликовано";
            Dialogs.ShowText("Публикация", log);
        }
        catch (GitException ex)
        {
            Status = $"{root.Name}: не удалось опубликовать";
            Dialogs.ShowText("Публикация", ex.Message);
        }

        Reload(fetch: false);
    }

    /// <summary>
    /// Строки окна публикации: скрипт вместе с манифестом, под ним — изменённые пресеты по одному; прочие файлы — как есть.
    /// </summary>
    private static async Task<List<PublishItem>> PublishItemsAsync(GitRepository repo, string root, GitStatus status,
        Dictionary<string, (IReadOnlyList<ScriptPreset> Published, IReadOnlyList<ScriptPreset> Current, int Changes)> presets)
    {
        var scripts = new SortedDictionary<string, (List<string> Paths, GitFileState? State, string? Presets)>(StringComparer.CurrentCultureIgnoreCase);
        var others = new List<PublishItem>();
        foreach (var (path, state) in status.Files.Where(f => IsUnder(f.Key, root)).OrderBy(f => f.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var top = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0];
            var side = top is ScriptLayout.ManifestsFolder or ScriptLayout.PresetsFolder && path.EndsWith(ScriptLayout.Extension, StringComparison.OrdinalIgnoreCase);
            // Подробное описание публикуется вместе со скриптом, как манифест.
            var description = top == ScriptLayout.ManifestsFolder && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && ScriptLayout.IsScript(Path.GetFileNameWithoutExtension(path));
            var script = description ? ScriptLayout.ScriptPath(root, Path.ChangeExtension(path, ScriptLayout.Extension))
                : side ? ScriptLayout.ScriptPath(root, path) : ScriptLayout.IsScript(path) ? path : null;
            if (script is null)
            {
                others.Add(PublishItem.Files(Path.GetRelativePath(root, path), state, [path]));
                continue;
            }

            var group = scripts.GetValueOrDefault(script, ([], null, null));
            if (top == ScriptLayout.PresetsFolder)
                group.Presets = path;
            else
            {
                group.Paths.Add(path);
                // Состояние строки — по файлу скрипта, если он изменён, иначе по манифесту.
                if (group.State is null || path.Equals(script, StringComparison.OrdinalIgnoreCase))
                    group.State = state;
            }
            scripts[script] = group;
        }

        var items = new List<PublishItem>();
        foreach (var (script, group) in scripts)
        {
            var name = Path.GetRelativePath(root, script);
            if (group.Paths.Count > 0)
                items.Add(PublishItem.Files(name, group.State!.Value, group.Paths));
            if (group.Presets is not { } file)
                continue;

            var current = PresetFile.Load(file);
            var published = PresetFile.Parse(await repo.ReadCommittedAsync(file) ?? "");
            var diff = PresetChanges.Diff(published.Presets, current.Presets);
            if (current.Issues.Concat(published.Issues).Any(i => i.Severity == IssueSeverity.Error) || diff.Count == 0)
            {
                // Не разобрать или изменилось только оформление — файлом целиком.
                items.Add(PublishItem.Files($"{name}: пресеты", status.Files[file], [file]));
                continue;
            }

            if (group.Paths.Count == 0)
                items.Add(PublishItem.Header(name));
            presets[file] = (published.Presets, current.Presets, diff.Count);
            items.AddRange(diff.Select(c => PublishItem.Preset(file, c)));
        }

        return [.. items, .. others];
    }

    /// <summary>Узнать об изменениях на сервере (git fetch) без обновления файлов.</summary>
    [RelayCommand]
    private Task FetchAsync() => UpdateGitAsync(fetch: true);

    /// <summary>Скопировать скрипт в другую папку со скриптами (например, из личной в командную).</summary>
    [RelayCommand]
    private void CopyTo(CopyTarget? target)
    {
        if (target?.Script.Script is not { } entry)
            return;

        try
        {
            var (manifest, script, _) = ScriptCopier.TargetPaths(entry, target.Script.RootPath, target.Root.Path);
            var exists = File.Exists(manifest) || File.Exists(script);
            if (exists && !Dialogs.Confirm($"В папке «{target.Root.Name}» уже есть {Path.GetFileName(script)}. Заменить?"))
                return;

            ScriptCopier.Copy(entry, target.Script.RootPath, target.Root.Path, overwrite: true);
            Status = $"«{entry.Title}» скопирован в «{target.Root.Name}»: {Path.GetRelativePath(target.Root.Path, script)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Dialogs.Info($"Не удалось скопировать: {ex.Message}");
            return;
        }

        Reload(fetch: false);
    }

    /// <summary>Добавить скрипт в папку узла (для скрипта — в его папку; без узла — в папку выбранного).</summary>
    [RelayCommand]
    private void AddScript(TreeNodeViewModel? node) => AddScriptFrom(node, file: null);

    /// <summary>Файл перетащен в главное окно.</summary>
    public void AddScriptFromFile(string file) => AddScriptFrom(null, file);

    private void AddScriptFrom(TreeNodeViewModel? node, string? file)
    {
        if (Settings.Roots.Count == 0)
        {
            Dialogs.Info("Сначала добавьте папку со скриптами в настройках");
            return;
        }

        node ??= _selectedNode;
        var folder = node switch
        {
            { Kind: NodeKind.Script, Path: { } path } => Path.GetDirectoryName(path),
            { Path: { } path } => path,
            _ => null,
        };

        var editor = new ScriptEditorViewModel(Settings.Roots, folder, Settings, TagFilters.Select(t => t.Name).ToList());
        if (file is not null)
            _ = editor.LoadFileAsync(file);
        ShowEditor(editor);
    }

    /// <summary>Изменить название, описание, параметры и подключения скрипта.</summary>
    [RelayCommand]
    private void EditScript(TreeNodeViewModel? node)
    {
        var entry = node?.Script ?? _selectedNode?.Script;
        if (entry is null)
            return;
        if (entry.IsUnmanaged)
        {
            var editor = ScriptEditorViewModel.ForUnmanaged(entry, Settings, TagFilters.Select(t => t.Name).ToList());
            _ = editor.LoadFileAsync(entry.ScriptPath);
            ShowEditor(editor);
            return;
        }
        if (entry.Manifest is null)
        {
            Dialogs.Info("В манифесте ошибки — исправьте их в файле манифеста");
            return;
        }

        ShowEditor(new ScriptEditorViewModel(entry, Settings, TagFilters.Select(t => t.Name).ToList()));
    }

    /// <summary>Удалить скрипт с манифестом и пресетами (в корзину); под git удаление публикуется как любое изменение.</summary>
    [RelayCommand]
    private void DeleteScript(TreeNodeViewModel? node)
    {
        var entry = node?.Script ?? _selectedNode?.Script;
        if (entry is null)
            return;
        if (Runs.For(entry.ManifestPath).Any(r => r.IsRunning))
        {
            Dialogs.Info($"«{entry.Title}» сейчас выполняется — остановите запуск, затем удалите скрипт");
            return;
        }

        var files = ScriptRemover.Files(entry);
        var list = string.Join("\n", files.Select(f => "  " + f));
        if (!Dialogs.Confirm($"Удалить скрипт «{entry.Title}»?\n\nФайлы уйдут в корзину:\n{list}\n\n"
                + "Если папка под git, удаление нужно опубликовать («Опубликовать…»). Логи в .output остаются."))
            return;

        try
        {
            ScriptRemover.Delete(entry, FileLauncher.MoveToRecycleBin);
            Status = $"Удалён: {entry.Title}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Часть файлов могла удалиться — дерево всё равно перечитываем.
            Status = $"Не удалось удалить: {entry.Title}";
            Dialogs.Info($"Не удалось удалить: {ex.Message}");
        }

        _formStates.Remove(entry.ManifestPath);
        if (string.Equals(SelectedScript?.ManifestPath, entry.ManifestPath, StringComparison.OrdinalIgnoreCase))
            SelectedScript = null;
        Reload(fetch: false);
    }

    private void ShowEditor(ScriptEditorViewModel editor)
    {
        if (EditScriptDialog?.Invoke(editor) is not { } saved)
            return;

        // Значения формы прежней версии манифеста могут не подойти новым параметрам.
        _formStates.Remove(saved);
        if (string.Equals(SelectedScript?.ManifestPath, saved, StringComparison.OrdinalIgnoreCase))
            SelectedScript = null;
        _pendingSelection = saved;
        Status = $"Сохранено: {saved}";
        Reload(fetch: false);
    }

    private async Task UpdateGitAsync(bool fetch)
    {
        _gitUpdate?.Cancel();
        var cts = _gitUpdate = new CancellationTokenSource();
        var ct = cts.Token;

        foreach (var (root, _) in _roots)
        {
            try
            {
                if (await GitRepository.OpenAsync(root.Path, ct) is not { } repo)
                {
                    _repos.Remove(root.Path);
                    _statuses.Remove(root.Path);
                    continue;
                }

                _repos[root.Path] = repo;
                _statuses[root.Path] = await repo.StatusAsync(ct);
                _gitErrors.Remove(root.Path);
                ApplyGit();

                if (fetch && await repo.HasRemoteAsync(ct))
                {
                    await repo.FetchAsync(ct);
                    _statuses[root.Path] = await repo.StatusAsync(ct);
                    ApplyGit();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (GitException ex)
            {
                // Например, сервер недоступен: локальный статус оставляем, ошибку показываем в подсказке.
                _gitErrors[root.Path] = ex.Message;
            }

            ApplyGit();
        }
    }

    /// <summary>Отметки git в дереве по последним статусам.</summary>
    private void ApplyGit()
    {
        foreach (var root in Nodes)
        {
            if (!_statuses.TryGetValue(root.RootPath, out var status))
            {
                root.IsGitRepo = false;
                root.GitSummary = null;
                continue;
            }

            var changed = status.Files.Keys.Count(f => IsUnder(f, root.RootPath));
            var parts = new List<string> { status.Branch ?? "?" };
            if (status.Behind > 0)
                parts.Add($"↓{status.Behind} на сервере");
            if (status.Ahead > 0)
                parts.Add($"↑{status.Ahead} не отправлено");
            if (changed > 0)
                parts.Add($"изменений: {changed}");
            if (_gitErrors.TryGetValue(root.RootPath, out var error))
                parts.Add("⚠ " + error.Split('\n')[0]);

            root.IsGitRepo = true;
            root.GitSummary = string.Join(" · ", parts);
            foreach (var node in Descendants(root).Where(n => n.Script is not null))
            {
                var entry = node.Script!;
                // Сначала файл скрипта (новый скрипт — «+»), затем манифест, пресеты и описание.
                node.GitState = new[] { entry.ScriptPath, entry.ManifestPath, entry.PresetsPath, entry.DescriptionPath }
                    .Select(p => status.Files.TryGetValue(p, out var s) ? s : (GitFileState?)null)
                    .FirstOrDefault(s => s is not null);
            }
        }
    }

    private void BuildTree()
    {
        var selectedPath = _pendingSelection ?? SelectedScript?.ManifestPath;
        _pendingSelection = null;
        var search = SearchText.Trim();
        var tags = TagFilters.Where(t => t.IsChecked).Select(t => t.Name).ToList();

        Nodes.Clear();
        foreach (var (root, folder) in _roots)
        {
            var others = Settings.Roots.Where(r => !ReferenceEquals(r, root)).ToList();
            var node = BuildNode(folder, NodeKind.Root, root.Path, others, search, tags, selectedPath);
            Nodes.Add(node ?? TreeNodeViewModel.FromFolder(folder, NodeKind.Root, this, root.Path));
        }

        ApplyGit();
        ApplyRunMarks();
    }

    private TreeNodeViewModel? BuildNode(ScriptFolder folder, NodeKind kind, string rootPath, IReadOnlyList<ScriptRootSettings> others,
        string search, IReadOnlyList<string> tags, string? selectedPath)
    {
        var node = TreeNodeViewModel.FromFolder(folder, kind, this, rootPath);
        foreach (var child in folder.Folders)
        {
            if (BuildNode(child, NodeKind.Folder, rootPath, others, search, tags, selectedPath) is { } childNode)
                node.Children.Add(childNode);
        }

        foreach (var script in folder.Scripts)
        {
            var scriptNode = TreeNodeViewModel.FromScript(script, this, rootPath, others);
            if (search.Length > 0 && !scriptNode.Matches(search) || !scriptNode.HasTags(tags))
                continue;
            // Восстанавливаем выбор после обновления дерева.
            scriptNode.IsSelected = string.Equals(script.ManifestPath, selectedPath, StringComparison.OrdinalIgnoreCase);
            node.Children.Add(scriptNode);
        }

        return node.Children.Count > 0 || folder.Error is not null || search.Length == 0 && tags.Count == 0 ? node : null;
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TreeNodeViewModel> Descendants(TreeNodeViewModel node) =>
        node.Children.SelectMany(c => Descendants(c).Prepend(c));

    private static IEnumerable<ScriptEntry> Flatten(ScriptFolder folder) =>
        folder.Scripts.Concat(folder.Folders.SelectMany(Flatten));
}

/// <summary>Тег в фильтре над деревом.</summary>
public sealed partial class TagFilterViewModel(string name, Action changed) : ObservableObject
{
    private bool _silent;

    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Без перестроения дерева.</summary>
    public void SetChecked(bool value)
    {
        _silent = true;
        IsChecked = value;
        _silent = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_silent)
            changed();
    }
}
