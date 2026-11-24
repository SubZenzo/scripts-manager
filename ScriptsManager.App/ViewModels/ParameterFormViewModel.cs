using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.App.ViewModels;

public sealed record PresetItem(ScriptPreset Preset)
{
    public string Display => Preset.Name;
}

/// <summary>Состояние формы: значения параметров строками и выбранные подключения.</summary>
public sealed class FormState
{
    public Dictionary<string, IReadOnlyList<string>> Values { get; } = [];
    public Dictionary<string, ConnectionChoice> Connections { get; } = [];
}

/// <summary>Проверенная форма: значения для скрипта и подключения по слотам.</summary>
public sealed record ResolvedForm(
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyDictionary<string, ConnectionChoice> Connections);

public sealed partial class ParameterFormViewModel : ObservableObject
{
    private readonly string _manifestPath;
    private readonly string _presetsPath;
    private bool _applyingPreset;

    /// <param name="presetsPath">Файл пресетов скрипта (в .presets папки со скриптами).</param>
    public ParameterFormViewModel(ScriptManifest manifest, string manifestPath, string presetsPath,
        ConnectionService connections, FormState? state)
    {
        _manifestPath = manifestPath;
        _presetsPath = presetsPath;
        Fields = manifest.Parameters.Select(p => new ParameterFieldViewModel(p)).ToList();
        Connections = manifest.Connections.Select(s => new ConnectionSlotViewModel(s, connections)).ToList();

        // Подключение: выбранное в этой сессии, затем последнее использованное для скрипта, затем из манифеста.
        foreach (var slot in Connections)
        {
            var choice = state?.Connections.GetValueOrDefault(slot.Name) ?? LastChoice(connections, slot.Name);
            if (choice is not null)
                slot.SetChoice(choice);
            else if (slot.Slot.Default is { } target)
                slot.SetTarget(target);
        }

        if (state is not null)
            SetValues(state.Values);
        LoadPresets();
    }

    public IReadOnlyList<ParameterFieldViewModel> Fields { get; }
    public IReadOnlyList<ConnectionSlotViewModel> Connections { get; }
    public bool HasFields => Fields.Count > 0;
    public bool HasConnections => Connections.Count > 0;
    public ObservableCollection<PresetItem> Presets { get; } = [];

    /// <summary>Пресеты сохранены или удалены (новый список).</summary>
    public event Action<IReadOnlyList<ScriptPreset>>? PresetsSaved;

    /// <summary>Подсказка в пустом поле пресета — чтобы было видно, что пресеты есть.</summary>
    public string PresetsHint => Presets.Count == 0 ? "пресетов нет" : $"выберите пресет ({Presets.Count})";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeletePresetCommand))]
    private PresetItem? _selectedPreset;

    [ObservableProperty]
    private string? _message;

    partial void OnSelectedPresetChanged(PresetItem? value)
    {
        if (value is null || _applyingPreset)
            return;
        ApplyDefaults();
        // Значения, не подходящие параметру (тип поменялся после сохранения пресета), не подставляются.
        SetValues(value.Preset.Values
            .Where(v => Fields.FirstOrDefault(f => f.Name.Equals(v.Key, StringComparison.OrdinalIgnoreCase)) is { } f
                        && ParameterValues.ValidateField(f.Parameter with { Required = false }, v.Value) is null)
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase));
        foreach (var slot in Connections)
        {
            if (value.Preset.Connections.TryGetValue(slot.Name, out var target))
                slot.SetTarget(target);
        }

        Message = null;
    }

    public FormState GetState()
    {
        var state = new FormState();
        foreach (var f in Fields)
            state.Values[f.Name] = f.GetValues();
        foreach (var slot in Connections)
        {
            if (slot.GetChoice() is { } choice)
                state.Connections[slot.Name] = choice;
        }

        return state;
    }

    /// <summary>Проверяет подключения и поля и вычисляет значения для скрипта; null — есть ошибки.</summary>
    public ResolvedForm? Resolve(DateTime now)
    {
        var valid = Connections.Select(c => c.Validate()).Concat(Fields.Select(f => f.Validate())).ToList().All(v => v);
        if (!valid)
            return null;
        return new ResolvedForm(
            Fields.ToDictionary(f => f.Name, f => ParameterValues.Resolve(f.Parameter, f.GetValues(), now)),
            Connections.ToDictionary(c => c.Name, c => c.GetChoice()!));
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        ApplyDefaults();
        SelectPreset(null);
        Message = null;
    }

    private void ApplyDefaults()
    {
        foreach (var f in Fields)
            f.SetValues(f.Parameter.Default);
        foreach (var slot in Connections)
        {
            if (slot.Slot.Default is { } target)
                slot.SetTarget(target);
        }
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = Dialogs.AskText("Сохранить пресет", "Имя пресета:", SelectedPreset?.Preset.Name ?? "");
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();

        if (Presets.Any(x => x.Preset.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
            && !Dialogs.Confirm($"Пресет «{name}» уже есть. Заменить?"))
            return;

        var state = GetState();
        var preset = new ScriptPreset(name)
        {
            Values = state.Values.ToDictionary(v => v.Key, v => (IReadOnlyList<string>)v.Value.Where(x => !string.IsNullOrWhiteSpace(x)).ToList()),
            Connections = state.Connections.ToDictionary(c => c.Key,
                c => new ConnectionTarget(c.Value.Connection.Server, c.Value.Connection.Port, c.Value.Database)),
        };

        var presets = Presets.Select(p => p.Preset).ToList();
        var index = presets.FindIndex(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (index >= 0)
            presets[index] = preset;
        else
            presets.Add(preset);
        if (!WritePresets(presets, "сохранить"))
            return;

        SelectPreset(Presets.FirstOrDefault(x => x.Preset.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)));
        Message = $"Пресет «{name}» сохранён в {Path.GetFileName(_presetsPath)}";
    }

    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private void DeletePreset()
    {
        var preset = SelectedPreset!.Preset;
        if (!Dialogs.Confirm($"Удалить пресет «{preset.Name}»?"))
            return;

        if (!WritePresets(Presets.Select(p => p.Preset).Where(p => p != preset).ToList(), "удалить"))
            return;
        Message = $"Пресет «{preset.Name}» удалён";
    }

    private bool CanDeletePreset() => SelectedPreset is not null;

    /// <summary>
    /// Перечитывает файл пресетов перед записью: пресеты, добавленные в файл после открытия формы (git pull, правка
    /// вручную), не теряются. В файл попадает <paramref name="presets"/> плюс такие новые пресеты.
    /// </summary>
    private bool WritePresets(List<ScriptPreset> presets, string action)
    {
        try
        {
            var shown = Presets.Select(p => p.Preset.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            presets.AddRange(PresetFile.Load(_presetsPath).Presets.Where(p => !shown.Contains(p.Name)
                && !presets.Any(x => x.Name.Equals(p.Name, StringComparison.CurrentCultureIgnoreCase))));
            PresetFile.Save(_presetsPath, presets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Не удалось {action} пресет: {ex.Message}";
            return false;
        }

        LoadPresets();
        PresetsSaved?.Invoke(Presets.Select(p => p.Preset).ToList());
        return true;
    }

    private void SetValues(IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        foreach (var f in Fields)
        {
            if (values.TryGetValue(f.Name, out var v))
                f.SetValues(v);
        }
    }

    private void SelectPreset(PresetItem? preset)
    {
        _applyingPreset = true;
        SelectedPreset = preset;
        _applyingPreset = false;
    }

    private ConnectionChoice? LastChoice(ConnectionService connections, string slot)
    {
        try
        {
            return connections.Store.GetLastChoice(_manifestPath, slot);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void LoadPresets()
    {
        Presets.Clear();
        var loaded = PresetFile.Load(_presetsPath);
        foreach (var p in loaded.Presets)
            Presets.Add(new PresetItem(p));
        if (loaded.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error) is { } error)
            Message = $"Пресеты ({_presetsPath}): {error}";

        OnPropertyChanged(nameof(PresetsHint));
    }
}
