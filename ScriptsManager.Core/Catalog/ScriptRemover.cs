namespace ScriptsManager.Core.Catalog;

/// <summary>Удаление скрипта: файл скрипта, манифест и пресеты. Папка вывода (.output) с логами остаётся.</summary>
public static class ScriptRemover
{
    /// <summary>Файлы скрипта, которые есть на диске.</summary>
    public static IReadOnlyList<string> Files(ScriptEntry entry) =>
        new[] { entry.ScriptPath, entry.ManifestPath, entry.PresetsPath }.Where(File.Exists).ToList();

    /// <summary>
    /// Удаляет файлы скрипта (<paramref name="deleteFile"/> — например, в корзину) и опустевшие папки манифеста и пресетов
    /// внутри <c>.manifests</c> и <c>.presets</c>. Папка самого скрипта не удаляется, даже если опустела.
    /// </summary>
    public static void Delete(ScriptEntry entry, Action<string> deleteFile)
    {
        foreach (var file in Files(entry))
            deleteFile(file);

        RemoveEmptyFolders(entry.ManifestPath, ScriptLayout.ManifestsFolder);
        RemoveEmptyFolders(entry.PresetsPath, ScriptLayout.PresetsFolder);
    }

    // Вверх от папки файла, пока она пуста и лежит внутри служебной папки (саму .manifests/.presets не трогаем).
    private static void RemoveEmptyFolders(string file, string sideFolder)
    {
        var dir = Path.GetDirectoryName(file);
        while (dir is not null
               && !Path.GetFileName(dir).Equals(sideFolder, StringComparison.OrdinalIgnoreCase)
               && IsInside(dir, sideFolder)
               && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static bool IsInside(string dir, string sideFolder) =>
        dir.Split(Path.DirectorySeparatorChar).Contains(sideFolder, StringComparer.OrdinalIgnoreCase);
}
