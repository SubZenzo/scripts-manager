namespace ScriptsManager.Core.Catalog;

/// <summary>
/// Копирование скрипта (файл скрипта, манифест, пресеты и описание) в другую папку со скриптами с тем же относительным путём.
/// </summary>
public static class ScriptCopier
{
    public static (string Manifest, string Script, string Presets) TargetPaths(ScriptEntry entry, string sourceRoot, string targetRoot)
    {
        if (entry.Manifest is null)
            throw new InvalidOperationException("Скрипт без манифеста или с ошибками в манифесте не копируется");
        var script = Path.GetFullPath(Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, entry.ScriptPath)));
        return (ScriptLayout.ManifestPath(targetRoot, script), script, ScriptLayout.PresetsPath(targetRoot, script));
    }

    public static (string Manifest, string Script, string Presets) Copy(ScriptEntry entry, string sourceRoot, string targetRoot, bool overwrite)
    {
        var target = TargetPaths(entry, sourceRoot, targetRoot);
        if (!overwrite && (File.Exists(target.Manifest) || File.Exists(target.Script)))
            throw new IOException($"В папке уже есть {Path.GetFileName(target.Script)}");

        Copy(entry.ScriptPath, target.Script);
        Copy(entry.ManifestPath, target.Manifest);
        if (File.Exists(entry.PresetsPath))
            Copy(entry.PresetsPath, target.Presets);
        var description = ScriptLayout.DescriptionPath(target.Manifest);
        if (File.Exists(entry.DescriptionPath))
            Copy(entry.DescriptionPath, description);
        else if (File.Exists(description))
            File.Delete(description);
        return target;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }
}
