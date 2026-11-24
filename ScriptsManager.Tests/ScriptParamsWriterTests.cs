using System.Diagnostics;
using ScriptsManager.Core.Authoring;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Tests;

public sealed class ScriptParamsWriterTests
{
    private static readonly ScriptParameter[] Added =
    [
        new("target", ParameterType.String) { Default = ["it's"], Help = "Куда" },
        new("count", ParameterType.Int) { Default = ["5"] },
        new("dryRun", ParameterType.Bool),
        new("servers", ParameterType.Range) { Default = ["19..21, 7"] },
        new("stands", ParameterType.List) { Multi = true, Items = [new("dev", "dev"), new("stage", "stage"), new("prod", "prod")], Default = ["dev", "stage", "prod"] },
        new("from", ParameterType.Date) { Default = ["today-7"] },
    ];

    private static async Task<string> AddToPowerShell(string script)
    {
        var analysis = await ScriptAnalyzer.AnalyzeAsync(RunnerKind.PowerShell, script, null);
        Assert.Null(analysis.Error);
        var declared = analysis.Parameters.Select(d => d.Parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ScriptParamsWriter.PowerShell(script, Added.Where(p => !declared.Contains(p.Name)).ToList(), analysis.ParamBlock!);
    }

    [Theory]
    [InlineData("<#\n.SYNOPSIS\nДеплой\n#>\nparam(\n    [string]$name = 'x'\n)\n\"$name\"\n")]
    [InlineData("[CmdletBinding()]\nparam()\nWrite-Host 1\n")]
    [InlineData("# комментарий\r\n\r\nWrite-Host $target\r\n")]
    [InlineData("using namespace System.Text\nWrite-Host 1\n")]
    [InlineData("")]
    public async Task PowerShellGetsMissingParameters(string script)
    {
        var text = await AddToPowerShell(script);

        // Результат разбирается, и в нём объявлены все параметры с типами и значениями по умолчанию.
        var again = await ScriptAnalyzer.AnalyzeAsync(RunnerKind.PowerShell, text, null);
        Assert.Null(again.Error);
        var p = again.Parameters.ToDictionary(d => d.Parameter.Name, d => d.Parameter, StringComparer.OrdinalIgnoreCase);
        Assert.All(Added, a => Assert.True(p.ContainsKey(a.Name), a.Name + " в\n" + text));
        Assert.Equal("it's", p["target"].Default.Single());
        Assert.Equal((ParameterType.Int, "5"), (p["count"].Type, p["count"].Default.Single()));
        Assert.Equal(ParameterType.Range, p["servers"].Type);
        Assert.Equal("19..21, 7", p["servers"].Default.Single());
        Assert.Contains("# Куда", text);
        // Повторное сохранение ничего не меняет.
        Assert.Equal(text, await AddToPowerShell(text));
    }

    [Fact]
    public void PythonOwnParser()
    {
        const string script = """
            import argparse

            def main():
                p = argparse.ArgumentParser()
                p.add_argument("--name", default="x")
                args = p.parse_args()
                print(args.name)
            """;

        var text = ScriptParamsWriter.Python(script.ReplaceLineEndings("\n"), Added, Added);

        Assert.Contains("    p.add_argument(\"--count\", type=int, default=5)\n", text);
        Assert.Contains("    p.add_argument(\"--from\", dest=\"from_\", required=True)\n    args = p.parse_args()", text);
        Assert.Contains("p.add_argument(\"--servers\", type=int, nargs=\"*\", default=[19, 20, 21, 7])", text);
        Assert.Contains("p.add_argument(\"--dryRun\", action=\"store_true\")", text);
        // Выражение даты вычисляет приложение — в скрипте значения по умолчанию нет; from — ключевое слово.
        Assert.Contains("p.add_argument(\"--from\", dest=\"from_\", required=True)", text);
        Assert.Equal(Added.Length + 1, ScriptAnalyzer.Analyze(RunnerKind.Python, text).Parameters.Count);
    }

    [Fact]
    public void PythonRegionIsCreatedAndRebuilt()
    {
        const string script = "#!/usr/bin/env python\n\"\"\"Описание\"\"\"\nimport os\nprint(count)\n";

        var text = ScriptParamsWriter.Python(script, [Added[1]], [Added[1]]);
        Assert.StartsWith("#!/usr/bin/env python\n\"\"\"Описание\"\"\"\n#region Params", text);
        Assert.Contains("count = _args.count\n#endregion\n\nimport os", text);

        // Новый параметр — в тот же блок; убранный из манифеста — из блока.
        var rebuilt = ScriptParamsWriter.Python(text, [Added[2]], [Added[2]]);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(rebuilt, "#region Params"));
        Assert.Contains("dryRun = _args.dryRun", rebuilt);
        Assert.DoesNotContain("--count", rebuilt);
        Assert.Equal(rebuilt, ScriptParamsWriter.Python(rebuilt, [Added[2]], []));
    }

    [Fact]
    public void PythonScriptRunsWithArguments()
    {
        var dir = Directory.CreateTempSubdirectory("dsm-py-").FullName;
        try
        {
            var path = Path.Combine(dir, "s.py");
            File.WriteAllText(path, ScriptParamsWriter.Python("print(target, count, dryRun, servers, stands, sep='|')\n", Added, Added));
            var psi = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { path, "--target", "t", "--count", "2", "--dryRun", "--servers", "3", "1", "--from", "2026-01-01" })
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, error);
            Assert.Equal("t|2|True|[3, 1]|['dev', 'stage', 'prod']", output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
