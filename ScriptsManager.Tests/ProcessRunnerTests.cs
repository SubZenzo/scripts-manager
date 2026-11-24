using System.Data;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Runs;
using NpgsqlTypes;

namespace ScriptsManager.Tests;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Output : IRunOutput
    {
        public List<(LogKind Kind, string Text)> Lines { get; } = [];
        public List<DataTable> Tables { get; } = [];

        public void Log(LogKind kind, string text)
        {
            lock (Lines)
                Lines.Add((kind, text));
        }

        public void Result(DataTable table) => Tables.Add(table);
    }

    private static readonly ScriptParameter[] Parameters =
    [
        new("name", ParameterType.String),
        new("count", ParameterType.Int),
        new("dryRun", ParameterType.Bool),
        new("day", ParameterType.Date),
        new("stands", ParameterType.List) { Multi = true },
        new("servers", ParameterType.Range),
        new("empty", ParameterType.String) { Required = false },
    ];

    private static readonly Dictionary<string, object?> Values = new()
    {
        ["name"] = "it's ok", ["count"] = 10L, ["dryRun"] = true, ["day"] = new DateTime(2026, 1, 2),
        ["stands"] = new[] { "dev", "stage", "prod" }, ["servers"] = new long[] { 3, 1, 2 }, ["empty"] = null,
    };

    private RunRequest Request(RunnerKind runner, string scriptFile, params (ConnectionSlot Slot, RunConnection Run)[] connections) => new(
        new ScriptManifest
        {
            Title = "t", Runner = runner, ScriptPath = Path.Combine(_dir.Path, scriptFile),
            Parameters = Parameters, Connections = connections.Select(c => c.Slot).ToList(),
        },
        Values,
        connections.ToDictionary(c => c.Slot.Name, c => c.Run));

    [Fact]
    public void ExecutableIsFoundInPath()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Executables.Find("powershell"), ignoreCase: true);
        Assert.Equal(@"C:\x\python.exe", Executables.Find(@"C:\x\python.exe"));
        Assert.Equal("no-such-tool-42", Executables.Find("no-such-tool-42"));
    }

    [Fact]
    public void PowerShellCommand()
    {
        var command = ProcessRunner.PowerShellCommand(Request(RunnerKind.PowerShell, "a'b.ps1"));

        Assert.Contains($"& '{Path.Combine(_dir.Path, "a''b.ps1")}' -name 'it''s ok' -count '10' -dryRun:$true -day '2026-01-02' -stands @('dev', 'stage', 'prod') -servers @(3, 1, 2);", command);
        Assert.DoesNotContain("-empty", command);
    }

    [Fact]
    public void PythonArguments()
    {
        var args = ProcessRunner.PythonArguments(Request(RunnerKind.Python, "s.py"));

        Assert.Equal(["-u", Path.Combine(_dir.Path, "s.py"), "--name", "it's ok", "--count", "10", "--dryRun",
            "--day", "2026-01-02", "--stands", "dev", "stage", "prod", "--servers", "3", "1", "2"], args);
    }

    [Fact]
    public void EnvironmentVariables()
    {
        var mssql = (new ConnectionSlot("src", DbKind.MsSql, ConnectionScope.Database),
            new RunConnection(new ConnectionChoice(new SavedConnection(DbKind.MsSql, "myserver", null, AuthKind.Windows, null), "mydatabase"), null));
        var pg = (new ConnectionSlot("dst", DbKind.Postgres, ConnectionScope.Database),
            new RunConnection(new ConnectionChoice(new SavedConnection(DbKind.Postgres, "myserver2", 5433, AuthKind.Password, "u"), "mydatabase2"), "p"));

        var env = ProcessRunner.Environment(Request(RunnerKind.Python, "s.py", mssql, pg), "R");

        Assert.Equal("R", env["RESULT_DIR"]);
        Assert.False(env.ContainsKey("OUTPUT_DIR"));
        Assert.Equal(@"C:\out", ProcessRunner.Environment(Request(RunnerKind.Python, "s.py") with { OutputDir = @"C:\out" }, "R")["OUTPUT_DIR"]);
        Assert.Equal("""{"name":"it's ok","count":"10","dryRun":"true","day":"2026-01-02","stands":["dev","stage","prod"],"servers":[3,1,2],"empty":null}""", env["SCRIPT_PARAMS"]);
        Assert.Equal("Data Source=myserver;Application Name=\" Scripts Manager\";TrustServerCertificate=True;MultiSubnetFailover=True;Initial Catalog=mydatabase;Integrated Security=True",
            env["DB_SRC_CONNSTR"]);
        Assert.Equal(("myserver", "mydatabase", "true"), (env["DB_SRC_SERVER"], env["DB_SRC_DATABASE"], env["DB_SRC_INTEGRATED"]));
        Assert.Contains("Trusted_Connection=yes", env["DB_SRC_ODBC"]);
        Assert.Equal(("myserver2", "5433", "u", "p"), (env["DB_DST_SERVER"], env["DB_DST_PORT"], env["DB_DST_USER"], env["DB_DST_PASSWORD"]));
        Assert.Equal(("myserver2", "5433", "mydatabase2", "u", "p"), (env["PGHOST"], env["PGPORT"], env["PGDATABASE"], env["PGUSER"], env["PGPASSWORD"]));
        Assert.Contains("Host=myserver2", env["DB_DST_CONNSTR"]);
    }

    [Fact]
    public async Task RunsPowerShell()
    {
        _dir.Write("s.ps1", """
            param([string]$name, [int]$count, [switch]$dryRun, [string]$day, [string[]]$stands, [int[]]$servers, [string]$empty = 'по умолчанию')
            "name=$name count=$count dryRun=$dryRun day=$day stands=$($stands -join '|') servers=$($servers -join '|') empty=$empty"
            "params=$env:SCRIPT_PARAMS"
            [Console]::Error.WriteLine('в stderr')
            "a;b`n1;кириллица" | Set-Content (Join-Path $env:RESULT_DIR 'r.csv') -Encoding UTF8
            'лог' | Set-Content (Join-Path $env:OUTPUT_DIR 'log.txt') -Encoding UTF8
            exit 3
            """, bom: true);
        var output = new Output();
        var outputDir = Path.Combine(_dir.Path, ".output", "s");

        var ex = await Assert.ThrowsAsync<ScriptFailedException>(() => new ProcessRunner(RunnerKind.PowerShell, null)
            .RunAsync(Request(RunnerKind.PowerShell, "s.ps1") with { OutputDir = outputDir }, output, CancellationToken.None));

        Assert.Contains("кодом 3", ex.Message);
        Assert.Contains((LogKind.Message, "name=it's ok count=10 dryRun=True day=2026-01-02 stands=dev|stage|prod servers=3|1|2 empty=по умолчанию"), output.Lines);
        Assert.Equal("лог", File.ReadAllText(Path.Combine(outputDir, "log.txt")).Trim());
        Assert.Contains(output.Lines, l => l.Kind == LogKind.Message && l.Text.StartsWith("params={\"name\":\"it's ok\""));
        Assert.Contains((LogKind.Error, "в stderr"), output.Lines);
        var table = Assert.Single(output.Tables);
        Assert.Equal(["a", "b"], table.Columns.Cast<DataColumn>().Select(c => c.Caption));
        Assert.Equal(["1", "кириллица"], table.Rows[0].ItemArray);
    }

    [Fact]
    public async Task PowerShellErrorFailsRun()
    {
        _dir.Write("s.ps1", "param([string]$name)\nthrow 'сломалось'", bom: true);
        var output = new Output();

        await Assert.ThrowsAsync<ScriptFailedException>(() =>
            new ProcessRunner(RunnerKind.PowerShell, null).RunAsync(Request(RunnerKind.PowerShell, "s.ps1"), output, CancellationToken.None));

        Assert.Contains(output.Lines, l => l.Kind == LogKind.Error && l.Text.Contains("сломалось"));
    }

    [Fact]
    public async Task StopKillsProcess()
    {
        _dir.Write("s.ps1", "param([string]$name)\n'старт'\nStart-Sleep -Seconds 30", bom: true);
        var output = new Output();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = DateTime.Now;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner(RunnerKind.PowerShell, null).RunAsync(Request(RunnerKind.PowerShell, "s.ps1"), output, cts.Token));

        Assert.True(DateTime.Now - started < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task MissingInterpreter()
    {
        _dir.Write("s.py", "print(1)");

        var ex = await Assert.ThrowsAsync<ScriptFailedException>(() =>
            new ProcessRunner(RunnerKind.Python, Path.Combine(_dir.Path, "nope", "python.exe"))
                .RunAsync(Request(RunnerKind.Python, "s.py"), new Output(), CancellationToken.None));

        Assert.Contains("настройках", ex.Message);
    }

    [Fact]
    public void ResultCsvAndJson()
    {
        var csv = ResultFiles.ReadCsv("﻿id,text\r\n1,\"a, \"\"b\"\"\nc\"\r\n2,\r\n");
        Assert.Equal(["id", "text"], csv.Columns.Cast<DataColumn>().Select(c => c.Caption));
        Assert.Equal(["1", "a, \"b\"\nc"], csv.Rows[0].ItemArray);
        Assert.Equal(2, csv.Rows.Count);

        var json = ResultFiles.ReadJson("""[{"a":1,"b":"x"},{"b":null,"c":true,"a":1.5}]""");
        Assert.Equal(["a", "b", "c"], json.Columns.Cast<DataColumn>().Select(c => c.Caption));
        Assert.Equal([1L, "x", DBNull.Value], json.Rows[0].ItemArray);
        Assert.Equal([1.5m, DBNull.Value, true], json.Rows[1].ItemArray);
    }

    [Fact]
    public void PostgresParameters()
    {
        Assert.Equal(NpgsqlDbType.Bigint, PlPgSqlRunner.CreateParameter(new("n", ParameterType.Int), 1L).NpgsqlDbType);
        Assert.Equal(NpgsqlDbType.Timestamp, PlPgSqlRunner.CreateParameter(new("d", ParameterType.DateTime), DateTime.Now).NpgsqlDbType);
        var list = PlPgSqlRunner.CreateParameter(new("l", ParameterType.List) { Multi = true }, new[] { "a", "б" });
        Assert.Equal((NpgsqlDbType.Text, (object)"[\"a\",\"б\"]"), (list.NpgsqlDbType, list.Value));
        Assert.Equal(DBNull.Value, PlPgSqlRunner.CreateParameter(new("s", ParameterType.String), null).Value);
    }
}
