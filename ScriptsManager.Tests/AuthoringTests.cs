using ScriptsManager.Core.Authoring;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Tests;

public sealed class AuthoringTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private const string TSqlScript = """
        set nocount on;
        -- параметры
        declare @from datetime = dateadd(day, -1, getdate()), @to datetime = getdate();
        declare @top int = 100
        declare @name nvarchar(50) = N'it''s', @note varchar(max), @flag bit = 1
        declare @t table (id int, name sysname)
        DECLARE @Id uniqueidentifier = NULL;
        declare @calc int = (select count(*) from sys.objects)
        declare @day date = '20260102'

        select top (@top) * from sys.objects where create_date between @from and @to
        declare @later int = 1
        """;

    [Fact]
    public void TSqlDeclarationsBecomeParameters()
    {
        var found = ScriptAnalyzer.Analyze(RunnerKind.TSql, TSqlScript).Parameters;

        Assert.Equal(["from", "to", "top", "name", "note", "flag", "Id", "calc", "day"], found.Select(f => f.Parameter.Name));
        var p = found.ToDictionary(f => f.Parameter.Name, f => f.Parameter);
        Assert.Equal((ParameterType.DateTime, "now-1d"), (p["from"].Type, p["from"].Default.Single()));
        Assert.Equal("now", p["to"].Default.Single());
        Assert.Equal((ParameterType.Int, "100", true), (p["top"].Type, p["top"].Default.Single(), p["top"].Required));
        Assert.Equal("it's", p["name"].Default.Single());
        Assert.True(p["note"].Multiline);
        Assert.False(p["note"].Required);
        Assert.Equal((ParameterType.Bool, "true"), (p["flag"].Type, p["flag"].Default.Single()));
        Assert.Equal((ParameterType.Guid, false), (p["Id"].Type, p["Id"].Required));
        Assert.Empty(p["calc"].Default);
        Assert.NotNull(found.Single(f => f.Parameter.Name == "calc").Note);
        Assert.Equal((ParameterType.Date, "2026-01-02"), (p["day"].Type, p["day"].Default.Single()));
    }

    [Fact]
    public void TSqlRemovesOnlySelectedDeclarations()
    {
        var text = TSqlDeclarations.Remove(TSqlScript, ["from", "to", "top", "note", "Id"]);

        Assert.Equal("""
            set nocount on;
            -- параметры
            declare @name nvarchar(50) = N'it''s', @flag bit = 1
            declare @t table (id int, name sysname)
            declare @calc int = (select count(*) from sys.objects)
            declare @day date = '20260102'

            select top (@top) * from sys.objects where create_date between @from and @to
            declare @later int = 1
            """, text);
    }

    private static readonly ScriptParameter[] RegionParameters =
    [
        new("from", ParameterType.DateTime),
        new("top", ParameterType.Int) { Default = ["10"] },
        new("flag", ParameterType.Bool) { Default = ["true"] },
        new("d", ParameterType.Date) { Default = ["today-7"] },
        new("list", ParameterType.List) { Multi = true, Editable = true, Default = ["a", "б"] },
        new("s", ParameterType.String) { Default = ["it's"] },
    ];

    [Fact]
    public void TSqlParamsRegionCollectsDeclarations()
    {
        var text = TSqlParamsRegion.Update("set nocount on;\ndeclare @from datetime = dateadd(month, -1, getdate()), @x int = 5\nselect @from, @x\n",
            RegionParameters, ["arch"]);

        Assert.Equal("""
            --#region Params
            -- Для запуска вне Scripts Manager: приложение пропускает этот блок и передаёт параметры само
            declare @from datetime = dateadd(month, -1, getdate());
            declare @top bigint = 10;
            declare @flag bit = 1;
            declare @d date = dateadd(day, -7, cast(getdate() as date));
            declare @list nvarchar(max) = N'["a","б"]';
            declare @s nvarchar(max) = N'it''s';
            declare @arch_server nvarchar(max);
            declare @arch_database nvarchar(max);
            --#endregion
            set nocount on;
            declare @x int = 5
            select @from, @x

            """.ReplaceLineEndings("\n"), text);

        // Повторное сохранение ничего не меняет, блок остаётся на месте.
        Assert.Equal(text, TSqlParamsRegion.Update(text, RegionParameters, ["arch"]));
        var moved = "-- шапка\r\n" + text.ReplaceLineEndings("\r\n");
        Assert.Equal(moved, TSqlParamsRegion.Update(moved, RegionParameters, ["arch"]));
    }

    [Fact]
    public void TSqlParamsRegionFollowsParameterChanges()
    {
        var text = TSqlParamsRegion.Update("declare @top int = 5;\nselect @top", [new("top", ParameterType.Int) { Default = ["5"] }], []);
        Assert.Contains("declare @top int = 5;", text);

        // Тип из скрипта сохраняется, значение — новое; тип другого вида — по параметру.
        Assert.Contains("declare @top int = 20;", TSqlParamsRegion.Update(text, [new("top", ParameterType.Int) { Default = ["20"] }], []));
        Assert.Contains("declare @top nvarchar(max) = N'x';", TSqlParamsRegion.Update(text, [new("top", ParameterType.String) { Default = ["x"] }], []));

        // Без параметров блок убирается.
        Assert.Equal("select @top", TSqlParamsRegion.Update(text, [], []));
    }

    [Fact]
    public void TSqlParamsRegionIsBlankedForRun()
    {
        var text = TSqlParamsRegion.Update("select @top, @s\r\nGO\r\nselect 1", RegionParameters, []);
        var blank = TSqlParamsRegion.Blank(text);

        Assert.DoesNotContain("declare", blank);
        Assert.Equal(text.Split('\n').Length, blank.Split('\n').Length);
        Assert.EndsWith("\r\nselect @top, @s\r\nGO\r\nselect 1", blank);

        var found = ScriptAnalyzer.Analyze(RunnerKind.TSql, text).Parameters;
        Assert.Equal(RegionParameters.Select(p => p.Name), found.Select(f => f.Parameter.Name));
        Assert.All(found, f => Assert.True(f.InParamsRegion));
        Assert.Equal(["today-7d"], found.Single(f => f.Parameter.Name == "d").Parameter.Default);
    }

    [Fact]
    public void TSqlStopsAtFirstStatement()
    {
        Assert.Empty(TSqlDeclarations.Find("select 1\ndeclare @x int = 1"));
        Assert.Empty(TSqlDeclarations.Find("declare c cursor for select 1"));
        Assert.Equal(["a"], TSqlDeclarations.Find("/* шапка */ declare @a as int = case when 1 = 1 then 2 else 3 end\nprint @a").Select(d => d.Name));
    }

    [Fact]
    public void PlPgSqlParameters()
    {
        var found = ScriptAnalyzer.Analyze(RunnerKind.PlPgSql, """
            select * from t where id = @id and name = '@not' -- @neither
            and data @> '{}';
            do $$ begin raise notice '%', current_setting('param.mode', true); end $$;
            """).Parameters;

        Assert.Equal(["id", "mode"], found.Select(f => f.Parameter.Name));
        Assert.True(ScriptAnalyzer.LooksLikePostgres("do $$ begin end $$"));
        Assert.False(ScriptAnalyzer.LooksLikePostgres("select top 10 * from t"));
    }

    [Fact]
    public void PlPgSqlDoBlockDeclarations()
    {
        const string script = """
            do $$
            declare
                _param int := 1; -- число
                _from date := current_date - 1;
                v_name varchar(50) default 'it''s';
                l_flag boolean := true;
                note text;
                r record;
            begin
            raise notice '%', _param;
            end;
            $$
            """;
        var found = ScriptAnalyzer.Analyze(RunnerKind.PlPgSql, script).Parameters.ToDictionary(f => f.Parameter.Name);

        Assert.Equal(["_param", "_from", "v_name", "l_flag", "note"], found.Keys);
        Assert.Equal((ParameterType.Int, true, "1"), (found["_param"].Parameter.Type, found["_param"].Parameter.Required, found["_param"].Parameter.Default[0]));
        Assert.Equal(["today-1d"], found["_from"].Parameter.Default);
        Assert.Equal(["it's"], found["v_name"].Parameter.Default);
        Assert.Equal((ParameterType.Bool, "true"), (found["l_flag"].Parameter.Type, found["l_flag"].Parameter.Default[0]));
        Assert.False(found["note"].Parameter.Required);
        Assert.False(found["_param"].InParamsRegion);

        var parameters = new[] { "_param", "_from", "v_name" }.Select(n => found[n].Parameter)
            .Append(new ScriptParameter("top", ParameterType.Int) { Default = ["10"] })
            .Append(new ScriptParameter("since", ParameterType.DateTime) { Default = ["now-2h"] })
            .ToList();
        var updated = PgDoBlock.Update(script, parameters);

        Assert.Contains("_param int := nullif(coalesce(current_setting('param._param', true), '1'), '')::int; -- число", updated);
        Assert.Contains("_from date := nullif(coalesce(current_setting('param._from', true), (current_date - 1)::text), '')::date;", updated);
        Assert.Contains("v_name varchar(50) := nullif(coalesce(current_setting('param.v_name', true), 'it''s'), '')::varchar(50);", updated);
        Assert.Contains("l_flag boolean := true;", updated);
        Assert.Contains("    r record;\n    top bigint := nullif(coalesce(current_setting('param.top', true), '10'), '')::bigint;\n"
                        + "    since timestamp := nullif(coalesce(current_setting('param.since', true), (localtimestamp(0) - interval '2 hour')::text), '')::timestamp;\nbegin", updated);

        // Повторное сохранение ничего не меняет, а найденное — уже параметры с прежними значениями по умолчанию.
        Assert.Equal(updated, PgDoBlock.Update(updated, parameters));
        var again = ScriptAnalyzer.Analyze(RunnerKind.PlPgSql, updated).Parameters.ToDictionary(f => f.Parameter.Name);
        Assert.True(again["_param"].InParamsRegion);
        Assert.Equal(["1"], again["_param"].Parameter.Default);
        Assert.Equal(["today-1d"], again["_from"].Parameter.Default);
        Assert.Equal(["now-2h"], again["since"].Parameter.Default);
        Assert.False(again["l_flag"].InParamsRegion);
    }

    [Fact]
    public void PlPgSqlDoBlockIndent()
    {
        const string script = "do $$\ndeclare \n_param int := 1; -- число\nbegin\nraise notice '%',  _param ;\nend;\n$$";
        var parameters = new[] { new ScriptParameter("_param", ParameterType.Int) { Default = ["1"] }, new ScriptParameter("param123", ParameterType.String) };

        Assert.Equal("do $$\ndeclare \n    _param int := nullif(coalesce(current_setting('param._param', true), '1'), '')::int; -- число\n"
                     + "    param123 text := nullif(current_setting('param.param123', true), '')::text;\nbegin\nraise notice '%',  _param ;\nend;\n$$",
            PgDoBlock.Update(script, parameters));
        // Уже связанное без отступа — получает отступ.
        Assert.Equal("do $$ declare\n    l_x int := nullif(current_setting('param.l_x', true), '')::int;\nbegin end $$",
            PgDoBlock.Update("do $$ declare\nl_x int := nullif(current_setting('param.l_x', true), '')::int;\nbegin end $$",
                [new ScriptParameter("l_x", ParameterType.Int)]));
    }

    [Fact]
    public void PlPgSqlDoBlockWithoutDeclare()
    {
        var p = new ScriptParameter("id", ParameterType.Guid) { Required = false };

        Assert.Equal("do $tag$\ndeclare\n    id uuid := nullif(current_setting('param.id', true), '')::uuid;\nbegin perform 1; end $tag$;",
            PgDoBlock.Update("do $tag$\nbegin perform 1; end $tag$;", [p]));
        // Без блока DO и для параметров, которые скрипт берёт сам, объявлять нечего.
        Assert.Equal("select @id", PgDoBlock.Update("select @id", [p]));
        const string setting = "do $$ begin raise notice '%', current_setting('param.id', true); end $$";
        Assert.Equal(setting, PgDoBlock.Update(setting, [p]));
    }

    [Fact]
    public void PythonArgparse()
    {
        var analysis = ScriptAnalyzer.Analyze(RunnerKind.Python, """
            import argparse, os
            parser = argparse.ArgumentParser()
            parser.add_argument("--query", required=True, help="Текст, \"в кавычках\"")
            parser.add_argument('-c', '--count', type=int, default=5)
            parser.add_argument("--ratio", default=0.5)
            parser.add_argument("--dry-run", action="store_true")
            parser.add_argument("--mode", choices=["a", "b"], default="a")
            parser.add_argument("--stands", nargs="+", choices=("dev", "stage"))
            parser.add_argument("file")
            conn = os.environ["DB_MAIN_CONNSTR"]
            """);
        var p = analysis.Parameters.ToDictionary(f => f.Parameter.Name, f => f);

        Assert.Equal(["query", "count", "ratio", "dry_run", "mode", "stands", "file"], p.Keys);
        Assert.Equal((ParameterType.String, true, "Текст, \"в кавычках\""), (p["query"].Parameter.Type, p["query"].Parameter.Required, p["query"].Parameter.Help));
        Assert.Equal((ParameterType.Int, "5"), (p["count"].Parameter.Type, p["count"].Parameter.Default.Single()));
        Assert.Equal(ParameterType.Decimal, p["ratio"].Parameter.Type);
        Assert.Equal(ParameterType.Bool, p["dry_run"].Parameter.Type);
        Assert.NotNull(p["dry_run"].Note);
        Assert.Equal(["a", "b"], p["mode"].Parameter.Items.Select(i => i.Key));
        Assert.True(p["stands"].Parameter.Multi);
        Assert.NotNull(p["file"].Note);
        Assert.Equal(["main"], analysis.Connections);
    }

    [Fact]
    public void PowerShellParamJson()
    {
        // Так печатает разбор param() в Windows PowerShell: массив из одного элемента — объектом.
        var found = ScriptAnalyzer.ParsePowerShellParams("""
            {"params":[
              {"name":"folder","type":"string","default":null,"value":null,"mandatory":true,"help":"Куда","set":[]},
              {"name":"count","type":null,"default":"10","value":"10","mandatory":false,"help":null,"set":[]},
              {"name":"dryRun","type":"switch","default":null,"value":null,"mandatory":false,"help":null,"set":[]},
              {"name":"mode","type":"string","default":"'a'","value":"a","mandatory":false,"help":null,"set":"a"},
              {"name":"from","type":"datetime","default":"(Get-Date).AddDays(-1)","value":null,"mandatory":false,"help":null,"set":[]},
              {"name":"enabled","type":"bool","default":"$true","value":null,"mandatory":false,"help":null,"set":[]},
              {"name":"servers","type":"int[]","default":"(19..21) + (1..2) + 7","value":null,"mandatory":false,"help":null,"set":[]}
            ]}
            """).ToDictionary(f => f.Parameter.Name);

        Assert.Equal((ParameterType.String, true, "Куда"), (found["folder"].Parameter.Type, found["folder"].Parameter.Required, found["folder"].Parameter.Help));
        Assert.Equal((ParameterType.Int, "10"), (found["count"].Parameter.Type, found["count"].Parameter.Default.Single()));
        Assert.Equal((ParameterType.Bool, "false"), (found["dryRun"].Parameter.Type, found["dryRun"].Parameter.Default.Single()));
        Assert.Equal((ParameterType.List, "a"), (found["mode"].Parameter.Type, found["mode"].Parameter.Items.Single().Key));
        Assert.Equal((ParameterType.DateTime, false), (found["from"].Parameter.Type, found["from"].Parameter.Required));
        Assert.NotNull(found["from"].Note);
        Assert.Equal("true", found["enabled"].Parameter.Default.Single());
        Assert.Equal((ParameterType.Range, "19..21, 1..2, 7"), (found["servers"].Parameter.Type, found["servers"].Parameter.Default.Single()));
        Assert.Null(found["servers"].Note);
    }

    [Fact]
    public async Task PowerShellParamBlock()
    {
        var analysis = await ScriptAnalyzer.AnalyzeAsync(RunnerKind.PowerShell, """
            <#
            .PARAMETER query
            Запрос к базе
            #>
            param(
                [Parameter(Mandatory)][string]$folder,
                [string]$query,
                [ValidateSet('dev', 'stage')][string[]]$stands = @('dev'),
                [int]$count = 10,
                [switch]$dryRun
            )
            $c = $env:DB_SRC_CONNSTR
            """, null);

        Assert.Null(analysis.Error);
        var p = analysis.Parameters.ToDictionary(f => f.Parameter.Name, f => f.Parameter);
        Assert.Equal(["folder", "query", "stands", "count", "dryRun"], p.Keys);
        Assert.True(p["folder"].Required);
        Assert.Equal("Запрос к базе", p["query"].Help);
        Assert.Equal((ParameterType.List, true), (p["stands"].Type, p["stands"].Multi));
        Assert.Equal(["dev", "stage"], p["stands"].Items.Select(i => i.Key));
        Assert.Equal("10", p["count"].Default.Single());
        Assert.Equal(ParameterType.Bool, p["dryRun"].Type);
        Assert.Equal(["src"], analysis.Connections);
    }

    [Fact]
    public void WrittenManifestLoadsBack()
    {
        var scriptPath = Path.Combine(_dir.Path, "Мой скрипт.sql");
        var manifest = new ScriptManifest
        {
            Title = "Скрипт: проверка #1",
            Description = "Две строки\nи \"кавычки\"",
            Runner = RunnerKind.TSql,
            ScriptPath = Path.Combine(_dir.Path, "Мой скрипт.sql"),
            Tags = ["a, b", "c"],
            Output = [OutputKind.Log],
            Confirm = true,
            Rerun = true,
            Connections =
            [
                new ConnectionSlot("connection", DbKind.MsSql, ConnectionScope.Database) { Default = new("myserver", null, "db") },
                new ConnectionSlot("pg", DbKind.Postgres, ConnectionScope.Server) { Default = new("pg-host", 5433) },
            ],
            Parameters =
            [
                new ScriptParameter("name", ParameterType.String) { Label = "Имя: полное", Help = "- с минуса", Default = ["null"] },
                new ScriptParameter("text", ParameterType.Text) { Required = false },
                new ScriptParameter("count", ParameterType.Int) { Min = 1, Max = 10, Default = ["5"] },
                new ScriptParameter("from", ParameterType.DateTime) { Default = ["today-1d"] },
                new ScriptParameter("mode", ParameterType.List) { Items = [new("k", "Значение"), new("x", "y")], Default = ["k"] },
                new ScriptParameter("stands", ParameterType.List) { Multi = true, Editable = true, Items = [new("dev", "dev")], Default = ["dev"] },
                new ScriptParameter("file", ParameterType.File) { Filter = "CSV|*.csv", Required = false },
            ],
        };
        _dir.Write("Мой скрипт.sql", "select 1");

        var yaml = ManifestWriter.Write(manifest);
        var loaded = ManifestLoader.Parse(yaml, scriptPath);

        Assert.Empty(loaded.Issues);
        var m = loaded.Manifest!;
        Assert.Equal((manifest.Title, manifest.Description, manifest.Confirm, manifest.Rerun), (m.Title, m.Description, m.Confirm, m.Rerun));
        Assert.Equal(manifest.Tags, m.Tags);
        Assert.Equal(manifest.Output, m.Output);
        Assert.Equal(manifest.Connections, m.Connections);
        Assert.Equal(manifest.Parameters.Count, m.Parameters.Count);
        for (var i = 0; i < m.Parameters.Count; i++)
        {
            var (a, b) = (manifest.Parameters[i], m.Parameters[i]);
            Assert.Equal((a.Name, a.Label, a.Type, a.Help, a.Required, a.Min, a.Max, a.Multi, a.Editable, a.Filter),
                (b.Name, b.Label, b.Type, b.Help, b.Required, b.Min, b.Max, b.Multi, b.Editable, b.Filter));
            Assert.Equal(a.Default, b.Default);
            Assert.Equal(a.Items, b.Items);
        }
        Assert.DoesNotContain("script:", yaml);
    }

    [Fact]
    public void PresetFileRoundTrip()
    {
        IReadOnlyList<ScriptPreset> presets =
        [
            new ScriptPreset("Пресет: #1")
            {
                Values = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["count"] = ["7"],
                    ["stands"] = ["dev", "stage"],
                    ["empty"] = [],
                    ["query"] = ["select top (10)\n    *\n\nfrom t -- комментарий"],
                    ["path"] = [@"D:\tmp\Новая папка (2)"],
                },
                Connections = new Dictionary<string, ConnectionTarget> { ["connection"] = new("myserver3", 1433, "x") },
            },
            new ScriptPreset("Второй"),
        ];

        var yaml = PresetFile.Write(presets);
        var loaded = PresetFile.Parse(yaml);

        Assert.Empty(loaded.Issues);
        Assert.Contains("query: |-", yaml);
        Assert.Equal(yaml, PresetFile.Write(loaded.Presets));
        var p = loaded.Presets[0];
        Assert.Equal(presets[0].Name, p.Name);
        foreach (var (name, values) in presets[0].Values)
            Assert.Equal(values, p.Values[name]);
        Assert.Equal(new ConnectionTarget("myserver3", 1433, "x"), p.Connections["connection"]);
        Assert.Equal("Второй", loaded.Presets[1].Name);
    }

    [Fact]
    public void PresetChangesApplySelected()
    {
        static ScriptPreset P(string name, string value) =>
            new(name) { Values = new Dictionary<string, IReadOnlyList<string>> { ["v"] = [value] } };

        IReadOnlyList<ScriptPreset> published = [P("a", "1"), P("b", "1"), P("c", "1")];
        IReadOnlyList<ScriptPreset> current = [P("a", "2"), P("c", "1"), P("d", "1"), P("e", "1")];

        Assert.Equal(
            [new PresetChange("a", PresetChangeKind.Changed), new PresetChange("d", PresetChangeKind.Added),
             new PresetChange("e", PresetChangeKind.Added), new PresetChange("b", PresetChangeKind.Deleted)],
            PresetChanges.Diff(published, current));

        // Опубликовать изменение a и новый d; удаление b и новый e остаются неопубликованными.
        var result = PresetChanges.Apply(published, current, ["a", "d"]);
        Assert.Equal(["a", "b", "c", "d"], result.Select(p => p.Name));
        Assert.Equal(["2"], result[0].Values["v"]);

        Assert.Equal(["a", "c"], PresetChanges.Apply(published, current, ["b"]).Select(p => p.Name).Take(2));
        Assert.DoesNotContain(PresetChanges.Apply(published, current, ["b"]), p => p.Name == "b");
    }

    [Fact]
    public void ReadsUtf8AndCp1251()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Assert.Equal("привет", ScriptFiles.Decode(System.Text.Encoding.GetEncoding(1251).GetBytes("привет")));
        Assert.Equal("привет", ScriptFiles.Decode([.. System.Text.Encoding.UTF8.Preamble, .. System.Text.Encoding.UTF8.GetBytes("привет")]));
        Assert.Equal("привет", ScriptFiles.Decode(System.Text.Encoding.UTF8.GetBytes("привет")));
        Assert.Equal("a_b", ScriptFiles.FileName("a:b"));
    }
}
