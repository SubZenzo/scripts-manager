using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Catalog;

/// <summary>
/// Раскладка папки со скриптами: скрипты — в папках как есть, манифест и пресеты скрипта
/// <c>Папка/Имя.sql</c> — в <c>.manifests/Папка/Имя.sql.yaml</c> и <c>.presets/Папка/Имя.sql.yaml</c>.
/// Расширение скрипта в имени, чтобы <c>Имя.ps1</c> и <c>Имя.py</c> не делили манифест.
/// </summary>
public static class ScriptLayout
{
    public const string ManifestsFolder = ".manifests";
    public const string PresetsFolder = ".presets";
    public const string Extension = ".yaml";

    /// <summary>Файлы, которые пишут скрипты (логи и т.п.); в git-статусе приложения не показываются.</summary>
    public const string OutputFolder = ".output";

    public static readonly IReadOnlySet<string> ScriptExtensions =
        new HashSet<string>([".sql", ".ps1", ".py"], StringComparer.OrdinalIgnoreCase);

    public static bool IsScript(string path) => ScriptExtensions.Contains(Path.GetExtension(path));

    public static string ManifestPath(string root, string scriptPath) => SidePath(root, ManifestsFolder, scriptPath);

    public static string PresetsPath(string root, string scriptPath) => SidePath(root, PresetsFolder, scriptPath);

    /// <summary>Папка вывода скрипта: <c>Папка/Имя.ps1</c> → <c>.output/Папка/Имя</c>.</summary>
    public static string OutputPath(string root, string scriptPath)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(scriptPath));
        return Path.Combine(fullRoot, OutputFolder, Path.ChangeExtension(relative, null));
    }

    /// <summary>Скрипт по пути манифеста или файла пресетов: <c>.manifests/Папка/Имя.sql.yaml</c> → <c>Папка/Имя.sql</c>.</summary>
    public static string ScriptPath(string root, string sidePath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(sidePath));
        var parts = relative.Split(Path.DirectorySeparatorChar, 2);
        if (parts.Length < 2 || parts[0] is not (ManifestsFolder or PresetsFolder))
            throw new ArgumentException($"Файл не в {ManifestsFolder} или {PresetsFolder}: {sidePath}", nameof(sidePath));
        return Path.Combine(Path.GetFullPath(root), parts[1][..^Extension.Length]);
    }

    /// <summary>Язык по расширению; для .sql — по тексту (T-SQL или PL/pgSQL).</summary>
    public static RunnerKind GuessRunner(string scriptPath, string? text) => Path.GetExtension(scriptPath).ToLowerInvariant() switch
    {
        ".ps1" => RunnerKind.PowerShell,
        ".py" => RunnerKind.Python,
        _ => text is not null && Authoring.ScriptAnalyzer.LooksLikePostgres(text) ? RunnerKind.PlPgSql : RunnerKind.TSql,
    };

    private static string SidePath(string root, string folder, string scriptPath)
    {
        var fullRoot = Path.GetFullPath(root);
        return Path.Combine(fullRoot, folder, Path.GetRelativePath(fullRoot, Path.GetFullPath(scriptPath)) + Extension);
    }
}
