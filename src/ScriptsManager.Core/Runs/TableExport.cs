using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ScriptsManager.Core.Runs;

public enum SqlDialect { TSql, PostgreSql }

/// <summary>Часть таблицы результата: строки и столбцы в порядке показа.</summary>
public sealed record TableSlice(IReadOnlyList<DataColumn> Columns, IReadOnlyList<DataRow> Rows)
{
    /// <summary>Выделенные ячейки (для копирования несмежного выделения); null — все.</summary>
    public IReadOnlySet<(DataRow Row, DataColumn Column)>? Selected { get; init; }

    public static TableSlice All(DataTable table) =>
        new(table.Columns.Cast<DataColumn>().ToList(), table.Rows.Cast<DataRow>().ToList());

    internal object? Value(DataRow row, DataColumn column) =>
        Selected is null || Selected.Contains((row, column)) ? row[column] : null;
}

public sealed record TableFormat(string Name, Func<TableSlice, string> Render);

/// <summary>Таблица результата в текстовых форматах и в xlsx.</summary>
public static class TableExport
{
    /// <summary>Форматы «Копировать как» — в порядке показа в меню.</summary>
    public static IReadOnlyList<TableFormat> Formats { get; } =
    [
        new("Jira / Confluence", Jira),
        new("Markdown", Markdown),
        new("SELECT для SQL Server", s => Select(s, SqlDialect.TSql)),
        new("SELECT для PostgreSQL", s => Select(s, SqlDialect.PostgreSql)),
    ];

    /// <summary>Как копирует Excel: табуляция между значениями, значения с табуляцией, переводом строки или кавычкой — в кавычках.</summary>
    public static string Tsv(TableSlice slice, bool headers)
    {
        var sb = new StringBuilder();
        if (headers)
            sb.AppendJoin('\t', slice.Columns.Select(c => TsvQuote(c.Caption))).Append("\r\n");
        foreach (var row in slice.Rows)
            sb.AppendJoin('\t', slice.Columns.Select(c => TsvQuote(ResultTables.Format(slice.Value(row, c))))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Таблица Markdown, столбцы выровнены пробелами; числовые столбцы — по правому краю.</summary>
    public static string Markdown(TableSlice slice)
    {
        var header = slice.Columns.Select(c => MdEscape(c.Caption)).ToList();
        var rows = slice.Rows.Select(r => slice.Columns.Select(c => MdCell(slice.Value(r, c))).ToList()).ToList();
        var numeric = slice.Columns.Select(c => slice.Rows.Any(r => r[c] is not DBNull)
            && slice.Rows.All(r => r[c] is DBNull || IsNumber(r[c]))).ToList();
        var widths = header.Select((h, i) => Math.Max(3, rows.Select(r => r[i].Length).Prepend(h.Length).Max())).ToList();

        var sb = new StringBuilder();
        Line(header, i => numeric[i]);
        sb.Append('|');
        for (var i = 0; i < widths.Count; i++)
            sb.Append(' ').Append(numeric[i] ? new string('-', widths[i] - 1) + ":" : new string('-', widths[i])).Append(" |");
        sb.AppendLine();
        foreach (var row in rows)
            Line(row, i => numeric[i]);
        return sb.ToString();

        void Line(IReadOnlyList<string> cells, Func<int, bool> right)
        {
            sb.Append('|');
            for (var i = 0; i < cells.Count; i++)
                sb.Append(' ').Append(right(i) ? cells[i].PadLeft(widths[i]) : cells[i].PadRight(widths[i])).Append(" |");
            sb.AppendLine();
        }
    }

    /// <summary>Разметка Jira и Confluence: заголовки между «||», ячейки между «|», без строки-разделителя.</summary>
    public static string Jira(TableSlice slice)
    {
        var sb = new StringBuilder("||");
        sb.AppendJoin("||", slice.Columns.Select(c => JiraEscape(c.Caption))).Append("||").AppendLine();
        foreach (var row in slice.Rows)
            sb.Append('|').AppendJoin('|', slice.Columns.Select(c => JiraCell(slice.Value(row, c)))).Append('|').AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// <c>select * from (values …) as t (…)</c> — одинаково работает в SQL Server и PostgreSQL;
    /// от диалекта зависят только литералы (N'…', даты, логические значения) и кавычки имён.
    /// </summary>
    public static string Select(TableSlice slice, SqlDialect dialect)
    {
        var names = UniqueNames(slice.Columns.Select(c => c.Caption)).Select(n => QuoteName(n, dialect));
        var rows = slice.Rows.Count > 0
            ? slice.Rows.Select(r => "(" + string.Join(", ", slice.Columns.Select(c => Literal(slice.Value(r, c), dialect))) + ")").ToList()
            : ["(" + string.Join(", ", slice.Columns.Select(_ => "null")) + ")"];

        var sb = new StringBuilder("select *\nfrom (values\n");
        sb.AppendJoin(",\n", rows.Select(r => "    " + r));
        sb.Append("\n) as t (").AppendJoin(", ", names).Append(')');
        if (slice.Rows.Count == 0)
            sb.Append("\nwhere 1 = 0");
        return sb.Append(';').Append('\n').ToString().ReplaceLineEndings();
    }

    /// <summary>Значение как литерал SQL.</summary>
    public static string Literal(object? value, SqlDialect dialect)
    {
        var pg = dialect == SqlDialect.PostgreSql;
        return value switch
        {
            null or DBNull => "null",
            bool b => pg ? (b ? "true" : "false") : (b ? "1" : "0"),
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            float f => float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "null",
            double d => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "null",
            DateTime d when d.TimeOfDay == TimeSpan.Zero => Typed(ResultTables.Format(d), "date"),
            DateTime d => Typed(ResultTables.Format(d), pg ? "timestamp" : "datetime2"),
            DateTimeOffset d => Typed(ResultTables.Format(d), pg ? "timestamptz" : "datetimeoffset"),
            TimeSpan t => Typed(t.ToString("c", CultureInfo.InvariantCulture), "time"),
            Guid g => pg ? Typed(g.ToString(), "uuid") : Text(g.ToString()),
            _ => Text(ResultTables.Format(value)),
        };

        string Text(string s)
        {
            var quoted = "'" + s.Replace("'", "''") + "'";
            // Без N кириллица в SQL Server пропадёт, если у базы не кириллическая collation.
            return !pg && s.Any(ch => ch > 127) ? "N" + quoted : quoted;
        }

        string Typed(string s, string type) => pg ? $"{type} '{s}'" : $"cast('{s}' as {type})";
    }

    private static string QuoteName(string name, SqlDialect dialect) => dialect == SqlDialect.PostgreSql
        ? "\"" + name.Replace("\"", "\"\"") + "\""
        : "[" + name.Replace("]", "]]") + "]";

    /// <summary>В списке столбцов производной таблицы имена не должны повторяться.</summary>
    private static IEnumerable<string> UniqueNames(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var unique = name;
            for (var i = 2; !used.Add(unique); i++)
                unique = $"{name}_{i}";
            yield return unique;
        }
    }

    private static bool IsNumber(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double;

    private static string TsvQuote(string value) =>
        value.IndexOfAny(['\t', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string MdCell(object? value) => value is DBNull ? "NULL" : MdEscape(ResultTables.Format(value));

    private static string JiraCell(object? value) => value is DBNull ? "NULL" : JiraEscape(ResultTables.Format(value));

    // Пустая ячейка склеилась бы с соседним «|» в «||» — нужен пробел; перевод строки внутри ячейки — «\\».
    private static string JiraEscape(string value)
    {
        var escaped = value.Replace("|", "\\|").Replace("{", "\\{").Replace("[", "\\[")
            .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " \\\\ ");
        return escaped.Length > 0 ? escaped : " ";
    }

    private static string MdEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    /// <summary>Минимальная книга Excel из одного листа: заголовок жирным и закреплён, даты — датами, числа — числами.</summary>
    public static void WriteXlsx(TableSlice slice, Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Part(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        Part(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Part(zip, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
            <sheets><sheet name="Результат" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        Part(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        // Стили: 0 — обычный, 1 — дата, 2 — дата и время, 3 — заголовок.
        Part(zip, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            <numFmts count="2"><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/><numFmt numFmtId="165" formatCode="yyyy-mm-dd hh:mm:ss"/></numFmts>
            <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
            <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
            <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
            <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
            <cellXfs count="4">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            </cellXfs>
            </styleSheet>
            """);

        using var writer = XmlWriter.Create(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(),
            new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true });
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        writer.WriteStartElement("worksheet", ns);
        writer.WriteRaw("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        writer.WriteStartElement("sheetData", ns);
        writer.WriteStartElement("row", ns);
        foreach (var column in slice.Columns)
            Cell(column.Caption, 3);
        writer.WriteEndElement();
        foreach (var row in slice.Rows)
        {
            writer.WriteStartElement("row", ns);
            foreach (var column in slice.Columns)
                Cell(slice.Value(row, column), 0);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();

        void Cell(object? value, int style)
        {
            writer.WriteStartElement("c", ns);
            if (style != 0)
                writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            switch (value)
            {
                case null or DBNull:
                    break;
                case bool b:
                    writer.WriteAttributeString("t", "b");
                    writer.WriteElementString("v", ns, b ? "1" : "0");
                    break;
                // Excel хранит 15 значащих цифр: длинные числа (идентификаторы) — текстом, чтобы не исказились.
                case byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double
                    when Convert.ToString(value, CultureInfo.InvariantCulture)!.Count(char.IsDigit) <= 15:
                    writer.WriteElementString("v", ns, Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case DateTime d when d.Year >= 1900:
                    writer.WriteAttributeString("s", d.TimeOfDay == TimeSpan.Zero ? "1" : "2");
                    writer.WriteElementString("v", ns, d.ToOADate().ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    var text = new string(ResultTables.Format(value).Where(XmlConvert.IsXmlChar).ToArray());
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteStartElement("is", ns);
                    writer.WriteStartElement("t", ns);
                    writer.WriteAttributeString("xml", "space", null, "preserve");
                    // Больше 32767 символов в ячейке Excel не принимает.
                    writer.WriteString(text.Length > 32767 ? text[..32767] : text);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    break;
            }
            writer.WriteEndElement();
        }
    }

    private static void Part(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
