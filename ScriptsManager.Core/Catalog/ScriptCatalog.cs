using ScriptsManager.Core.Authoring;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.Core.Catalog;

public sealed class ScriptFolder(string name, string path)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public List<ScriptFolder> Folders { get; } = [];
    public List<ScriptEntry> Scripts { get; } = [];

    /// <summary>Ошибка чтения папки (для корня — например, папка не найдена).</summary>
    public string? Error { get; init; }
}

/// <summary>Скрипт папки: файл скрипта, его манифест и пресеты (см. <see cref="ScriptLayout"/>).</summary>
public sealed class ScriptEntry(string scriptPath, string manifestPath, string presetsPath, ManifestLoadResult result, string? outputPath = null)
{
    public string ScriptPath { get; } = scriptPath;
    public string ManifestPath { get; } = manifestPath;
    public string PresetsPath { get; } = presetsPath;

    /// <summary>Папка вывода скрипта (OUTPUT_DIR); null — неизвестна.</summary>
    public string? OutputPath { get; } = outputPath;
    public ManifestLoadResult Result { get; } = result;
    public ScriptManifest? Manifest => Result.Manifest;
    public string Title => Manifest?.Title ?? Path.GetFileNameWithoutExtension(ScriptPath);

    /// <summary>Манифеста нет: скрипт не запускается, манифест создаётся при правке.</summary>
    public bool IsUnmanaged { get; private init; }

    /// <summary>Язык скрипта без манифеста — по расширению (для .sql — по тексту).</summary>
    public RunnerKind? UnmanagedRunner { get; private init; }

    public static ScriptEntry Unmanaged(string scriptPath, string manifestPath, string presetsPath, RunnerKind runner, string? outputPath = null) =>
        new(scriptPath, manifestPath, presetsPath, new ManifestLoadResult(null,
            [new ManifestIssue(IssueSeverity.Warning,
                "Манифеста нет, скрипт нельзя запустить. «Изменить…» — задать параметры и подключения, манифест создастся при сохранении")]), outputPath)
        {
            IsUnmanaged = true,
            UnmanagedRunner = runner,
        };
}

public static class ScriptCatalog
{
    /// <summary>
    /// Загружает папку со скриптами: подпапки — категории, каждый файл .sql/.ps1/.py — скрипт, манифест — в .manifests.
    /// Манифест, у которого нет файла скрипта, тоже показывается — с ошибкой.
    /// </summary>
    public static ScriptFolder LoadRoot(ScriptRootSettings root)
    {
        if (!Directory.Exists(root.Path))
            return new ScriptFolder(root.Name, root.Path) { Error = $"Папка не найдена: {root.Path}" };

        var rootPath = Path.GetFullPath(root.Path);
        var folder = LoadFolder(rootPath, root.Name, rootPath);
        AddLostManifests(rootPath, folder);
        return folder;
    }

    private static ScriptFolder LoadFolder(string root, string name, string path)
    {
        try
        {
            var folder = new ScriptFolder(name, path);

            foreach (var dir in Directory.EnumerateDirectories(path).Order(StringComparer.OrdinalIgnoreCase))
            {
                // .manifests, .presets, .git, .vs и т.п.
                if (Path.GetFileName(dir).StartsWith('.'))
                    continue;

                var child = LoadFolder(root, Path.GetFileName(dir), dir);
                if (child.Folders.Count > 0 || child.Scripts.Count > 0 || child.Error is not null)
                    folder.Folders.Add(child);
            }

            folder.Scripts.AddRange(Directory.EnumerateFiles(path).Where(ScriptLayout.IsScript).Select(f => Entry(root, f)));
            Sort(folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ScriptFolder(name, path) { Error = ex.Message };
        }
    }

    private static ScriptEntry Entry(string root, string script)
    {
        var manifest = ScriptLayout.ManifestPath(root, script);
        var presets = ScriptLayout.PresetsPath(root, script);
        var output = ScriptLayout.OutputPath(root, script);
        return File.Exists(manifest)
            ? new ScriptEntry(script, manifest, presets, ManifestLoader.Load(manifest, script), output)
            : ScriptEntry.Unmanaged(script, manifest, presets, ScriptLayout.GuessRunner(script, ReadSafe(script)), output);
    }

    /// <summary>Манифесты, файл скрипта которых удалён или переименован, — в папку, где скрипт был.</summary>
    private static void AddLostManifests(string root, ScriptFolder top)
    {
        var manifests = Path.Combine(root, ScriptLayout.ManifestsFolder);
        if (!Directory.Exists(manifests))
            return;

        foreach (var manifest in Directory.EnumerateFiles(manifests, "*" + ScriptLayout.Extension, SearchOption.AllDirectories))
        {
            var script = ScriptLayout.ScriptPath(root, manifest);
            if (File.Exists(script) || !ScriptLayout.IsScript(script))
                continue;

            var folder = top;
            var relative = Path.GetRelativePath(root, Path.GetDirectoryName(script)!);
            foreach (var part in relative == "." ? Array.Empty<string>() : relative.Split(Path.DirectorySeparatorChar))
            {
                var child = folder.Folders.FirstOrDefault(f => f.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new ScriptFolder(part, Path.Combine(folder.Path, part));
                    folder.Folders.Add(child);
                    folder.Folders.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                }
                folder = child;
            }

            folder.Scripts.Add(new ScriptEntry(script, manifest, ScriptLayout.PresetsPath(root, script), ManifestLoader.Load(manifest, script),
                ScriptLayout.OutputPath(root, script)));
            Sort(folder);
        }
    }

    private static void Sort(ScriptFolder folder) =>
        folder.Scripts.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title));

    private static string? ReadSafe(string file)
    {
        try
        {
            return ScriptFiles.Read(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
