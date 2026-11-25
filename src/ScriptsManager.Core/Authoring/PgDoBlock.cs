using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Authoring;

/// <summary>Переменная из DECLARE блока DO.</summary>
/// <param name="Name">Имя переменной, например <c>_from</c>.</param>
/// <param name="SqlType">Тип как в скрипте.</param>
/// <param name="Initializer">Выражение после <c>:=</c>, <c>=</c> или <c>default</c>; null — без значения.</param>
/// <param name="Start">Начало объявления в тексте скрипта.</param>
/// <param name="End">Конец объявления (до «;»).</param>
public sealed record PgDeclaration(string Name, string SqlType, string? Initializer, int Start, int End)
{
    /// <summary>Значение уже берётся из параметра приложения.</summary>
    public bool IsParameter => Initializer is not null && PgDoBlock.SettingRegex().IsMatch(Initializer);
}

/// <summary>
/// Параметры в первом блоке <c>DO $$ … $$</c> PL/pgSQL: туда приложение не может передать значения, поэтому
/// переменная DECLARE читает их из настройки сессии (её задаёт <c>PlPgSqlRunner</c>):
/// <c>l_n int := nullif(coalesce(current_setting('param.l_n', true), '1'), '')::int;</c>. Вне приложения настройки нет,
/// и действует прежнее значение — скрипт можно запустить и в psql/DBeaver.
/// </summary>
public static partial class PgDoBlock
{
    /// <summary>Объявления DECLARE первого блока DO; нет блока или DECLARE — пусто.</summary>
    public static IReadOnlyList<PgDeclaration> Find(string text) =>
        Body(text) is var (start, end) ? Declarations(text, start, end, out _) : [];

    /// <summary>
    /// Связывает переменные блока DO с параметрами манифеста: переменная с именем параметра получает
    /// значение из <c>current_setting</c>, недостающие дописываются в DECLARE. Параметры, которые скрипт уже берёт
    /// сам (<c>@имя</c>, <c>current_setting('param.имя')</c> в тексте), и объявления, уже связанные с параметром,
    /// не меняются. Без блока DO текст не меняется: в обычных запросах параметры — <c>@имя</c>.
    /// </summary>
    public static string Update(string text, IReadOnlyList<ScriptParameter> parameters)
    {
        if (parameters.Count == 0 || Body(text) is not var (start, end))
            return text;

        var declarations = Declarations(text, start, end, out var section);
        var used = ScriptAnalyzer.Analyze(RunnerKind.PlPgSql, text).Parameters
            .Where(d => !declarations.Any(x => x.Name.Equals(d.Parameter.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.Parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var result = new StringBuilder(text);

        // Новые — в конец DECLARE (или новым DECLARE перед BEGIN).
        var missing = parameters
            .Where(p => !used.Contains(p.Name) && !declarations.Any(d => d.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Name} {SqlType(p)} := {Setting(p.Name, Fallback(p), SqlType(p))};")
            .ToList();
        if (missing.Count > 0)
        {
            var lines = string.Concat(missing.Select(m => nl + Indent + m));
            result.Insert(section.InsertAt, section.HasDeclare ? lines : "declare" + lines + nl);
        }

        // С конца, чтобы не сдвигались позиции.
        foreach (var d in declarations.AsEnumerable().Reverse())
        {
            if (parameters.FirstOrDefault(p => p.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)) is not { } p)
                continue;
            var lineStart = LineStart(text, d.Start);
            if (d.IsParameter)
            {
                // Уже связанное — только отступ.
                if (lineStart is { } ls && text[ls..d.Start] != Indent)
                    result.Remove(ls, d.Start - ls).Insert(ls, Indent);
                continue;
            }
            var fallback = d.Initializer is null ? null
                : StringLiteralRegex().IsMatch(d.Initializer) ? d.Initializer
                : NumberRegex().IsMatch(d.Initializer) ? $"'{d.Initializer}'"
                : $"({d.Initializer})::text";
            // Объявление в начале строки — с отступом в 4 пробела.
            var from = lineStart ?? d.Start;
            result.Remove(from, d.End - from).Insert(from, (lineStart is null ? "" : Indent) + $"{d.Name} {d.SqlType} := {Setting(p.Name, fallback, d.SqlType)}");
        }

        return result.ToString();
    }

    /// <summary>Чтение параметра: значение из приложения, без него — <paramref name="fallback"/>; пустая строка — null.</summary>
    private static string Setting(string name, string? fallback, string type)
    {
        var setting = $"current_setting('param.{name.ToLowerInvariant()}', true)";
        return fallback is null
            ? $"nullif({setting}, '')::{type}"
            : $"nullif(coalesce({setting}, {fallback}), '')::{type}";
    }

    /// <summary>Значение по умолчанию параметра для запуска вне приложения; несколько значений — без него.</summary>
    private static string? Fallback(ScriptParameter p)
    {
        if (p.Default.Count != 1 || p.Multi || p.Type == ParameterType.Range)
            return null;
        var m = ParameterValues.DateExpressionRegex().Match(p.Default[0]);
        if (!m.Success)
            return "'" + p.Default[0].Replace("'", "''") + "'";

        var expression = m.Groups["base"].Value.Equals("today", StringComparison.OrdinalIgnoreCase) ? "current_date" : "localtimestamp(0)";
        if (m.Groups["sign"].Success)
        {
            var unit = m.Groups["unit"].Value.ToLowerInvariant() switch { "h" => "hour", "m" => "minute", _ => "day" };
            expression += $" {m.Groups["sign"].Value} interval '{m.Groups["amount"].Value} {unit}'";
        }
        return $"({expression})::text";
    }

    /// <summary>Значение вне приложения из объявления, уже связанного с параметром (второй аргумент coalesce).</summary>
    public static string? Fallback(string initializer)
    {
        var m = CoalesceRegex().Match(initializer);
        if (!m.Success)
            return null;
        // До запятой или скобки, закрывающей coalesce.
        var rest = initializer[(m.Index + m.Length)..];
        var depth = 0;
        var quoted = false;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c == '\'')
                quoted = !quoted;
            else if (quoted)
                continue;
            else if (c == '(')
                depth++;
            else if (c == ')' && depth-- == 0)
            {
                var value = rest[..i].Trim();
                var cast = TextCastRegex().Match(value);
                return cast.Success ? cast.Groups[1].Value.Trim() : value;
            }
        }
        return null;
    }

    private static string SqlType(ScriptParameter p) => p.Multi || p.Type == ParameterType.Range ? "jsonb" : p.Type switch
    {
        ParameterType.Int => "bigint",
        ParameterType.Decimal => "numeric",
        ParameterType.Bool => "boolean",
        ParameterType.Date => "date",
        ParameterType.DateTime => "timestamp",
        ParameterType.Guid => "uuid",
        _ => "text",
    };

    /// <summary>Тело первого блока DO (между долларовыми метками).</summary>
    private static (int Start, int End)? Body(string text)
    {
        var code = new StringBuilder();
        foreach (Match m in SegmentRegex().Matches(text))
        {
            if (m.Groups["dollar"].Success)
            {
                if (DoRegex().IsMatch(code.ToString()))
                {
                    var tag = m.Groups["tag"].Value.Length + 2;
                    return (m.Index + tag, m.Index + m.Length - tag);
                }
                code.Clear();
            }
            else if (m.Groups["code"].Success)
                code.Append(m.Value);
        }
        return null;
    }

    /// <summary>Где дописывать объявления: после последнего в DECLARE или, без DECLARE, перед BEGIN.</summary>
    private readonly record struct Section(bool HasDeclare, int InsertAt);

    private static List<PgDeclaration> Declarations(string text, int start, int end, out Section section)
    {
        // Комментарии — пробелами, чтобы не мешали разбору и не сдвигали позиции.
        var masked = new StringBuilder(text[start..end]);
        foreach (Match m in SegmentRegex().Matches(masked.ToString()))
        {
            if (m.Groups["cmt"].Success)
                for (var i = m.Index; i < m.Index + m.Length; i++)
                    if (masked[i] is not ('\r' or '\n'))
                        masked[i] = ' ';
        }
        var body = masked.ToString();

        var header = HeaderRegex().Match(body);
        var result = new List<PgDeclaration>();
        if (!header.Success)
        {
            section = new(false, start);
            return result;
        }
        if (!header.Groups["declare"].Success)
        {
            section = new(false, start + header.Groups["begin"].Index);
            return result;
        }

        var declStart = header.Groups["declare"].Index + header.Groups["declare"].Length;
        var declEnd = header.Groups["begin"].Index;
        var insertAt = declStart;
        var from = declStart;
        var quoted = false;
        var depth = 0;
        for (var i = declStart; i < declEnd; i++)
        {
            var c = body[i];
            if (c == '\'')
                quoted = !quoted;
            else if (!quoted && c == '(')
                depth++;
            else if (!quoted && c == ')')
                depth--;
            else if (!quoted && depth == 0 && c == ';')
            {
                var item = body[from..i];
                var lead = item.Length - item.TrimStart().Length;
                var m = DeclarationRegex().Match(item.Trim());
                if (m.Success && !NotParameterTypeRegex().IsMatch(m.Groups["type"].Value))
                {
                    result.Add(new PgDeclaration(m.Groups["name"].Value, m.Groups["type"].Value.Trim(),
                        m.Groups["init"].Success ? m.Groups["init"].Value.Trim() : null,
                        start + from + lead, start + from + item.TrimEnd().Length));
                }
                insertAt = i + 1;
                from = i + 1;
            }
        }

        // После комментария в конце строки последнего объявления, а не перед ним.
        var lineEnd = insertAt;
        while (lineEnd < declEnd && body[lineEnd] is ' ' or '\t')
            lineEnd++;
        if (lineEnd == declEnd || body[lineEnd] is '\r' or '\n')
            insertAt = lineEnd;
        section = new(true, start + insertAt);
        return result;
    }

    private const string Indent = "    ";

    /// <summary>Начало строки, если до <paramref name="at"/> на ней только пробелы; иначе null.</summary>
    private static int? LineStart(string text, int at)
    {
        var lineStart = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
        return text.AsSpan(lineStart, at - lineStart).IsWhiteSpace() ? lineStart : null;
    }

    [GeneratedRegex(@"current_setting\s*\(\s*'param\.", RegexOptions.IgnoreCase)]
    internal static partial Regex SettingRegex();

    [GeneratedRegex(@"(?<str>'(?:[^']|'')*')|(?<cmt>--[^\n]*|/\*.*?\*/)|(?<dollar>\$(?<tag>[A-Za-z_]*)\$.*?\$\k<tag>\$)|(?<qid>""[^""]*"")|(?<code>[^'\-/$""]+|[\-/$])", RegexOptions.Singleline)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"(?:^|[\s;(])do\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DoRegex();

    // Перед DECLARE может быть метка блока <<имя>>.
    [GeneratedRegex(@"^\s*(?:<<\s*\w+\s*>>\s*)?(?:(?<declare>\bdeclare\b).*?)?(?<begin>\bbegin\b)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+(?:constant\s+)?(?<type>.+?)(?:\s+not\s+null)?(?:\s*(?::=|=|\bdefault\b)\s*(?<init>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DeclarationRegex();

    // Записи, курсоры и псевдонимы параметром быть не могут.
    [GeneratedRegex(@"^\s*(?:record|refcursor|alias)\b|\bcursor\b|%", RegexOptions.IgnoreCase)]
    private static partial Regex NotParameterTypeRegex();

    [GeneratedRegex(@"coalesce\s*\(\s*current_setting\s*\(\s*'param\.\w+'\s*(?:,\s*true\s*)?\)\s*,", RegexOptions.IgnoreCase)]
    private static partial Regex CoalesceRegex();

    [GeneratedRegex(@"^\((.*)\)\s*::\s*text$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TextCastRegex();

    [GeneratedRegex(@"^'(?:[^']|'')*'$")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"^[-+]?(\d+(\.\d*)?|\.\d+)$")]
    private static partial Regex NumberRegex();
}
