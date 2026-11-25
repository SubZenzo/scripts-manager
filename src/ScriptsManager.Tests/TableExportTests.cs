using System.Data;
using System.IO.Compression;
using System.Xml.Linq;
using ScriptsManager.Core.Runs;

namespace ScriptsManager.Tests;

public sealed class TableExportTests
{
    private static DataTable Sample()
    {
        var table = new DataTable();
        table.Columns.Add("c0", typeof(object)).Caption = "id";
        table.Columns.Add("c1", typeof(object)).Caption = "Имя";
        table.Columns.Add("c2", typeof(object)).Caption = "Когда";
        table.Columns.Add("c3", typeof(object)).Caption = "id";
        table.Rows.Add(1, "a|b'c", new DateTime(2026, 1, 2), true);
        table.Rows.Add(20, DBNull.Value, new DateTime(2026, 1, 2, 3, 4, 5), false);
        return table;
    }

    [Fact]
    public void Tsv()
    {
        var table = Sample();
        var all = TableSlice.All(table);
        Assert.Equal("id\tИмя\tКогда\tid\r\n1\ta|b'c\t2026-01-02\tTrue\r\n20\t\t2026-01-02 03:04:05\tFalse\r\n",
            TableExport.Tsv(all, headers: true));

        // Невыделенные ячейки прямоугольника — пустые; значения с табуляцией и переводом строки — в кавычках.
        table.Rows[0][1] = "x\ty";
        var part = new TableSlice([table.Columns[0], table.Columns[1]], [table.Rows[0], table.Rows[1]])
        {
            Selected = new HashSet<(DataRow, DataColumn)> { (table.Rows[0], table.Columns[1]), (table.Rows[1], table.Columns[0]) },
        };
        Assert.Equal("\t\"x\ty\"\r\n20\t\r\n", TableExport.Tsv(part, headers: false));
    }

    [Fact]
    public void Markdown()
    {
        var md = TableExport.Markdown(TableSlice.All(Sample()));

        Assert.Equal("""
            |  id | Имя    | Когда               | id    |
            | --: | ------ | ------------------- | ----- |
            |   1 | a\|b'c | 2026-01-02          | True  |
            |  20 | NULL   | 2026-01-02 03:04:05 | False |

            """.ReplaceLineEndings(), md);
    }

    [Fact]
    public void Jira()
    {
        var table = Sample();
        table.Rows[1][1] = "";
        table.Rows.Add(3, "две\nстроки", DBNull.Value, DBNull.Value);

        Assert.Equal("""
            ||id||Имя||Когда||id||
            |1|a\|b'c|2026-01-02|True|
            |20| |2026-01-02 03:04:05|False|
            |3|две \\ строки|NULL|NULL|

            """.ReplaceLineEndings(), TableExport.Jira(TableSlice.All(table)));
    }

    [Fact]
    public void Select()
    {
        var all = TableSlice.All(Sample());

        Assert.Equal("""
            select *
            from (values
                (1, 'a|b''c', cast('2026-01-02' as date), 1),
                (20, null, cast('2026-01-02 03:04:05' as datetime2), 0)
            ) as t ([id], [Имя], [Когда], [id_2]);

            """.ReplaceLineEndings(), TableExport.Select(all, SqlDialect.TSql));

        Assert.Equal("""
            select *
            from (values
                (1, 'a|b''c', date '2026-01-02', true),
                (20, null, timestamp '2026-01-02 03:04:05', false)
            ) as t ("id", "Имя", "Когда", "id_2");

            """.ReplaceLineEndings(), TableExport.Select(all, SqlDialect.PostgreSql));

        Assert.Equal("N'привет'", TableExport.Literal("привет", SqlDialect.TSql));
        Assert.Equal("'привет'", TableExport.Literal("привет", SqlDialect.PostgreSql));
        Assert.EndsWith("where 1 = 0;" + Environment.NewLine, TableExport.Select(all with { Rows = [] }, SqlDialect.TSql));
    }

    [Fact]
    public void Xlsx()
    {
        var table = Sample();
        table.Rows.Add(12345678901234567L, "\u0001ok", DBNull.Value, DBNull.Value);
        using var stream = new MemoryStream();
        TableExport.WriteXlsx(TableSlice.All(table), stream);

        using var zip = new ZipArchive(new MemoryStream(stream.ToArray()));
        Assert.NotNull(zip.GetEntry("xl/styles.xml"));
        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = sheet.Descendants(ns + "row").Select(r => r.Elements(ns + "c").ToList()).ToList();

        Assert.Equal(4, rows.Count);
        Assert.Equal("Имя", rows[0][1].Value);
        Assert.Equal("1", rows[1][0].Element(ns + "v")!.Value);
        Assert.Equal("1", (string?)rows[1][2].Attribute("s"));
        Assert.Equal("2", (string?)rows[2][2].Attribute("s"));
        Assert.Equal("b", (string?)rows[1][3].Attribute("t"));
        // Больше 15 цифр — текстом; недопустимые в XML символы выброшены.
        Assert.Equal("inlineStr", (string?)rows[3][0].Attribute("t"));
        Assert.Equal("ok", rows[3][1].Value);
    }
}
