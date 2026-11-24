using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Authoring;

/// <summary>
/// Где в PowerShell-скрипте объявлять параметры (смещения в тексте, по AST).
/// </summary>
/// <param name="Exists">Блок param() есть.</param>
/// <param name="AfterLast">Конец последнего параметра в param(); -1 — параметров нет.</param>
/// <param name="InsertAt">Без блока — куда вставить новый (перед первым кодом); с пустым блоком — позиция «)».</param>
public sealed record PowerShellParamBlock(bool Exists, int AfterLast, int InsertAt);

/// <summary>
/// Объявляет в тексте PowerShell- и Python-скрипта параметры манифеста, которых в нём нет: иначе приложение передаёт
/// значение, а скрипт его не принимает (PowerShell кладёт в $args, argparse падает на незнакомом аргументе).
/// Существующие объявления не меняются.
/// </summary>
public static partial class ScriptParamsWriter
{
    public const string PythonRegionStart = "#region Params";
    public const string PythonRegionEnd = "#endregion";

    // ---- PowerShell ----

    /// <summary>Дописывает <paramref name="missing"/> в param() (или создаёт блок в начале скрипта).</summary>
    public static string PowerShell(string text, IReadOnlyList<ScriptParameter> missing, PowerShellParamBlock block)
    {
        if (missing.Count == 0)
            return text;

        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        if (block.Exists && block.AfterLast >= 0)
        {
            var indent = LineIndent(text, block.AfterLast);
            var sb = new StringBuilder();
            foreach (var p in missing)
                sb.Append(',').Append(nl).Append(PowerShellDeclaration(p, indent, nl));
            return text.Insert(block.AfterLast, sb.ToString());
        }

        var body = string.Join("," + nl, missing.Select(p => PowerShellDeclaration(p, "    ", nl)));
        if (block.Exists)
            return text.Insert(block.InsertAt, nl + body + nl);

        // После using … — с новой строки.
        var before = block.InsertAt > 0 && text[block.InsertAt - 1] != '\n' ? nl : "";
        return text.Insert(block.InsertAt, before + "param(" + nl + body + nl + ")" + nl + (before.Length > 0 ? "" : nl));
    }

    /// <summary>Разбор блока из JSON, который печатает разбор param() (поле <c>block</c>).</summary>
    public static PowerShellParamBlock? ParseBlock(JsonElement root) =>
        root.TryGetProperty("block", out var b) && b.ValueKind == JsonValueKind.Object
            ? new PowerShellParamBlock(b.GetProperty("exists").GetBoolean(), b.GetProperty("afterLast").GetInt32(), b.GetProperty("insertAt").GetInt32())
            : null;

    private static string PowerShellDeclaration(ScriptParameter p, string indent, string nl)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(p.Help))
            sb.Append(indent).Append("# ").Append(p.Help.ReplaceLineEndings(" ")).Append(nl);
        var type = p.Type switch
        {
            ParameterType.Int => "long",
            ParameterType.Decimal => "decimal",
            ParameterType.Bool => "bool",
            ParameterType.Date or ParameterType.DateTime => "datetime",
            ParameterType.Guid => "guid",
            ParameterType.Range => "int[]",
            ParameterType.List when p.Multi => "string[]",
            _ => "string",
        };
        sb.Append(indent).Append('[').Append(type).Append("]$").Append(p.Name);
        if (PowerShellDefault(p) is { } value)
            sb.Append(" = ").Append(value);
        return sb.ToString();
    }

    private static string? PowerShellDefault(ScriptParameter p)
    {
        if (p.Default.Count == 0)
            return p.Type == ParameterType.Bool ? "$false" : null;
        var first = p.Default[0];
        return p.Type switch
        {
            ParameterType.Bool => first.Equals("true", StringComparison.OrdinalIgnoreCase) ? "$true" : "$false",
            ParameterType.Int or ParameterType.Decimal => first,
            // Выражения (today-7) вычисляет приложение: в скрипте значения по умолчанию нет.
            ParameterType.Date or ParameterType.DateTime => IsDateLiteral(first) ? PsString(first) : null,
            ParameterType.Range => PsRange(first),
            ParameterType.List when p.Multi => "@(" + string.Join(", ", p.Default.Select(PsString)) + ")",
            _ => PsString(first),
        };
    }

    // «19..28, 7» → «@(19..28) + @(7)»: так получается массив и из одного числа.
    private static string? PsRange(string text) =>
        ParameterValues.TryParseRange(text, out _) is null
            ? string.Join(" + ", text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => "@(" + RangeSpacesRegex().Replace(part, "..") + ")"))
            : null;

    private static string PsString(string s) => "'" + s.Replace("'", "''") + "'";

    // ---- Python ----

    /// <summary>
    /// Объявляет <paramref name="missing"/> для argparse: в блоке <c>#region Params</c>, если он есть (блок пересобирается
    /// из параметров манифеста, объявленных в нём, и новых), иначе перед <c>parse_args()</c> своего парсера скрипта,
    /// иначе — новым блоком в начале скрипта.
    /// </summary>
    public static string Python(string text, IReadOnlyList<ScriptParameter> parameters, IReadOnlyList<ScriptParameter> missing)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var region = PythonRegionRegex().Match(text);
        if (region.Success)
        {
            var declared = ScriptAnalyzer.Analyze(RunnerKind.Python, region.Value).Parameters.Select(d => d.Parameter.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inRegion = parameters.Where(p => declared.Contains(p.Name) || missing.Contains(p)).ToList();
            if (missing.Count == 0)
                return text;
            return text[..region.Index] + PythonRegion(inRegion, nl) + text[(region.Index + region.Length)..];
        }

        if (missing.Count == 0)
            return text;

        var parser = PythonParserRegex().Match(text);
        if (parser.Success)
        {
            var name = parser.Groups["var"].Value;
            var call = new Regex(@"^(?<indent>[ \t]*).*\b" + Regex.Escape(name) + @"\s*\.\s*parse_(?:known_)?args\s*\(", RegexOptions.Multiline)
                .Match(text, parser.Index);
            if (call.Success)
            {
                var indent = call.Groups["indent"].Value;
                var lines = string.Concat(missing.Select(p => indent + PythonArgument(name, p) + nl));
                return text.Insert(call.Index, lines);
            }
        }

        var at = PythonHeaderEnd(text);
        return text.Insert(at, PythonRegion(missing, nl) + nl + (at < text.Length ? nl : ""));
    }

    private static string PythonRegion(IReadOnlyList<ScriptParameter> parameters, string nl)
    {
        var sb = new StringBuilder();
        sb.Append(PythonRegionStart).Append(": параметры передаёт  Scripts Manager (--имя значение)").Append(nl);
        sb.Append("import argparse").Append(nl).Append(nl);
        sb.Append("_parser = argparse.ArgumentParser()").Append(nl);
        foreach (var p in parameters)
            sb.Append(PythonArgument("_parser", p)).Append(nl);
        sb.Append("_args = _parser.parse_args()").Append(nl);
        foreach (var p in parameters)
            sb.Append(PythonName(p)).Append(" = _args.").Append(PythonName(p)).Append(nl);
        sb.Append(PythonRegionEnd);
        return sb.ToString();
    }

    // Ключевое слово (from, class, …) не может быть переменной и атрибутом: from → from_.
    private static string PythonName(ScriptParameter p) => PythonKeywords.Contains(p.Name) ? p.Name + "_" : p.Name;

    private static readonly HashSet<string> PythonKeywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
        "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or",
        "pass", "raise", "return", "try", "while", "with", "yield",
    ];

    private static string PythonArgument(string parser, ScriptParameter p)
    {
        var args = new List<string> { PyString("--" + p.Name) };
        if (PythonName(p) != p.Name)
            args.Add("dest=" + PyString(PythonName(p)));
        switch (p.Type)
        {
            case ParameterType.Bool:
                args.Add("action=\"store_true\"");
                break;
            case ParameterType.Int:
                args.Add("type=int");
                break;
            case ParameterType.Decimal:
                args.Add("type=float");
                break;
            case ParameterType.Range:
                args.Add("type=int");
                args.Add("nargs=\"*\"");
                break;
            case ParameterType.List when p.Multi:
                args.Add("nargs=\"*\"");
                break;
        }
        if (p.Type == ParameterType.List && p.Items.Count > 0 && !p.Editable)
            args.Add("choices=[" + string.Join(", ", p.Items.Select(i => PyString(i.Key))) + "]");

        var value = PythonDefault(p);
        if (value is not null)
            args.Add("default=" + value);
        else if (p.Required && p.Type != ParameterType.Bool)
            args.Add("required=True");
        if (!string.IsNullOrWhiteSpace(p.Help))
            args.Add("help=" + PyString(p.Help.ReplaceLineEndings(" ")));
        return $"{parser}.add_argument({string.Join(", ", args)})";
    }

    private static string? PythonDefault(ScriptParameter p)
    {
        if (p.Default.Count == 0 || p.Type == ParameterType.Bool)
            return null;
        var first = p.Default[0];
        return p.Type switch
        {
            ParameterType.Int or ParameterType.Decimal => first,
            ParameterType.Date or ParameterType.DateTime => IsDateLiteral(first) ? PyString(first) : null,
            ParameterType.Range => ParameterValues.TryParseRange(first, out var numbers) is null
                ? "[" + string.Join(", ", numbers) + "]" : null,
            ParameterType.List when p.Multi => "[" + string.Join(", ", p.Default.Select(PyString)) + "]",
            _ => PyString(first),
        };
    }

    // Новый блок — после #!, кодировки, комментариев и docstring в начале файла.
    private static int PythonHeaderEnd(string text)
    {
        var pos = 0;
        var inDocstring = false;
        string? quote = null;
        while (pos < text.Length)
        {
            var end = text.IndexOf('\n', pos);
            var next = end < 0 ? text.Length : end + 1;
            var line = text[pos..next].Trim();
            if (inDocstring)
            {
                if (line.Contains(quote!))
                    inDocstring = false;
            }
            else if (line.StartsWith("\"\"\"") || line.StartsWith("'''"))
            {
                quote = line[..3];
                inDocstring = line.Length < 6 || !line[3..].Contains(quote);
            }
            else if (line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("from __future__"))
            {
                return pos;
            }
            pos = next;
        }

        return text.Length;
    }

    private static string PyString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    // ---- общее ----

    private static bool IsDateLiteral(string value) => !ParameterValues.DateExpressionRegex().IsMatch(value);

    private static string LineIndent(string text, int offset)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        var i = start;
        while (i < text.Length && text[i] is ' ' or '\t')
            i++;
        return text[start..i];
    }

    [GeneratedRegex(@"\s*\.\.\s*")]
    private static partial Regex RangeSpacesRegex();

    [GeneratedRegex(@"(?<var>[A-Za-z_]\w*)\s*=\s*(?:argparse\s*\.\s*)?ArgumentParser\s*\(")]
    private static partial Regex PythonParserRegex();

    [GeneratedRegex(@"^#region Params\b.*?^#endregion[^\r\n]*", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex PythonRegionRegex();
}
