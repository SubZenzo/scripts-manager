using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Runs;

/// <summary>
/// Запуск PowerShell и Python отдельным процессом. Параметры — аргументами, все вместе — JSON в SCRIPT_PARAMS,
/// подключения — переменными DB_&lt;СЛОТ&gt;_*, таблицы — файлы *.csv и *.json из RESULT_DIR.
/// </summary>
public sealed class ProcessRunner(RunnerKind kind, string? interpreter) : IScriptRunner
{
    public async Task RunAsync(RunRequest request, IRunOutput output, CancellationToken ct)
    {
        var manifest = request.Manifest;
        var runDir = Directory.CreateTempSubdirectory("dsm-run-").FullName;
        var resultDir = Directory.CreateDirectory(Path.Combine(runDir, "result")).FullName;
        try
        {
            if (request.OutputDir is { } outputDir)
                Directory.CreateDirectory(outputDir);
            var psi = kind == RunnerKind.PowerShell ? PowerShell(request, runDir) : Python(request);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Path.GetDirectoryName(manifest.ScriptPath)!;
            foreach (var (name, value) in Environment(request, resultDir))
                psi.Environment[name] = value;

            foreach (var (slot, connection) in request.Connections)
                output.Log(LogKind.Info, $"Подключение {slot}: {connection.Choice.Display}");
            output.Log(LogKind.Info, $"Запуск: {psi.FileName} {Path.GetFileName(manifest.ScriptPath)}");
            if (request.OutputDir is not null)
                output.Log(LogKind.Info, $"OUTPUT_DIR: {request.OutputDir}");

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    output.Log(LogKind.Message, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    output.Log(LogKind.Error, e.Data);
            };

            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                // Текст системной ошибки без английской обёртки .NET («An error occurred trying to start process…»).
                var reason = new Win32Exception(ex.NativeErrorCode).Message.TrimEnd('.');
                throw new ScriptFailedException($"Не удалось запустить {psi.FileName}: {reason}. Путь к интерпретатору задаётся в настройках");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await using (ct.Register(() => Kill(process)))
                await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();

            foreach (var table in ResultFiles.Load(resultDir, output))
            {
                output.Log(LogKind.Info, $"{table.TableName}: {table.Rows.Count} строк");
                output.Result(table);
            }

            if (process.ExitCode != 0)
                throw new ScriptFailedException($"Скрипт завершился с кодом {process.ExitCode}");
        }
        finally
        {
            try
            {
                Directory.Delete(runDir, recursive: true);
            }
            catch (IOException)
            {
                // Файл мог остаться занятым дочерним процессом; временная папка — не страшно.
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Уже завершился.
        }
    }

    /// <summary>
    /// Скрипт вызывается из временной обёртки через -File: при -EncodedCommand Windows PowerShell пишет ошибки
    /// в stderr как CLIXML, а напрямую через -File нельзя передать массив и switch со значением.
    /// </summary>
    private ProcessStartInfo PowerShell(RunRequest request, string runDir)
    {
        var wrapper = Path.Combine(runDir, "run.ps1");
        // С BOM — иначе Windows PowerShell прочитает кириллицу в значениях как ANSI.
        File.WriteAllText(wrapper, PowerShellCommand(request), new UTF8Encoding(true));

        var psi = new ProcessStartInfo(Executables.Find(string.IsNullOrWhiteSpace(interpreter) ? "powershell.exe" : interpreter));
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper })
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>Текст обёртки: вызов скрипта с именованными аргументами и код возврата (1 — скрипт завершился ошибкой).</summary>
    public static string PowerShellCommand(RunRequest request)
    {
        var sb = new StringBuilder("$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; & ")
            .Append(Quote(request.Manifest.ScriptPath));
        foreach (var p in request.Manifest.Parameters)
        {
            switch (request.Values.GetValueOrDefault(p.Name))
            {
                case null:
                    // Не передаём — сработает значение по умолчанию из param().
                    break;
                case bool b:
                    sb.Append($" -{p.Name}:${(b ? "true" : "false")}");
                    break;
                case string[] list:
                    sb.Append($" -{p.Name} @(").Append(string.Join(", ", list.Select(Quote))).Append(')');
                    break;
                case long[] numbers:
                    sb.Append($" -{p.Name} @(").Append(string.Join(", ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)))).Append(')');
                    break;
                case var value:
                    sb.Append($" -{p.Name} ").Append(Quote(RunValues.Text(value)!));
                    break;
            }
        }

        // Сначала явный exit N из скрипта, затем ошибка без кода ($? ложно и после exit N, поэтому порядок важен).
        return sb.Append("; $ok = $?; if ($LASTEXITCODE) { exit $LASTEXITCODE } elseif (-not $ok) { exit 1 }").ToString();

        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
    }

    private ProcessStartInfo Python(RunRequest request)
    {
        var psi = new ProcessStartInfo(Executables.Find(string.IsNullOrWhiteSpace(interpreter) ? "python" : interpreter));
        foreach (var arg in PythonArguments(request))
            psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    /// <summary><c>-u скрипт --имя значение</c>; список — <c>--имя a b</c> (argparse: nargs="*"); да/нет — флаг <c>--имя</c>, если да; null не передаётся.</summary>
    public static IReadOnlyList<string> PythonArguments(RunRequest request)
    {
        var args = new List<string> { "-u", request.Manifest.ScriptPath };
        foreach (var p in request.Manifest.Parameters)
        {
            var value = request.Values.GetValueOrDefault(p.Name);
            if (value is null or false)
                continue;
            args.Add("--" + p.Name);
            if (value is true)
                continue; // argparse: action="store_true"
            if (value is string[] list)
                args.AddRange(list);
            else if (value is long[] numbers)
                args.AddRange(numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
            else
                args.Add(RunValues.Text(value)!);
        }

        return args;
    }

    /// <summary>Переменные окружения: SCRIPT_PARAMS, RESULT_DIR, DB_&lt;СЛОТ&gt;_* и PG* для первого подключения PostgreSQL.</summary>
    public static IReadOnlyDictionary<string, string> Environment(RunRequest request, string resultDir)
    {
        var env = new Dictionary<string, string>
        {
            ["SCRIPT_PARAMS"] = RunValues.Json(request.Values),
            ["RESULT_DIR"] = resultDir,
        };
        if (request.OutputDir is { } outputDir)
            env["OUTPUT_DIR"] = outputDir;

        var pgSet = false;
        foreach (var slot in request.Manifest.Connections)
        {
            if (!request.Connections.TryGetValue(slot.Name, out var run))
                continue;
            var c = run.Choice.Connection;
            var prefix = $"DB_{slot.Name.ToUpperInvariant()}_";
            env[prefix + "CONNSTR"] = ConnectionFactory.PortableConnectionString(c, run.Choice.Database, run.Password);
            env[prefix + "SERVER"] = c.Server.Trim();
            env[prefix + "PORT"] = c.Port?.ToString() ?? (c.Type == DbKind.Postgres ? "5432" : "");
            env[prefix + "DATABASE"] = run.Choice.Database ?? "";
            env[prefix + "USER"] = c.User ?? "";
            env[prefix + "PASSWORD"] = run.Password ?? "";
            env[prefix + "INTEGRATED"] = c.Auth == AuthKind.Windows ? "true" : "false";
            if (c.Type == DbKind.MsSql)
                env[prefix + "ODBC"] = ConnectionFactory.OdbcConnectionString(c, run.Choice.Database, run.Password);

            if (c.Type == DbKind.Postgres && !pgSet)
            {
                pgSet = true;
                env["PGHOST"] = c.Server.Trim();
                env["PGPORT"] = (c.Port ?? 5432).ToString();
                env["PGDATABASE"] = run.Choice.Database ?? "postgres";
                env["PGUSER"] = c.User ?? "";
                if (c.Auth == AuthKind.Password)
                    env["PGPASSWORD"] = run.Password ?? "";
            }
        }

        return env;
    }
}
