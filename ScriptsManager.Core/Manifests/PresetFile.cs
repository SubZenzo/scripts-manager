using System.Globalization;
using System.Text;
using ScriptsManager.Core.Authoring;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ScriptsManager.Core.Manifests;

/// <summary>Пресеты из файла и замечания к нему.</summary>
public sealed record PresetFileResult(IReadOnlyList<ScriptPreset> Presets, IReadOnlyList<ManifestIssue> Issues);

/// <summary>
/// Файл пресетов скрипта (<c>.presets/…/Имя.sql.yaml</c>) — YAML-список:
/// <code>
/// - name: Роуминг
///   values: { queue: DelayedMessageQueue, stands: [dev, stage, prod] }
///   connections: { connection: { server: myserver, database: mydatabase } }
/// </code>
/// Значения — как в форме (строки, даты могут быть выражениями); пустой список — очистить поле.
/// </summary>
public static class PresetFile
{
    public static PresetFileResult Load(string path)
    {
        if (!File.Exists(path))
            return new([], []);
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new([], [new(IssueSeverity.Error, $"Не удалось прочитать пресеты: {ex.Message}")]);
        }
    }

    /// <summary>Пишет пресеты; пустой список — файл удаляется.</summary>
    public static void Save(string path, IReadOnlyList<ScriptPreset> presets)
    {
        if (presets.Count == 0)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Write(presets), new UTF8Encoding(false));
    }

    /// <summary>Разбор без сверки с манифестом (для неё — <see cref="Validate"/>).</summary>
    public static PresetFileResult Parse(string yaml)
    {
        var issues = new List<ManifestIssue>();
        var presets = new List<ScriptPreset>();
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            issues.Add(new(IssueSeverity.Error, $"Ошибка YAML в пресетах: {(ex.InnerException ?? ex).Message}", (int)ex.Start.Line));
            return new(presets, issues);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
            return new(presets, issues);
        if (stream.Documents[0].RootNode is not YamlSequenceNode list)
        {
            issues.Add(new(IssueSeverity.Error, "Файл пресетов должен быть YAML-списком (- name: …)"));
            return new(presets, issues);
        }

        foreach (var item in list)
        {
            var line = (int)item.Start.Line;
            if (item is not YamlMappingNode map || Child(map, "name") is not YamlScalarNode { Value: { Length: > 0 } name })
            {
                issues.Add(new(IssueSeverity.Error, "У пресета нет name", line));
                continue;
            }

            var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (Child(map, "values") is YamlMappingNode valueMap)
            {
                foreach (var (k, v) in valueMap.Children)
                {
                    values[(k as YamlScalarNode)?.Value ?? ""] = v switch
                    {
                        YamlSequenceNode seq => seq.OfType<YamlScalarNode>().Select(s => s.Value ?? "").ToList(),
                        YamlScalarNode { Value: null } => [],
                        YamlScalarNode s => [s.Value!],
                        _ => [],
                    };
                }
            }

            var connections = new Dictionary<string, ConnectionTarget>(StringComparer.OrdinalIgnoreCase);
            if (Child(map, "connections") is YamlMappingNode connectionMap)
            {
                foreach (var (k, v) in connectionMap.Children)
                {
                    var slot = (k as YamlScalarNode)?.Value ?? "";
                    if (v is YamlMappingNode t && Scalar(t, "server") is { Length: > 0 } server)
                    {
                        int? port = int.TryParse(Scalar(t, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
                        connections[slot] = new ConnectionTarget(server, port, Scalar(t, "database"));
                    }
                    else
                    {
                        issues.Add(new(IssueSeverity.Error, $"Пресет «{name}»: подключение «{slot}» задаётся объектом {{ server, port, database }}", line));
                    }
                }
            }

            if (presets.Any(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
                issues.Add(new(IssueSeverity.Warning, $"Пресет «{name}» указан несколько раз", line));
            presets.Add(new ScriptPreset(name) { Values = values, Connections = connections });
        }

        return new(presets, issues);
    }

    /// <summary>Сверка с манифестом: значения неизвестных параметров и подключений не применяются.</summary>
    public static IReadOnlyList<ManifestIssue> Validate(IReadOnlyList<ScriptPreset> presets, ScriptManifest manifest)
    {
        var issues = new List<ManifestIssue>();
        foreach (var preset in presets)
        {
            foreach (var (name, values) in preset.Values)
            {
                var p = manifest.Parameters.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (p is null)
                    issues.Add(new(IssueSeverity.Warning, $"Пресет «{preset.Name}»: нет параметра «{name}»"));
                else if (ParameterValues.ValidateField(p with { Required = false }, values) is { } error)
                    issues.Add(new(IssueSeverity.Warning, $"Пресет «{preset.Name}», {name}: {error}"));
            }

            foreach (var slot in preset.Connections.Keys.Where(s => !manifest.Connections.Any(c => c.Name.Equals(s, StringComparison.OrdinalIgnoreCase))))
                issues.Add(new(IssueSeverity.Warning, $"Пресет «{preset.Name}»: нет подключения «{slot}»"));
        }

        return issues;
    }

    public static string Write(IEnumerable<ScriptPreset> presets)
    {
        var sb = new StringBuilder();
        foreach (var preset in presets)
        {
            sb.Append("- name: ").Append(ManifestWriter.Q(preset.Name)).Append('\n');
            if (preset.Values.Count > 0)
            {
                sb.Append("  values:\n");
                foreach (var (name, values) in preset.Values)
                    sb.Append("    ").Append(name).Append(':').Append(Value(values)).Append('\n');
            }
            if (preset.Connections.Count > 0)
            {
                sb.Append("  connections:\n");
                foreach (var (name, target) in preset.Connections)
                    sb.Append("    ").Append(name).Append(": ").Append(ManifestWriter.Target(target)).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>Одно значение — скаляр (многострочное — блоком <c>|-</c>, чтобы запросы читались), иначе список.</summary>
    private static string Value(IReadOnlyList<string> values)
    {
        if (values.Count != 1)
            return " [" + string.Join(", ", values.Select(v => ManifestWriter.Q(v, flow: true))) + "]";

        var value = values[0];
        if (!value.Contains('\n') || value.Contains('\r') || value.StartsWith(' ') || value.EndsWith('\n'))
            return " " + ManifestWriter.Q(value);

        return " |-\n" + string.Join("\n", value.Split('\n').Select(l => l.Length == 0 ? "" : "      " + l));
    }

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value?.Equals(key, StringComparison.OrdinalIgnoreCase) == true).Value;

    private static string? Scalar(YamlMappingNode map, string key) => (Child(map, key) as YamlScalarNode)?.Value;
}
