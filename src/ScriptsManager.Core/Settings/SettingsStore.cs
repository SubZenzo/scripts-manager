using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptsManager.Core.Settings;

public sealed class AppSettings
{
    public List<ScriptRootSettings> Roots { get; set; } = [];

    /// <summary>Интерпретатор Python; пусто — <c>python</c> из PATH.</summary>
    public string? PythonPath { get; set; }

    /// <summary>PowerShell; пусто — Windows PowerShell (<c>powershell.exe</c>).</summary>
    public string? PowerShellPath { get; set; }
}

public sealed class ScriptRootSettings
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class SettingsStore(string filePath)
{
    // Кириллица без \u-экранирования, чтобы файлы можно было читать и править руками.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScriptsManager");

    public static SettingsStore Default { get; } = new(Path.Combine(DefaultDirectory, "settings.json"));

    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
            return new AppSettings();

        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
