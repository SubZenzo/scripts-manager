using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptsManager.Core.Catalog;
using ScriptsManager.Core.Git;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.App.ViewModels;

public enum NodeKind { Root, Folder, Script }

/// <summary>Пункт меню «Копировать в»: куда скопировать скрипт.</summary>
public sealed record CopyTarget(TreeNodeViewModel Script, ScriptRootSettings Root)
{
    public string Name => Root.Name;
}

public sealed partial class TreeNodeViewModel : ObservableObject
{
    private TreeNodeViewModel(NodeKind kind, string name, MainViewModel owner, string rootPath)
    {
        Kind = kind;
        Name = name;
        Owner = owner;
        RootPath = rootPath;
    }

    public NodeKind Kind { get; }
    public string Name { get; }
    public string? Path { get; private init; }
    public ScriptEntry? Script { get; private init; }
    public string? Error { get; private init; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    /// <summary>Для команд контекстного меню (у ContextMenu нет доступа к DataContext окна).</summary>
    public MainViewModel Owner { get; }

    /// <summary>Папка со скриптами, в которой лежит узел.</summary>
    public string RootPath { get; }

    public IReadOnlyList<CopyTarget> CopyTargets { get; private set; } = [];
    public bool CanCopy => CopyTargets.Count > 0;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitMark), nameof(ToolTip))]
    private GitFileState? _gitState;

    /// <summary>Для корня: ветка, отставание и число изменений.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private string? _gitSummary;

    [ObservableProperty]
    private bool _isGitRepo;

    /// <summary>Точка у скрипта: выполняется (Running) или завершился, а результат ещё не смотрели; null — нечего показать.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunMarkText))]
    private RunState? _runMark;

    public string? RunMarkText => RunMark switch
    {
        RunState.Running => "Выполняется",
        RunState.Succeeded => "Выполнен — результат не просмотрен",
        RunState.Failed => "Ошибка — результат не просмотрен",
        RunState.Cancelled => "Остановлен — результат не просмотрен",
        _ => null,
    };

    // Segoe MDL2 Assets
    public string Glyph => Kind switch
    {
        NodeKind.Root => "",
        NodeKind.Folder => "",
        _ => "",
    };

    public string? Badge => (Script?.Manifest?.Runner ?? Script?.UnmanagedRunner)?.Badge();

    /// <summary>Скрипт без манифеста: показывается серым, манифест создаётся при правке.</summary>
    public bool IsUnmanaged => Script?.IsUnmanaged == true;
    public bool HasErrors => Error is not null || Script?.Result.HasErrors == true && !IsUnmanaged;
    public bool HasWarnings => !HasErrors && !IsUnmanaged && Script?.Result.Issues.Count > 0;

    public string? GitMark => GitState switch
    {
        GitFileState.Untracked or GitFileState.Added => "+",
        GitFileState.Deleted => "−",
        GitFileState.Conflict => "!",
        null => null,
        _ => "●",
    };

    private string? GitText => GitState switch
    {
        GitFileState.Untracked or GitFileState.Added => "Новый, не опубликован",
        GitFileState.Deleted => "Удалён, не опубликовано",
        GitFileState.Conflict => "Конфликт — разрешите в git",
        null => null,
        _ => "Изменён, не опубликован",
    };

    public string? ToolTip => string.Join("\n", new[]
    {
        Error ?? Script?.Manifest?.Description ?? (IsUnmanaged ? $"Нет манифеста: {Path}" : Path), GitText, GitSummary,
    }.OfType<string>());

    public static TreeNodeViewModel FromFolder(ScriptFolder folder, NodeKind kind, MainViewModel owner, string rootPath) =>
        new(kind, folder.Name, owner, rootPath) { Path = folder.Path, Error = folder.Error };

    public static TreeNodeViewModel FromScript(ScriptEntry entry, MainViewModel owner, string rootPath, IEnumerable<ScriptRootSettings> otherRoots)
    {
        var node = new TreeNodeViewModel(NodeKind.Script, entry.Title, owner, rootPath) { Path = entry.ScriptPath, Script = entry };
        if (entry.Manifest is not null)
            node.CopyTargets = otherRoots.Select(r => new CopyTarget(node, r)).ToList();
        return node;
    }

    public bool Matches(string search)
    {
        if (Script is null)
            return false;
        return Contains(Script.Title, search)
            || Contains(System.IO.Path.GetFileName(Script.ScriptPath), search)
            || Script.Manifest?.Tags.Any(t => Contains(t, search)) == true;
    }

    /// <summary>Есть ли у скрипта все теги.</summary>
    public bool HasTags(IReadOnlyList<string> tags) =>
        tags.Count == 0 || Script?.Manifest?.Tags is { } own
            && tags.All(t => own.Contains(t, StringComparer.CurrentCultureIgnoreCase));

    private static bool Contains(string text, string search) =>
        text.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}
