using System.Data;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Runs;

namespace ScriptsManager.Tests;

public sealed class TSqlRunnerTests
{
    [Fact]
    public void SplitByGo()
    {
        var batches = SqlBatches.Split("select 1\r\nGO\r\n\r\nselect 2\n  go 3 -- три раза\nselect 3\ngo\n");

        Assert.Equal(3, batches.Count);
        Assert.Equal(("select 1", 1, 1), (batches[0].Text, batches[0].StartLine, batches[0].Repeat));
        Assert.Equal(("\nselect 2", 3, 3), (batches[1].Text, batches[1].StartLine, batches[1].Repeat));
        Assert.Equal(("select 3", 6, 1), (batches[2].Text, batches[2].StartLine, batches[2].Repeat));
    }

    [Fact]
    public void GoInsideCommentOrIdentifierIsNotSeparator()
    {
        var batches = SqlBatches.Split("/* пример:\ngo\n*/ select 1 -- /* не начало\ngo\nselect goal = 1\ngoto_label:");

        Assert.Equal(2, batches.Count);
        Assert.StartsWith("/* пример:\ngo\n*/", batches[0].Text);
        Assert.Equal("select goal = 1\ngoto_label:", batches[1].Text);
    }

    [Fact]
    public void EmptyScript() => Assert.Empty(SqlBatches.Split("\n  \ngo\n"));

    [Fact]
    public void Parameters()
    {
        var manifest = new ScriptManifest
        {
            Title = "t",
            Runner = RunnerKind.TSql,
            ScriptPath = "t.sql",
            Connections = [new("main", DbKind.MsSql, ConnectionScope.Database), new("other", DbKind.MsSql, ConnectionScope.Database)],
            Parameters =
            [
                new("n", ParameterType.Int),
                new("d", ParameterType.Decimal),
                new("b", ParameterType.Bool),
                new("day", ParameterType.Date),
                new("at", ParameterType.DateTime) { Required = false },
                new("id", ParameterType.Guid),
                new("s", ParameterType.String),
                new("list", ParameterType.List) { Multi = true },
            ],
        };
        var values = new Dictionary<string, object?>
        {
            ["n"] = 5L, ["d"] = 0.5m, ["b"] = true, ["day"] = new DateTime(2026, 1, 2), ["at"] = null,
            ["id"] = Guid.Empty, ["s"] = "x", ["list"] = new[] { "a", "б" },
        };
        var other = new RunConnection(new ConnectionChoice(new SavedConnection(DbKind.MsSql, "myserver", null, AuthKind.Windows, null), "mydatabase"), null);
        var request = new RunRequest(manifest, values, new Dictionary<string, RunConnection> { ["other"] = other });

        var p = TSqlRunner.BuildParameters(request).ToDictionary(x => x.ParameterName);

        Assert.Equal((SqlDbType.BigInt, (object)5L), (p["@n"].SqlDbType, p["@n"].Value));
        Assert.Equal((SqlDbType.Decimal, (byte)38, (byte)10), (p["@d"].SqlDbType, p["@d"].Precision, p["@d"].Scale));
        Assert.Equal(SqlDbType.Bit, p["@b"].SqlDbType);
        Assert.Equal(SqlDbType.Date, p["@day"].SqlDbType);
        Assert.Equal((SqlDbType.DateTime2, (object)DBNull.Value), (p["@at"].SqlDbType, p["@at"].Value));
        Assert.Equal(SqlDbType.UniqueIdentifier, p["@id"].SqlDbType);
        Assert.Equal((SqlDbType.NVarChar, -1), (p["@s"].SqlDbType, p["@s"].Size));
        Assert.Equal("[\"a\",\"б\"]", p["@list"].Value);
        Assert.Equal("myserver", p["@other_server"].Value);
        Assert.Equal("mydatabase", p["@other_database"].Value);
        Assert.False(p.ContainsKey("@main_server"));
    }

    [Fact]
    public void Csv()
    {
        var table = new DataTable();
        table.Columns.Add("c0", typeof(object)).Caption = "Имя";
        table.Columns.Add("c1", typeof(object)).Caption = "Когда";
        table.Columns.Add("c2", typeof(object)).Caption = "Сумма";
        table.Rows.Add("а;б \"в\"", new DateTime(2026, 1, 2, 3, 4, 5), 1.5m);
        table.Rows.Add(DBNull.Value, new DateTime(2026, 1, 2), 2m);

        var writer = new StringWriter { NewLine = "\n" };
        ResultTables.WriteCsv(table, writer);

        Assert.Equal("Имя;Когда;Сумма\n\"а;б \"\"в\"\"\";2026-01-02 03:04:05;1.5\n;2026-01-02;2\n", writer.ToString());
    }

    [Fact]
    public void BinaryDisplay()
    {
        Assert.Equal("0x0A0B", ResultTables.Display(new byte[] { 10, 11 }));
        var big = (string)ResultTables.Display(new byte[ResultTables.MaxBinaryBytes + 1]);
        Assert.EndsWith($"… ({ResultTables.MaxBinaryBytes + 1} байт)", big);
    }
}
