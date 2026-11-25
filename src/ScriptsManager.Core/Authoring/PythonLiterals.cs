using System.Text;
using System.Text.RegularExpressions;

namespace ScriptsManager.Core.Authoring;

/// <summary>Минимальный разбор аргументов вызова Python: строки, числа, True/False/None, списки литералов.</summary>
internal static partial class PythonLiterals
{
    /// <summary>Позиция закрывающей скобки для <paramref name="open"/>; -1 — не найдена.</summary>
    public static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\'' or '"')
                i = SkipString(text, i) - 1;
            else if (c == '#')
                while (i < text.Length && text[i] != '\n') i++;
            else if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}' && --depth == 0)
                return i;
        }
        return -1;
    }

    /// <summary>Аргументы вызова верхнего уровня: (имя, выражение), имя null — позиционный.</summary>
    public static List<(string? Key, string Value)> SplitArguments(string args)
    {
        var result = new List<(string?, string)>();
        foreach (var part in Split(args))
        {
            var m = KeywordRegex().Match(part);
            result.Add(m.Success ? (m.Groups[1].Value, part[m.Length..].Trim()) : (null, part.Trim()));
        }
        return result;
    }

    /// <summary>Значение строкового литерала; null — не строка.</summary>
    public static string? String(string expression)
    {
        var e = expression.Trim();
        var prefix = 0;
        while (prefix < e.Length && char.IsLetter(e[prefix])) prefix++;
        if (prefix > 2 || prefix == e.Length || e[prefix] is not ('\'' or '"'))
            return null;
        var flags = e[..prefix].ToLowerInvariant();
        if (flags.Contains('f') || flags.Contains('b'))
            return null;
        var raw = flags.Contains('r');

        var quote = e[prefix];
        var triple = e.Length >= prefix + 6 && e.Substring(prefix, 3) == new string(quote, 3);
        var q = triple ? 3 : 1;
        if (SkipString(e, prefix) != e.Length || e.Length < prefix + 2 * q)
            return null;
        var body = e[(prefix + q)..^q];
        return raw ? body : Unescape(body);
    }

    /// <summary>Скаляр как строка: строка, число, True/False → true/false; None и прочее — null.</summary>
    public static string? Scalar(string expression)
    {
        var e = expression.Trim();
        if (String(e) is { } s)
            return s;
        if (e is "True" or "False")
            return e == "True" ? "true" : "false";
        return NumberRegex().IsMatch(e) ? e.TrimStart('+') : null;
    }

    /// <summary>Список или кортеж литералов; null — не литерал.</summary>
    public static List<string>? List(string expression)
    {
        var e = expression.Trim();
        if (e.Length < 2 || !(e[0] == '[' && e[^1] == ']' || e[0] == '(' && e[^1] == ')'))
            return null;
        var items = Split(e[1..^1]).Select(Scalar).ToList();
        return items.Any(i => i is null) ? null : items.Select(i => i!).ToList();
    }

    private static IEnumerable<string> Split(string args)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c is '\'' or '"')
                i = SkipString(args, i) - 1;
            else if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == ',' && depth == 0)
            {
                if (args[start..i].Trim().Length > 0)
                    yield return args[start..i];
                start = i + 1;
            }
        }
        if (args[start..].Trim().Length > 0)
            yield return args[start..];
    }

    /// <summary>Позиция после строки, начинающейся в <paramref name="i"/> (учитывает тройные кавычки).</summary>
    private static int SkipString(string text, int i)
    {
        var quote = text[i];
        var triple = i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote;
        i += triple ? 3 : 1;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i] == quote && (!triple || i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote))
                return i + (triple ? 3 : 1);
            if (!triple && text[i] == '\n')
                return i;
            i++;
        }
        return text.Length;
    }

    private static string Unescape(string body)
    {
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i + 1 == body.Length)
            {
                sb.Append(body[i]);
                continue;
            }
            var c = body[++i];
            if (c is 'n' or 't' or 'r' or '\\' or '\'' or '"')
                sb.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c });
            else
                sb.Append('\\').Append(c); // как в Python: неизвестная последовательность остаётся как есть
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)")]
    private static partial Regex KeywordRegex();

    [GeneratedRegex(@"^[-+]?(\d+(\.\d*)?|\.\d+)$")]
    private static partial Regex NumberRegex();
}
