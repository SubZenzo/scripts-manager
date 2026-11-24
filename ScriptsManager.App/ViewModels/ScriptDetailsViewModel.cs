using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.Core.Catalog;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Runs;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.App.ViewModels;

public sealed record ParameterRow(string Name, string Label, string Type, string Required, string Default, string Details, string? Help);

public sealed record ConnectionRow(string Name, string Type, string Scope, string Default);

public sealed record PresetRow(string Name, string Values);

public sealed partial class ScriptDetailsViewModel : ObservableObject
{
    private readonly ConnectionService _connections;
    private readonly RunRegistry _runs;

    public ScriptDetailsViewModel(ScriptEntry entry, ConnectionService connections, RunRegistry runs, FormState? state)
    {
        Entry = entry;
        _connections = connections;
        _runs = runs;
        _scriptRuns = runs.For(entry.ManifestPath);
        _currentRun = _scriptRuns.FirstOrDefault();
        _issues = entry.Result.Issues;
        ScriptPath = entry.ScriptPath;
        ScriptText = File.Exists(entry.ScriptPath) ? ReadText(entry.ScriptPath) : null;
        // Свои файлы пишут только PowerShell и Python.
        if (entry.OutputPath is { } output && (entry.Manifest?.Runner ?? entry.UnmanagedRunner) is RunnerKind.PowerShell or RunnerKind.Python)
            Output = new OutputFilesViewModel(output);
        if (entry.IsUnmanaged)
        {
            ManifestText = "";
            Summary = $"{entry.UnmanagedRunner?.Name()} · без манифеста";
            Syntax = entry.UnmanagedRunner?.Highlighting();
            return;
        }

        ManifestText = ReadText(entry.ManifestPath);

        var m = entry.Manifest;
        if (m is null)
            return;

        // Замечания к пресетам не мешают запуску: неподходящие значения просто не подставятся.
        var presets = PresetFile.Load(entry.PresetsPath);
        _issues = [.. _issues, .. presets.Issues, .. PresetFile.Validate(presets.Presets, m)];
        UpdatePresets(presets.Presets);

        if (!entry.Result.HasErrors)
        {
            Form = new ParameterFormViewModel(m, entry.ManifestPath, entry.PresetsPath, connections, state);
            Form.PresetsSaved += UpdatePresets;
        }
        // Вернулись к скрипту, который ещё выполняется или завершился без нас, — сразу показываем результат.
        if (CurrentRun is { } last && (last.IsRunning || !last.IsSeen))
        {
            _selectedTabIndex = ResultTabIndex;
            MarkSeen();
        }

        Description = m.Description;
        Summary = string.Join(" · ", new[]
        {
            m.Runner.Name(),
            m.Tags.Count > 0 ? string.Join(", ", m.Tags) : null,
            "вывод: " + string.Join(", ", m.Output.Select(o => YamlNames.Outputs.ToYaml(o))),
            m.Confirm ? "с подтверждением" : null,
            m.Rerun ? "с повторным запуском" : null,
        }.OfType<string>());

        Syntax = m.Runner.Highlighting();

        Parameters = m.Parameters.Select(p => new ParameterRow(
            p.Name, p.Label, YamlNames.ParameterTypes.ToYaml(p.Type), p.Required ? "да" : "нет",
            string.Join(", ", p.Default), Details(p), p.Help)).ToList();

        Connections = m.Connections.Select(c => new ConnectionRow(
            c.Name, c.Type.Name(), c.Scope == ConnectionScope.Server ? "сервер" : "сервер и БД",
            c.Default?.Name() ?? "")).ToList();
    }

    private readonly IReadOnlyList<ManifestIssue> _issues;

    private void UpdatePresets(IReadOnlyList<ScriptPreset> presets) =>
        Presets = presets.Select(p => new PresetRow(p.Name, string.Join("; ",
            p.Values.Select(v => $"{v.Key} = {string.Join(", ", v.Value)}")
                .Concat(p.Connections.Select(c => $"{c.Key} → {c.Value.Name()}"))))).ToList();

    public ScriptEntry Entry { get; }

    /// <summary>Файлы из папки вывода скрипта; null — у T-SQL и PL/pgSQL.</summary>
    public OutputFilesViewModel? Output { get; }

    /// <summary>Индекс вкладки «Вывод» в карточке.</summary>
    public const int OutputTabIndex = 2;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == OutputTabIndex)
            Output?.Refresh();
        MarkSeen();
    }

    /// <summary>Форма запуска; null — манифест с ошибками.</summary>
    public ParameterFormViewModel? Form { get; }

    public string Title => Entry.Title;
    public string? Description { get; }
    public string? Summary { get; }
    public IReadOnlyList<ManifestIssue> Issues => _issues;
    public bool HasIssues => Issues.Count > 0;
    public bool HasErrors => Entry.Result.HasErrors && !Entry.IsUnmanaged;

    /// <summary>Почему нет формы запуска.</summary>
    public string NoFormHint => Entry.IsUnmanaged
        ? "Чтобы запустить скрипт, нажмите «Изменить…» и сохраните: появится манифест с параметрами и подключениями"
        : "Исправьте ошибки в манифесте, чтобы запустить скрипт";

    public IReadOnlyList<ParameterRow> Parameters { get; } = [];
    public IReadOnlyList<ConnectionRow> Connections { get; } = [];
    [ObservableProperty]
    private IReadOnlyList<PresetRow> _presets = [];

    public string ManifestPath => Entry.ManifestPath;
    public string ManifestText { get; }
    public string? ScriptPath { get; }
    public string? ScriptText { get; }
    public string? Syntax { get; }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        var form = Form!;
        var resolved = form.Resolve(DateTime.Now);
        if (resolved is null)
        {
            form.Message = "Исправьте ошибки в подключениях и параметрах";
            return;
        }

        form.Message = null;
        var manifest = Entry.Manifest!;
        if (manifest.Confirm && !Dialogs.Confirm($"Запустить «{Title}»?\n\n{Describe(resolved.Connections.Select(c => (c.Key, c.Value.Display)), resolved.Values)}"))
            return;

        if (_runs.CreateRunner(manifest.Runner) is not { } runner)
        {
            form.Message = $"Запуск скриптов {manifest.Runner.Name()} пока не поддерживается";
            return;
        }

        foreach (var (slot, choice) in resolved.Connections)
        {
            try
            {
                _connections.Remember(ManifestPath, slot, choice);
            }
            catch (Exception ex)
            {
                form.Message = $"Не удалось запомнить подключение: {ex.Message}";
            }
        }

        Start(runner, new RunRequest(manifest, resolved.Values, resolved.Connections.ToDictionary(
            c => c.Key, c => new RunConnection(c.Value, _connections.GetPassword(c.Value.Connection))))
        {
            OutputDir = Output?.Folder,
        });
    }

    /// <summary>Повторный запуск разрешён манифестом (<c>rerun: true</c>).</summary>
    public bool CanRerun => Entry.Manifest?.Rerun == true && !Entry.Result.HasErrors;

    /// <summary>Запустить ещё раз с параметрами и подключениями выбранного запуска; его результат остаётся.</summary>
    [RelayCommand]
    private void Rerun(RunViewModel? previous)
    {
        if (!CanRerun || previous is not { IsRunning: false, Request: { } request })
            return;
        if (Entry.Manifest!.Confirm && !Dialogs.Confirm(
                $"Запустить «{Title}» ещё раз?\n\n{Describe(request.Connections.Select(c => (c.Key, c.Value.Choice.Display)), request.Values)}"))
            return;
        if (_runs.CreateRunner(request.Manifest.Runner) is { } runner)
            Start(runner, request);
    }

    private static string Describe(IEnumerable<(string Name, string Display)> connections, IReadOnlyDictionary<string, object?> values) =>
        string.Join("\n", connections.Select(c => $"{c.Name}: {c.Display}")
            .Concat(values.Select(v => $"{v.Key} = {Format(v.Value)}")));

    private void Start(IScriptRunner runner, RunRequest request)
    {
        var run = new RunViewModel(ManifestPath, Title, string.Join(", ", request.Connections.Values.Select(c => c.Choice.Display)));
        if (request.Values.Count > 0)
            run.Log(LogKind.Info, "Параметры: " + string.Join(", ", request.Values.Select(v => $"{v.Key} = {Format(v.Value)}")));

        run.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunViewModel.IsRunning) && !run.IsRunning)
                Output?.Refresh();
        };
        // Тот же скрипт можно запустить ещё раз, не дожидаясь первого (например, на другом сервере).
        _runs.Add(run);
        RunsChanged();
        CurrentRun = run;
        SelectedTabIndex = ResultTabIndex;
        _ = run.RunAsync(runner, request);
    }

    private bool CanRun() => Form is not null;

    /// <summary>Запуски скрипта за сеанс, новые сверху (сохраняются при переходе к другим скриптам).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeveralRuns))]
    private IReadOnlyList<RunViewModel> _scriptRuns = [];

    public bool HasSeveralRuns => ScriptRuns.Count > 1;

    /// <summary>Запуск, показанный на вкладке «Результат».</summary>
    [ObservableProperty]
    private RunViewModel? _currentRun;

    partial void OnCurrentRunChanged(RunViewModel? value) => MarkSeen();

    /// <summary>Список запусков поменялся (добавили, убрали завершённые).</summary>
    public void RunsChanged()
    {
        ScriptRuns = _runs.For(ManifestPath);
        if (CurrentRun is null || !ScriptRuns.Contains(CurrentRun))
            CurrentRun = ScriptRuns.FirstOrDefault();
    }

    /// <summary>Завершённый запуск на открытой вкладке «Результат» — посмотрели.</summary>
    public void MarkSeen()
    {
        if (SelectedTabIndex == ResultTabIndex && CurrentRun is { IsRunning: false, IsSeen: false } run)
            run.IsSeen = true;
    }

    /// <summary>Индекс вкладки «Результат» в карточке.</summary>
    public const int ResultTabIndex = 1;

    [ObservableProperty]
    private int _selectedTabIndex;

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{s}'",
        string[] a => "[" + string.Join(", ", a.Select(Format)) + "]",
        long[] a => "[" + string.Join(", ", a) + "]",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "",
    };

    [RelayCommand(CanExecute = nameof(CanOpenScript))]
    private void OpenScript() => FileLauncher.OpenForEdit(ScriptPath!);

    private bool CanOpenScript() => ScriptText is not null;

    [RelayCommand(CanExecute = nameof(CanOpenManifest))]
    private void OpenManifest() => FileLauncher.OpenForEdit(ManifestPath);

    private bool CanOpenManifest() => !Entry.IsUnmanaged;

    [RelayCommand]
    private void ShowInFolder() => FileLauncher.ShowInExplorer(Entry.ScriptPath);

    private static string Details(ScriptParameter p)
    {
        var parts = new List<string>();
        if (p.Items.Count > 0)
            parts.Add(string.Join(", ", p.Items.Select(i => i.Key == i.Value ? i.Value : $"{i.Value} ({i.Key})")));
        if (p.Min is not null || p.Max is not null)
            parts.Add($"{p.Min?.ToString() ?? "…"} – {p.Max?.ToString() ?? "…"}");
        if (p.Multi)
            parts.Add("несколько значений");
        if (p.Editable)
            parts.Add("можно своё значение");
        if (p.Multiline && p.Type == ParameterType.String)
            parts.Add("многострочный");
        if (p.Filter is not null)
            parts.Add(p.Filter);
        return string.Join("; ", parts);
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Не удалось прочитать файл: {ex.Message}";
        }
    }
}
