using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.Core.Settings;
using Microsoft.Win32;

namespace ScriptsManager.App.ViewModels;

public sealed partial class RootRow : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _path = "";
}

public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings)
    {
        foreach (var r in settings.Roots)
            Roots.Add(new RootRow { Name = r.Name, Path = r.Path });
        PythonPath = settings.PythonPath ?? "";
        PowerShellPath = settings.PowerShellPath ?? "";
    }

    public ObservableCollection<RootRow> Roots { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRootCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private RootRow? _selectedRoot;

    [ObservableProperty]
    private string _pythonPath = "";

    [ObservableProperty]
    private string _powerShellPath = "";

    [RelayCommand]
    private void AddRoot()
    {
        var dialog = new OpenFolderDialog { Title = "Папка со скриптами" };
        if (dialog.ShowDialog() != true)
            return;

        var row = new RootRow { Name = Path.GetFileName(dialog.FolderName.TrimEnd('\\')), Path = dialog.FolderName };
        Roots.Add(row);
        SelectedRoot = row;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveRoot() => Roots.Remove(SelectedRoot!);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown() => Move(1);

    private bool HasSelection() => SelectedRoot is not null;

    private void Move(int delta)
    {
        var row = SelectedRoot!;
        var index = Roots.IndexOf(row) + delta;
        if (index < 0 || index >= Roots.Count)
            return;
        Roots.Move(index - delta, index);
        SelectedRoot = row;
    }

    /// <summary>Ошибка заполнения или null.</summary>
    public string? Validate() =>
        Roots.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Path)) is not null
            ? "У каждой папки должны быть имя и путь"
            : null;

    public AppSettings ToSettings() => new()
    {
        Roots = Roots.Select(r => new ScriptRootSettings { Name = r.Name.Trim(), Path = r.Path.Trim() }).ToList(),
        PythonPath = string.IsNullOrWhiteSpace(PythonPath) ? null : PythonPath.Trim(),
        PowerShellPath = string.IsNullOrWhiteSpace(PowerShellPath) ? null : PowerShellPath.Trim(),
    };
}
