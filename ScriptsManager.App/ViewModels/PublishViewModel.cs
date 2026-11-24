using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptsManager.Core.Git;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.App.ViewModels;

/// <summary>
/// Строка окна публикации: скрипт (файл скрипта и манифест вместе), отдельный пресет скрипта или другой файл.
/// </summary>
public sealed partial class PublishItem : ObservableObject
{
    private PublishItem(string text, string stateText, bool isChecked)
    {
        Text = text;
        StateText = stateText;
        _isChecked = isChecked;
    }

    public string Text { get; }
    public string StateText { get; }

    /// <summary>Файлы, которые публикуются целиком.</summary>
    public IReadOnlyList<string> Paths { get; private init; } = [];

    /// <summary>Для пресета: файл пресетов и имя пресета в нём.</summary>
    public string? PresetsFile { get; private init; }
    public string? PresetName { get; private init; }

    public Thickness Margin => PresetName is null ? new Thickness(0) : new Thickness(20, 0, 0, 0);

    /// <summary>Заголовок без флажка: скрипт не менялся, менялись только его пресеты.</summary>
    public bool IsHeader { get; private init; }

    [ObservableProperty]
    private bool _isChecked;

    public static PublishItem Files(string text, GitFileState state, IReadOnlyList<string> paths) =>
        new(text, StateName(state), state != GitFileState.Conflict) { Paths = paths };

    public static PublishItem Header(string text) => new(text, "", false) { IsHeader = true };

    /// <summary>Пресеты по умолчанию не отмечены: публиковать их или нет, решает пользователь.</summary>
    public static PublishItem Preset(string presetsFile, PresetChange change) =>
        new($"пресет «{change.Name}»", change.Kind switch
        {
            PresetChangeKind.Added => "новый",
            PresetChangeKind.Deleted => "удалён",
            _ => "изменён",
        }, false)
        { PresetsFile = presetsFile, PresetName = change.Name };

    private static string StateName(GitFileState state) => state switch
    {
        GitFileState.Untracked or GitFileState.Added => "новый",
        GitFileState.Deleted => "удалён",
        GitFileState.Renamed => "переименован",
        GitFileState.Conflict => "конфликт",
        _ => "изменён",
    };
}

/// <summary>Окно публикации: что закоммитить и с каким сообщением.</summary>
public sealed partial class PublishViewModel(string rootName, string? branch, IReadOnlyList<PublishItem> items) : ObservableObject
{
    public string Title => $"Публикация: {rootName}" + (branch is null ? "" : $" (ветка {branch})");
    public IReadOnlyList<PublishItem> Items { get; } = items;

    [ObservableProperty]
    private string _message = "";

    /// <summary>Файлы, публикуемые целиком.</summary>
    public IReadOnlyCollection<string> SelectedFiles => Items.Where(i => i.IsChecked).SelectMany(i => i.Paths).ToList();

    /// <summary>Отмеченные пресеты по файлам пресетов.</summary>
    public IReadOnlyDictionary<string, List<string>> SelectedPresets =>
        Items.Where(i => i.IsChecked && i.PresetsFile is not null)
            .GroupBy(i => i.PresetsFile!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(i => i.PresetName!).ToList(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Ошибка заполнения или null.</summary>
    public string? Validate() =>
        !Items.Any(i => i.IsChecked) ? "Отметьте, что публиковать"
        : Message.Trim().Length == 0 ? "Напишите, что изменилось"
        : null;
}
