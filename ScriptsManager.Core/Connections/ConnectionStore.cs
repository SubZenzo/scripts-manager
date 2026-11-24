using System.Text.Json;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.Core.Connections;

/// <summary>
/// Недавние подключения и последний выбор подключения для слотов скриптов (без паролей).
/// </summary>
public sealed class ConnectionStore(string filePath)
{
    public const int MaxRecent = 30;

    public static ConnectionStore Default { get; } = new(Path.Combine(SettingsStore.DefaultDirectory, "connections.json"));

    public string FilePath { get; } = filePath;

    public IReadOnlyList<SavedConnection> GetRecent(DbKind type) =>
        Load().Recent.Where(c => c.Type == type).ToList();

    /// <summary>Поднимает подключение в начало списка недавних.</summary>
    public void Touch(SavedConnection connection)
    {
        var data = Load();
        data.Recent.RemoveAll(c => c.SameAs(connection));
        data.Recent.Insert(0, connection);
        if (data.Recent.Count > MaxRecent)
            data.Recent.RemoveRange(MaxRecent, data.Recent.Count - MaxRecent);
        Save(data);
    }

    public void Forget(SavedConnection connection)
    {
        var data = Load();
        data.Recent.RemoveAll(c => c.SameAs(connection));
        Save(data);
    }

    public ConnectionChoice? GetLastChoice(string manifestPath, string slot) =>
        Load().LastChoices.GetValueOrDefault(ChoiceKey(manifestPath, slot));

    public void SetLastChoice(string manifestPath, string slot, ConnectionChoice choice)
    {
        var data = Load();
        data.LastChoices[ChoiceKey(manifestPath, slot)] = choice;
        Save(data);
    }

    private static string ChoiceKey(string manifestPath, string slot) =>
        Path.GetFullPath(manifestPath) + "|" + slot;

    private Data Load()
    {
        if (!File.Exists(FilePath))
            return new Data();
        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), SettingsStore.JsonOptions) ?? new Data();
        return data with { LastChoices = new(data.LastChoices, StringComparer.OrdinalIgnoreCase) };
    }

    private void Save(Data data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, SettingsStore.JsonOptions));
    }

    private sealed record Data
    {
        public List<SavedConnection> Recent { get; init; } = [];
        public Dictionary<string, ConnectionChoice> LastChoices { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
