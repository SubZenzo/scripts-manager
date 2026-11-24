using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Authoring;

/// <summary>
/// Блок <c>--#region Params … --#endregion</c> в T-SQL скрипте: объявления параметров, чтобы скрипт
/// запускался и вне приложения (SSMS и т.п.). Приложение передаёт параметры само и при запуске блок пропускает.
/// </summary>
public static partial class TSqlParamsRegion
{
    public const string StartLine = "--#region Params";
    public const string EndLine = "--#endregion";
    private const string Comment = "-- Для запуска вне Scripts Manager: приложение пропускает этот блок и передаёт параметры само";

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Текст для выполнения: строки блока очищены, номера строк сохраняются.</summary>
    public static string Blank(string text) =>
        Find(text) is var (start, end)
            ? text[..start] + NotNewLineRegex().Replace(text[start..end], "") + text[end..]
            : text;

    /// <summary>Имена переменных, объявленных в блоке.</summary>
    public static IReadOnlySet<string> Names(string text) =>
        Find(text) is var (start, end)
            ? TSqlDeclarations.Find(text[start..end]).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();

    /// <summary>
    /// Пересобирает блок по параметрам манифеста и дополнительным подключениям (<c>@слот_server</c>, <c>@слот_database</c>).
    /// Объявления этих переменных в начале скрипта переносятся в блок; тип и выражение из скрипта сохраняются,
    /// если совпадают с типом и значением по умолчанию параметра. Блок остаётся на прежнем месте, новый — в начале.
    /// </summary>
    public static string Update(string text, IReadOnlyList<ScriptParameter> parameters, IEnumerable<string> extraConnections)
    {
        var previous = TSqlDeclarations.Find(text)
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";

        // Метка-комментарий держит место блока, пока убираются объявления.
        var marker = $"--#params-{Guid.NewGuid():N}{newline}";
        text = Find(text) is var (start, end) ? text[..start] + marker + text[end..] : marker + text;

        var declarations = new List<string>();
        foreach (var p in parameters)
            declarations.Add(Declaration(p, previous.GetValueOrDefault(p.Name)));
        foreach (var name in extraConnections.SelectMany(c => new[] { c + "_server", c + "_database" }))
            declarations.Add(previous.TryGetValue(name, out var d) ? Format(name, d.SqlType, d.Initializer) : Format(name, "nvarchar(max)", null));

        text = TSqlDeclarations.Remove(text, parameters.Select(p => p.Name).Concat(extraConnections.SelectMany(c => new[] { c + "_server", c + "_database" })));
        var block = declarations.Count == 0 ? "" : string.Join(newline, [StartLine, Comment, .. declarations, EndLine]) + newline;
        return text.Replace(marker, block);
    }

    /// <summary>Начало первой строки блока и конец его последней строки (вместе с переводом строки).</summary>
    private static (int Start, int End)? Find(string text)
    {
        var start = StartRegex().Match(text);
        if (!start.Success)
            return null;
        var end = EndRegex().Match(text, start.Index + start.Length);
        if (!end.Success)
            return null;
        var stop = end.Index + end.Length;
        if (stop < text.Length && text[stop] == '\r') stop++;
        if (stop < text.Length && text[stop] == '\n') stop++;
        return (start.Index, stop);
    }

    private static string Declaration(ScriptParameter p, TSqlDeclaration? previous)
    {
        var expected = SqlFamily(p);
        if (previous is not null && ScriptAnalyzer.SqlType(previous.SqlType, out _) == expected)
        {
            // Выражение из скрипта (например, dateadd(month, -1, getdate())) лучше сгенерированного,
            // если даёт то же значение по умолчанию. Ссылки на другие переменные не переносим: их тут ещё нет.
            var initializer = previous.Initializer is { } i && !i.Contains('@')
                && ScriptAnalyzer.SqlDefault(i, expected).Values.SequenceEqual(p.Default) ? i : Literal(p);
            return Format(p.Name, previous.SqlType, initializer);
        }

        return Format(p.Name, SqlTypeName(p), Literal(p));
    }

    private static string Format(string name, string sqlType, string? initializer) =>
        $"declare @{name} {sqlType}" + (initializer is null ? "" : " = " + initializer) + ";";

    private static ParameterType SqlFamily(ScriptParameter p) => p.Multi ? ParameterType.String : p.Type switch
    {
        ParameterType.Int or ParameterType.Decimal or ParameterType.Bool or ParameterType.Date
            or ParameterType.DateTime or ParameterType.Guid => p.Type,
        _ => ParameterType.String,
    };

    /// <summary>Тип, с которым параметр передаёт TSqlRunner.</summary>
    private static string SqlTypeName(ScriptParameter p) => SqlFamily(p) switch
    {
        ParameterType.Int => "bigint",
        ParameterType.Decimal => "decimal(38, 10)",
        ParameterType.Bool => "bit",
        ParameterType.Date => "date",
        ParameterType.DateTime => "datetime2",
        ParameterType.Guid => "uniqueidentifier",
        _ => "nvarchar(max)",
    };

    /// <summary>Значение по умолчанию как выражение T-SQL; null — без значения.</summary>
    private static string? Literal(ScriptParameter p)
    {
        var values = p.Default.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (values.Count == 0)
            return null;
        if (p.Multi)
            return Unicode(JsonSerializer.Serialize(values, JsonOptions));

        var value = values[0];
        return SqlFamily(p) switch
        {
            ParameterType.Int or ParameterType.Decimal => value,
            ParameterType.Bool => value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0",
            ParameterType.Date or ParameterType.DateTime => DateLiteral(value, p.Type == ParameterType.Date),
            ParameterType.Guid => $"'{value}'",
            _ => Unicode(value),
        };
    }

    private static string Unicode(string value) => "N'" + value.Replace("'", "''") + "'";

    /// <summary><c>today-7</c> → <c>dateadd(day, -7, cast(getdate() as date))</c>; литерал — в ISO.</summary>
    private static string DateLiteral(string value, bool dateOnly)
    {
        var m = ParameterValues.DateExpressionRegex().Match(value);
        if (!m.Success)
        {
            return ParameterValues.TryEvaluateDate(value, DateTime.Now, out var d)
                ? $"'{d.ToString(dateOnly ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}'"
                : Unicode(value);
        }

        var today = dateOnly || m.Groups["base"].Value.Equals("today", StringComparison.OrdinalIgnoreCase);
        var baseExpression = today ? "cast(getdate() as date)" : "getdate()";
        if (!m.Groups["sign"].Success)
            return baseExpression;
        var unit = m.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "h" => "hour",
            "m" => "minute",
            _ => "day",
        };
        var sign = m.Groups["sign"].Value == "-" ? "-" : "";
        return $"dateadd({unit}, {sign}{m.Groups["amount"].Value}, {baseExpression})";
    }

    [GeneratedRegex(@"^[ \t]*--[ \t]*#region[ \t]+Params\b[^\n]*", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"^[ \t]*--[ \t]*#endregion\b[^\r\n]*", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EndRegex();

    [GeneratedRegex(@"[^\r\n]")]
    private static partial Regex NotNewLineRegex();
}
