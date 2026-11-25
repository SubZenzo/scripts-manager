using System.Globalization;
using System.Text.RegularExpressions;

namespace ScriptsManager.Core.Runs;

/// <summary>Пакет скрипта: текст, номер первой строки в файле и число повторов (<c>GO 5</c>).</summary>
public sealed record SqlBatch(string Text, int StartLine, int Repeat);

public static partial class SqlBatches
{
    /// <summary>
    /// Делит скрипт по строкам <c>GO</c> (как SSMS). Строки внутри многострочных комментариев
    /// <c>/* */</c> не считаются разделителями; строковые литералы не разбираются.
    /// </summary>
    public static IReadOnlyList<SqlBatch> Split(string script)
    {
        var result = new List<SqlBatch>();
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var current = new List<string>();
        var start = 1;
        var inComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var m = inComment ? Match.Empty : GoRegex().Match(line);
            if (m.Success)
            {
                var repeat = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
                Add(result, current, start, repeat);
                current.Clear();
                start = i + 2;
                continue;
            }

            current.Add(line);
            inComment = UpdateCommentState(line, inComment);
        }

        Add(result, current, start, 1);
        return result;
    }

    private static void Add(List<SqlBatch> result, List<string> lines, int start, int repeat)
    {
        var text = string.Join("\n", lines);
        if (!string.IsNullOrWhiteSpace(text) && repeat > 0)
            result.Add(new SqlBatch(text, start, repeat));
    }

    // Грубо отслеживаем /* */, пропуская однострочные комментарии --.
    private static bool UpdateCommentState(string line, bool inComment)
    {
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (inComment)
            {
                if (line[i] == '*' && line[i + 1] == '/')
                {
                    inComment = false;
                    i++;
                }
            }
            else if (line[i] == '-' && line[i + 1] == '-')
            {
                break;
            }
            else if (line[i] == '/' && line[i + 1] == '*')
            {
                inComment = true;
                i++;
            }
        }

        return inComment;
    }

    [GeneratedRegex(@"^\s*go(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoRegex();
}
