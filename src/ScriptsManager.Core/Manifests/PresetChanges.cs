namespace ScriptsManager.Core.Manifests;

public enum PresetChangeKind { Added, Changed, Deleted }

public sealed record PresetChange(string Name, PresetChangeKind Kind);

/// <summary>Изменения пресетов скрипта относительно опубликованной версии — чтобы публиковать их по одному.</summary>
public static class PresetChanges
{
    public static IReadOnlyList<PresetChange> Diff(IReadOnlyList<ScriptPreset> published, IReadOnlyList<ScriptPreset> current)
    {
        var result = new List<PresetChange>();
        foreach (var p in current)
        {
            var old = Find(published, p.Name);
            if (old is null)
                result.Add(new(p.Name, PresetChangeKind.Added));
            else if (Text(old) != Text(p))
                result.Add(new(p.Name, PresetChangeKind.Changed));
        }

        result.AddRange(published.Where(p => Find(current, p.Name) is null).Select(p => new PresetChange(p.Name, PresetChangeKind.Deleted)));
        return result;
    }

    /// <summary>Опубликованные пресеты с применёнными выбранными изменениями (остальные остаются неопубликованными).</summary>
    public static IReadOnlyList<ScriptPreset> Apply(IReadOnlyList<ScriptPreset> published, IReadOnlyList<ScriptPreset> current,
        IReadOnlyCollection<string> selected)
    {
        bool IsSelected(string name) => selected.Contains(name, StringComparer.CurrentCultureIgnoreCase);

        var result = published.Where(p => !IsSelected(p.Name) || Find(current, p.Name) is not null)
            .Select(p => IsSelected(p.Name) ? Find(current, p.Name)! : p)
            .ToList();
        result.AddRange(current.Where(p => IsSelected(p.Name) && Find(published, p.Name) is null));
        return result;
    }

    private static ScriptPreset? Find(IReadOnlyList<ScriptPreset> presets, string name) =>
        presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

    private static string Text(ScriptPreset preset) => PresetFile.Write([preset]);
}
