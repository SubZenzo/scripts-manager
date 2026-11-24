using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Authoring;

/// <summary>Манифест в YAML в том же виде, что и написанные вручную: поля со значениями по умолчанию не пишутся.</summary>
public static partial class ManifestWriter
{
    public static string Write(ScriptManifest m)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');

        Line("title: " + Q(m.Title));
        if (!string.IsNullOrWhiteSpace(m.Description))
            Line("description: " + Q(m.Description));
        Line("runner: " + YamlNames.Runners.ToYaml(m.Runner));

        if (m.Tags.Count > 0)
            Line("tags: " + Flow(m.Tags));
        if (!m.Output.SequenceEqual([OutputKind.Grid, OutputKind.Log]))
            Line("output: " + Flow(m.Output.Select(o => YamlNames.Outputs.ToYaml(o))));
        if (m.Confirm)
            Line("confirm: true");
        if (m.Rerun)
            Line("rerun: true");

        if (m.Connections.Count > 0)
        {
            Line("connections:");
            foreach (var c in m.Connections)
            {
                Line("  - name: " + c.Name);
                Line("    type: " + YamlNames.DbKinds.ToYaml(c.Type));
                if (c.Scope != ConnectionScope.Database)
                    Line("    scope: " + YamlNames.Scopes.ToYaml(c.Scope));
                if (c.Default is { } d)
                    Line("    default: " + Target(d));
            }
        }

        if (m.Parameters.Count > 0)
        {
            Line("params:");
            foreach (var p in m.Parameters)
            {
                Line("  - name: " + p.Name);
                if (p.Label != p.Name)
                    Line("    label: " + Q(p.Label));
                if (p.Type != ParameterType.String)
                    Line("    type: " + YamlNames.ParameterTypes.ToYaml(p.Type));
                if (!p.Required)
                    Line("    required: false");
                if (p.Type is ParameterType.String or ParameterType.Text && p.Multiline != (p.Type == ParameterType.Text))
                    Line("    multiline: " + Bool(p.Multiline));
                if (p.Type is ParameterType.Int or ParameterType.Decimal or ParameterType.Range)
                {
                    if (p.Min is { } min)
                        Line("    min: " + min.ToString(CultureInfo.InvariantCulture));
                    if (p.Max is { } max)
                        Line("    max: " + max.ToString(CultureInfo.InvariantCulture));
                }
                if (p.Type == ParameterType.List)
                {
                    if (p.Multi)
                        Line("    multi: true");
                    if (p.Editable)
                        Line("    editable: true");
                    if (p.Items.Count > 0)
                        Line("    items: " + Items(p.Items));
                }
                if (p.Type == ParameterType.File && !string.IsNullOrEmpty(p.Filter))
                    Line("    filter: " + Q(p.Filter));
                if (p.Default.Count > 0)
                    Line("    default: " + Values(p.Default, p.Multi));
                if (!string.IsNullOrWhiteSpace(p.Help))
                    Line("    help: " + Q(p.Help));
            }
        }

        return sb.ToString();
    }

    internal static string Target(ConnectionTarget t)
    {
        var parts = new List<string> { "server: " + Q(t.Server, flow: true) };
        if (t.Port is { } port)
            parts.Add("port: " + port.ToString(CultureInfo.InvariantCulture));
        if (t.Database is { } db)
            parts.Add("database: " + Q(db, flow: true));
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string Items(IReadOnlyList<ListItem> items) =>
        items.All(i => i.Key == i.Value)
            ? Flow(items.Select(i => i.Key))
            : "[ " + string.Join(", ", items.Select(i => $"{{ key: {Q(i.Key, flow: true)}, value: {Q(i.Value, flow: true)} }}")) + " ]";

    private static string Values(IReadOnlyList<string> values, bool multi) =>
        multi || values.Count > 1 ? Flow(values) : Q(values[0]);

    private static string Flow(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => Q(v, flow: true))) + "]";

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Скаляр YAML: без кавычек, если читается однозначно как та же строка, иначе в двойных кавычках.</summary>
    internal static string Q(string value, bool flow = false)
    {
        var plain = value.Length > 0
            && value == value.Trim()
            && PlainRegex().IsMatch(value)
            && !(flow && value.IndexOfAny([',', '[', ']', '{', '}', ':']) >= 0)
            && value is not ("~" or "null" or "Null" or "NULL");
        if (plain)
            return value;

        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    // Первый символ — не индикатор YAML; внутри нет «: », « #» и переводов строк.
    [GeneratedRegex(@"^[^\s\-?:,\[\]{}#&*!|>'""%@`](?:[^\r\n:#]|:(?=\S)|(?<=\S)#)*(?<!:)$")]
    private static partial Regex PlainRegex();
}
