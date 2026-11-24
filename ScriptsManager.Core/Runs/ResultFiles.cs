using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ScriptsManager.Core.Runs;

/// <summary>Таблицы из файлов, которые скрипт положил в RESULT_DIR: *.csv (разделитель «;», «,» или табуляция) и *.json (массив объектов).</summary>
public static class ResultFiles
{
    public static IReadOnlyList<DataTable> Load(string dir, IRunOutput output)
    {
        var result = new List<DataTable>();
        foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".csv" or ".json"))
                continue;
            try
            {
                var table = ext == ".csv" ? ReadCsv(File.ReadAllText(file)) : ReadJson(File.ReadAllText(file));
                table.TableName = Path.GetFileName(file);
                result.Add(table);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                output.Log(LogKind.Error, $"{Path.GetFileName(file)}: не удалось прочитать таблицу — {ex.Message}");
            }
        }

        return result;
    }

    public static DataTable ReadCsv(string text)
    {
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ';', ',', '\t' }.MaxBy(d => firstLine.Count(c => c == d));
        var rows = ParseCsv(text, delimiter);
        var table = new DataTable();
        if (rows.Count == 0)
            return table;

        foreach (var header in rows[0])
            AddColumn(table, header);
        foreach (var row in rows.Skip(1))
        {
            if (row.Count == 1 && row[0].Length == 0)
                continue;
            while (row.Count > table.Columns.Count)
                AddColumn(table, "");
            table.Rows.Add(row.Cast<object>().ToArray());
        }

        return table;
    }

    public static DataTable ReadJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ожидается JSON-массив объектов");

        var table = new DataTable();
        var columns = new Dictionary<string, int>();
        var rows = new List<Dictionary<int, object>>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("элементы массива должны быть объектами");
            var row = new Dictionary<int, object>();
            foreach (var p in item.EnumerateObject())
            {
                if (!columns.TryGetValue(p.Name, out var index))
                {
                    index = columns[p.Name] = columns.Count;
                    AddColumn(table, p.Name);
                }

                row[index] = Value(p.Value);
            }

            rows.Add(row);
        }

        foreach (var row in rows)
        {
            var values = Enumerable.Range(0, columns.Count).Select(i => row.GetValueOrDefault(i, DBNull.Value)).ToArray();
            table.Rows.Add(values);
        }

        return table;
    }

    private static object Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => e.GetRawText(),
    };

    private static void AddColumn(DataTable table, string caption) =>
        table.Columns.Add("c" + table.Columns.Count.ToString(CultureInfo.InvariantCulture), typeof(object)).Caption =
            caption.Length > 0 ? caption : "(без имени)";

    /// <summary>CSV по RFC 4180: кавычки, удвоенные кавычки, переводы строк внутри кавычек.</summary>
    private static List<List<string>> ParseCsv(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString().TrimEnd('\r'));
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().TrimEnd('\r'));
            rows.Add(row);
        }

        return rows;
    }
}
