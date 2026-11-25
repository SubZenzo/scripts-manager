using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Tests;

public sealed class ManifestLoaderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ManifestLoadResult Load(string yaml, string scriptFile = "script.sql", bool createScript = true)
    {
        if (createScript)
            _dir.Write(scriptFile, "select 1");
        return ManifestLoader.Load(_dir.Write(".manifests/" + scriptFile + ".yaml", yaml), Path.Combine(_dir.Path, scriptFile));
    }

    private static void AssertError(ManifestLoadResult result, string fragment) =>
        Assert.Contains(result.Issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains(fragment));

    [Fact]
    public void FullManifest()
    {
        var result = Load(
            """
            title: Сообщения в очередях
            description: Просмотр очереди
            runner: tsql
            tags: [Работа с очередями]
            output: [grid]
            confirm: true
            rerun: true
            connections:
              - name: connection
                type: mssql
                scope: database
                default: { server: myserver, database: mydatabase }
              - name: pg
                type: postgres
                scope: server
                default: { server: myserver2, port: 5433 }
            params:
              - { name: fdate, label: Дата с, type: datetime, required: false, default: today-1 }
              - { name: ttop, type: int, default: 100, min: 0, max: 1000 }
              - name: queue
                type: list
                editable: true
                items: [DelayedMessageQueue, ProcessingMessageQueue]
                default: DelayedMessageQueue
              - name: mode
                type: list
                multi: true
                items: [ { key: 1, value: Тест }, { key: 2, value: Бой } ]
                default: [1, 2]
              - { name: ids, type: text }
              - { name: id, type: guid, required: false }
            """);

        Assert.Empty(result.Issues);
        var m = result.Manifest!;
        Assert.Equal("Сообщения в очередях", m.Title);
        Assert.Equal(RunnerKind.TSql, m.Runner);
        Assert.Equal(Path.Combine(_dir.Path, "script.sql"), m.ScriptPath);
        Assert.Equal(["Работа с очередями"], m.Tags);
        Assert.Equal([OutputKind.Grid], m.Output);
        Assert.True(m.Confirm);
        Assert.True(m.Rerun);

        Assert.Equal(new ConnectionTarget("myserver", null, "mydatabase"), m.Connections[0].Default);
        Assert.Equal(DbKind.Postgres, m.Connections[1].Type);
        Assert.Equal(ConnectionScope.Server, m.Connections[1].Scope);
        Assert.Equal(5433, m.Connections[1].Default!.Port);

        var fdate = m.Parameters[0];
        Assert.Equal(("Дата с", ParameterType.DateTime, false), (fdate.Label, fdate.Type, fdate.Required));
        Assert.Equal(["today-1"], fdate.Default);
        Assert.Equal((0m, 1000m), (m.Parameters[1].Min!.Value, m.Parameters[1].Max!.Value));
        Assert.True(m.Parameters[2].Editable);
        Assert.Equal(new ListItem("2", "Бой"), m.Parameters[3].Items[1]);
        Assert.Equal(["1", "2"], m.Parameters[3].Default);
        Assert.True(m.Parameters[4].Multiline);
        Assert.Equal("ids", m.Parameters[4].Label);
    }
    [Fact]
    public void Defaults()
    {
        var result = Load("runner: powershell", scriptFile: "my-script.ps1");

        Assert.False(result.HasErrors);
        var m = result.Manifest!;
        Assert.Equal("my-script", m.Title);
        Assert.Equal([OutputKind.Grid, OutputKind.Log], m.Output);
        Assert.False(m.Confirm);
        Assert.False(m.Rerun);
        Assert.Empty(m.Parameters);
    }

    [Fact]
    public void RunnerMustMatchScriptFile()
    {
        var result = Load("runner: python", scriptFile: "decode.sql");

        AssertError(result, "не подходит к файлу decode.sql");
    }

    [Fact]
    public void MissingScriptFile()
    {
        var result = Load("runner: tsql", createScript: false);

        Assert.True(result.HasErrors);
        Assert.NotNull(result.Manifest);
        AssertError(result, "Файл скрипта не найден");
    }

    [Fact]
    public void MissingRunner()
    {
        var result = Load("title: x");

        Assert.Null(result.Manifest);
        AssertError(result, "Не указано runner");
    }

    [Fact]
    public void UnknownRunner()
    {
        var result = Load("runner: bash");

        Assert.Null(result.Manifest);
        AssertError(result, "«bash»");
    }

    [Fact]
    public void YamlSyntaxError()
    {
        var result = Load("runner: tsql\nparams: [\n");

        Assert.Null(result.Manifest);
        var issue = Assert.Single(result.Issues);
        Assert.StartsWith("Ошибка YAML", issue.Message);
        Assert.NotNull(issue.Line);
    }

    [Fact]
    public void UnknownFieldIsWarningWithLine()
    {
        var result = Load("runner: tsql\ntitel: опечатка");

        Assert.False(result.HasErrors);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("titel", issue.Message);
        Assert.Equal(2, issue.Line);
    }

    [Fact]
    public void ParameterErrors()
    {
        var result = Load(
            """
            runner: tsql
            params:
              - { name: a, type: int }
              - { name: A, type: string }
              - { name: 1b }
              - { name: c, type: color }
              - { name: d, type: list }
              - { name: e, type: int, default: abc }
              - { name: f, type: int, min: 10, max: 1 }
              - { name: g, type: list, items: [x, y], default: z }
              - { name: h, type: list, items: [x, y], default: [x, y] }
              - { name: i, type: datetime, default: вчера }
              - { label: без имени }
            """);

        AssertError(result, "«A» указан несколько раз");
        AssertError(result, "«1b» должно состоять");
        AssertError(result, "«color»");
        AssertError(result, "«d»: у списка нет items");
        AssertError(result, "«abc» — не целое число");
        AssertError(result, "«f»: min больше max");
        AssertError(result, "«z» нет в списке");
        AssertError(result, "только у списка с multi: true");
        AssertError(result, "«вчера» — не дата");
        AssertError(result, "Не указано name");
    }

    [Fact]
    public void NotApplicableAttributesAreWarnings()
    {
        var result = Load("runner: tsql\nparams:\n  - { name: a, type: string, min: 1, items: [x] }");

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Issues.Count(i => i.Severity == IssueSeverity.Warning));
    }

    [Fact]
    public void NameConflictBetweenParameterAndConnection()
    {
        var result = Load("runner: tsql\nconnections: [{ name: db, type: mssql }]\nparams: [{ name: db }]");

        AssertError(result, "занято и подключением, и параметром");
    }

    [Fact]
    public void PresetErrors()
    {
        var manifest = Load("runner: tsql\nconnections: [{ name: db, type: mssql }]\nparams: [{ name: n, type: int }]").Manifest!;
        var file = PresetFile.Parse(
            """
            - name: p
              values: { n: x, missing: 1 }
              connections: { db: { database: d }, other: { server: s } }
            - values: {}
            """);

        Assert.Contains(file.Issues, i => i.Message.Contains("подключение «db» задаётся объектом"));
        Assert.Contains(file.Issues, i => i.Message == "У пресета нет name");
        var issues = PresetFile.Validate(file.Presets, manifest);
        Assert.Contains(issues, i => i.Message.Contains("«x» — не целое число"));
        Assert.Contains(issues, i => i.Message.Contains("нет параметра «missing»"));
        Assert.Contains(issues, i => i.Message.Contains("нет подключения «other»"));
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
    }

    [Fact]
    public void ConnectionErrors()
    {
        var result = Load(
            """
            runner: tsql
            connections:
              - { name: a, type: oracle }
              - { name: b, type: postgres, default: { server: s, port: 99999 } }
              - { name: c, type: mssql, scope: server, default: { server: s, database: d } }
            """);

        AssertError(result, "«oracle»");
        AssertError(result, "«99999» — неверный порт");
        Assert.Contains(result.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("scope: server"));
    }
}
