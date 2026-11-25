using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ScriptsManager.Core.Manifests;

public static class ManifestLoader
{
    public static readonly IReadOnlyList<string> Extensions = [".yaml", ".yml"];

    /// <param name="scriptPath">Файл скрипта, к которому относится манифест (см. ScriptLayout).</param>
    public static ManifestLoadResult Load(string manifestPath, string scriptPath)
    {
        string yaml;
        try
        {
            yaml = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, [new(IssueSeverity.Error, $"Не удалось прочитать манифест: {ex.Message}")]);
        }

        return Parse(yaml, scriptPath);
    }

    /// <param name="scriptPath">Файл скрипта, к которому относится манифест.</param>
    public static ManifestLoadResult Parse(string yaml, string scriptPath) =>
        new ManifestParser(Path.GetFullPath(scriptPath)).Parse(yaml);
}

internal sealed partial class ManifestParser(string scriptPath)
{
    private readonly List<ManifestIssue> _issues = [];

    public ManifestLoadResult Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            var message = YamlPositionRegex().Replace((ex.InnerException ?? ex).Message, "");
            _issues.Add(new(IssueSeverity.Error, $"Ошибка YAML: {message}", (int)ex.Start.Line));
            return Result(null);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            _issues.Add(new(IssueSeverity.Error, "Манифест должен быть YAML-объектом (title, runner, params, ...)"));
            return Result(null);
        }

        var f = Fields(root, "title", "description", "runner", "tags", "output", "confirm", "rerun", "connections", "params");

        var runner = Enum(f, "runner", YamlNames.Runners, root);
        if (runner is null)
            return Result(null);

        if (!File.Exists(scriptPath))
            Error($"Файл скрипта не найден: {scriptPath}", root);
        else if (!Path.GetExtension(scriptPath).Equals(runner.Value.FileExtension(), StringComparison.OrdinalIgnoreCase))
            Error($"Язык {YamlNames.Runners.ToYaml(runner.Value)} не подходит к файлу {Path.GetFileName(scriptPath)}", f["runner"]);

        var output = StringList(f, "output")
            .Select(o => (Name: o, Ok: YamlNames.Outputs.TryGetValue(o, out var kind), Kind: kind))
            .Where(o => o.Ok || Error($"Неизвестный вид вывода «{o.Name}», допустимо: {Allowed(YamlNames.Outputs)}", f["output"]))
            .Select(o => o.Kind)
            .Distinct()
            .ToList();

        var connections = ParseConnections(f.GetValueOrDefault("connections"));
        var parameters = ParseParameters(f.GetValueOrDefault("params"));

        foreach (var p in parameters.Where(p => connections.Any(c => c.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))))
            Error($"Имя «{p.Name}» занято и подключением, и параметром", f["params"]);

        var manifest = new ScriptManifest
        {
            Title = Scalar(f, "title") ?? Path.GetFileNameWithoutExtension(scriptPath),
            Description = Scalar(f, "description"),
            Runner = runner.Value,
            ScriptPath = scriptPath,
            Tags = StringList(f, "tags"),
            Output = output.Count > 0 ? output : [OutputKind.Grid, OutputKind.Log],
            Confirm = Bool(f, "confirm") ?? false,
            Rerun = Bool(f, "rerun") ?? false,
            Connections = connections,
            Parameters = parameters,
        };

        return Result(manifest);
    }

    private List<ConnectionSlot> ParseConnections(YamlNode? node)
    {
        var result = new List<ConnectionSlot>();
        foreach (var item in Sequence(node, "connections"))
        {
            var f = Fields(item, "name", "type", "scope", "default");
            var name = Name(f, item);
            var type = Enum(f, "type", YamlNames.DbKinds, item);
            var scope = f.ContainsKey("scope") ? Enum(f, "scope", YamlNames.Scopes, item) : ConnectionScope.Database;
            if (name is null || type is null || scope is null)
                continue;

            if (result.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                Error($"Подключение «{name}» указано несколько раз", item);

            var target = f.TryGetValue("default", out var d) ? Target(d, scope.Value) : null;
            result.Add(new ConnectionSlot(name, type.Value, scope.Value) { Default = target });
        }

        return result;
    }

    private List<ScriptParameter> ParseParameters(YamlNode? node)
    {
        var result = new List<ScriptParameter>();
        foreach (var item in Sequence(node, "params"))
        {
            var f = Fields(item, "name", "label", "type", "help", "required", "default", "min", "max",
                "multiline", "multi", "editable", "items", "filter");
            var name = Name(f, item);
            var type = f.ContainsKey("type") ? Enum(f, "type", YamlNames.ParameterTypes, item) : ParameterType.String;
            if (name is null || type is null)
                continue;

            if (result.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                Error($"Параметр «{name}» указан несколько раз", item);

            var p = new ScriptParameter(name, type.Value)
            {
                Label = Scalar(f, "label") ?? name,
                Help = Scalar(f, "help"),
                Required = Bool(f, "required") ?? true,
                Min = Decimal(f, "min"),
                Max = Decimal(f, "max"),
                Multiline = Bool(f, "multiline") ?? type == ParameterType.Text,
                Multi = Bool(f, "multi") ?? false,
                Editable = Bool(f, "editable") ?? false,
                Items = ParseItems(f.GetValueOrDefault("items")),
                Filter = Scalar(f, "filter"),
            };

            WarnIfNotApplicable(f, "min", p, ParameterType.Int, ParameterType.Decimal, ParameterType.Range);
            WarnIfNotApplicable(f, "max", p, ParameterType.Int, ParameterType.Decimal, ParameterType.Range);
            WarnIfNotApplicable(f, "multiline", p, ParameterType.String, ParameterType.Text);
            WarnIfNotApplicable(f, "multi", p, ParameterType.List);
            WarnIfNotApplicable(f, "editable", p, ParameterType.List);
            WarnIfNotApplicable(f, "items", p, ParameterType.List);
            WarnIfNotApplicable(f, "filter", p, ParameterType.File);

            if (p.Min > p.Max)
                Error($"Параметр «{name}»: min больше max", item);
            if (p.Type == ParameterType.List && p.Items.Count == 0 && !p.Editable)
                Error($"Параметр «{name}»: у списка нет items (или укажите editable: true)", item);

            if (f.TryGetValue("default", out var d))
                p = p with { Default = Values(d, p, "default") };

            result.Add(p);
        }

        return result;
    }

    private List<ListItem> ParseItems(YamlNode? node)
    {
        var result = new List<ListItem>();
        if (node is null)
            return result;
        if (node is not YamlSequenceNode seq)
        {
            Error("items должен быть списком", node);
            return result;
        }

        foreach (var child in seq)
        {
            ListItem? item = null;
            if (child is YamlMappingNode map)
            {
                var f = Fields(map, "key", "value");
                var key = Scalar(f, "key");
                if (key is null)
                    Error("У элемента списка нет key", map);
                else
                    item = new ListItem(key, Scalar(f, "value") ?? key);
            }
            else if (ScalarValue(child, "items") is { } v)
            {
                item = new ListItem(v, v);
            }

            if (item is null)
                continue;
            if (result.Any(i => i.Key == item.Key))
                Warning($"Элемент списка «{item.Key}» указан несколько раз", child);
            else
                result.Add(item);
        }

        return result;
    }

    private ConnectionTarget? Target(YamlNode node, ConnectionScope scope)
    {
        if (node is not YamlMappingNode map)
        {
            Error("Подключение задаётся объектом { server, port, database }", node);
            return null;
        }

        var f = Fields(map, "server", "port", "database");
        var server = Scalar(f, "server");
        if (server is null)
        {
            Error("Не указан server", map);
            return null;
        }

        int? port = null;
        if (Scalar(f, "port") is { } portText)
        {
            if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
                port = p;
            else
                Error($"«{portText}» — неверный порт", f["port"]);
        }

        var database = Scalar(f, "database");
        if (database is not null && scope == ConnectionScope.Server)
            Warning("database не используется при scope: server", f["database"]);

        return new ConnectionTarget(server, port, database);
    }

    private List<string> Values(YamlNode node, ScriptParameter p, string context)
    {
        var values = node is YamlSequenceNode seq
            ? seq.Select(n => ScalarValue(n, context)).OfType<string>().ToList()
            : ScalarValue(node, context) is { } v ? [v] : [];

        if (values.Count > 1 && !p.Multi)
            Error($"Параметр «{p.Name}» ({context}): несколько значений допустимо только у списка с multi: true", node);

        foreach (var value in values)
        {
            if (ParameterValues.Validate(p, value) is { } error)
                Error($"Параметр «{p.Name}» ({context}): {error}", node);
        }

        return values;
    }

    private void WarnIfNotApplicable(Dictionary<string, YamlNode> f, string field, ScriptParameter p, params ParameterType[] types)
    {
        if (f.TryGetValue(field, out var node) && !types.Contains(p.Type))
            Warning($"Параметр «{p.Name}»: {field} не используется для типа {YamlNames.ParameterTypes.ToYaml(p.Type)}", node);
    }

    private string? Name(Dictionary<string, YamlNode> f, YamlNode owner)
    {
        var name = Scalar(f, "name");
        if (name is null)
            Error("Не указано name", owner);
        else if (!IdentifierRegex().IsMatch(name))
            Error($"Имя «{name}» должно состоять из латиницы, цифр и _ и не начинаться с цифры", f["name"]);
        else
            return name;
        return null;
    }

    private T? Enum<T>(Dictionary<string, YamlNode> f, string field, IReadOnlyDictionary<string, T> names, YamlNode owner)
        where T : struct
    {
        var value = Scalar(f, field);
        if (value is null)
        {
            Error($"Не указано {field} ({Allowed(names)})", owner);
            return null;
        }

        if (names.TryGetValue(value, out var result))
            return result;

        Error($"Неизвестное значение {field}: «{value}», допустимо: {Allowed(names)}", f[field]);
        return null;
    }

    private Dictionary<string, YamlNode> Fields(YamlMappingNode node, params string[] known)
    {
        var result = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyNode, valueNode) in node.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            if (known.Contains(key, StringComparer.OrdinalIgnoreCase))
                result[key] = valueNode;
            else
                Warning($"Неизвестное поле «{key}»", keyNode);
        }

        return result;
    }

    private IEnumerable<YamlMappingNode> Sequence(YamlNode? node, string field)
    {
        if (node is null || IsNull(node))
            yield break;
        if (node is not YamlSequenceNode seq)
        {
            Error($"{field} должен быть списком", node);
            yield break;
        }

        foreach (var child in seq)
        {
            if (child is YamlMappingNode map)
                yield return map;
            else
                Error($"Элемент {field} должен быть объектом", child);
        }
    }

    private List<string> StringList(Dictionary<string, YamlNode> f, string field)
    {
        if (!f.TryGetValue(field, out var node) || IsNull(node))
            return [];
        if (node is YamlSequenceNode seq)
            return seq.Select(n => ScalarValue(n, field)).OfType<string>().ToList();
        return ScalarValue(node, field) is { } v ? [v] : [];
    }

    private string? Scalar(Dictionary<string, YamlNode> f, string field) =>
        f.TryGetValue(field, out var node) ? ScalarValue(node, field) : null;

    private string? ScalarValue(YamlNode node, string field)
    {
        if (node is YamlScalarNode s)
            return IsNull(s) ? null : s.Value;
        Error($"{field}: ожидается значение, а не список или объект", node);
        return null;
    }

    private bool? Bool(Dictionary<string, YamlNode> f, string field)
    {
        var value = Scalar(f, field);
        if (value is null)
            return null;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
        Error($"{field}: ожидается true или false, а не «{value}»", f[field]);
        return null;
    }

    private decimal? Decimal(Dictionary<string, YamlNode> f, string field)
    {
        var value = Scalar(f, field);
        if (value is null)
            return null;
        if (ParameterValues.TryParseDecimal(value, out var d))
            return d;
        Error($"{field}: «{value}» — не число", f[field]);
        return null;
    }

    private static bool IsNull(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } s
        && (string.IsNullOrEmpty(s.Value) || s.Value is "~" or "null" or "Null" or "NULL");

    private static string Allowed<T>(IReadOnlyDictionary<string, T> names) => string.Join(", ", names.Keys);

    private bool Error(string message, YamlNode node)
    {
        _issues.Add(new(IssueSeverity.Error, message, (int)node.Start.Line));
        return false;
    }

    private void Warning(string message, YamlNode node) =>
        _issues.Add(new(IssueSeverity.Warning, message, (int)node.Start.Line));

    private ManifestLoadResult Result(ScriptManifest? manifest) => new(manifest, _issues);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^\(Line: \d+, Col: \d+, Idx: \d+\) - \(Line: \d+, Col: \d+, Idx: \d+\):\s*")]
    private static partial Regex YamlPositionRegex();
}
