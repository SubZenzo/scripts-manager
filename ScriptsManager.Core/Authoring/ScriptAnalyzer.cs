using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Authoring;

/// <summary>Параметр, найденный в тексте скрипта.</summary>
/// <param name="Source">Откуда взят, например <c>@from datetime = getdate()</c>.</param>
/// <param name="Note">Что стоит проверить вручную.</param>
/// <param name="InParamsRegion">T-SQL: объявлен в блоке <c>--#region Params</c>, то есть уже был параметром.</param>
public sealed record DetectedParameter(ScriptParameter Parameter, string Source, string? Note = null, bool InParamsRegion = false);

/// <param name="Connections">Имена подключений, на которые ссылается скрипт (<c>DB_&lt;ИМЯ&gt;_*</c>).</param>
/// <param name="ParamBlock">PowerShell: куда дописывать параметры в param() (см. <see cref="ScriptParamsWriter"/>).</param>
public sealed record ScriptAnalysis(IReadOnlyList<DetectedParameter> Parameters, IReadOnlyList<string> Connections, string? Error = null,
    PowerShellParamBlock? ParamBlock = null);

/// <summary>
/// Ищет параметры в тексте скрипта: T-SQL — DECLARE в начале, PL/pgSQL — <c>@имя</c> и <c>current_setting('param.имя')</c>,
/// PowerShell — блок <c>param()</c>, Python — <c>add_argument</c> из argparse. Тип без явного указания — строка.
/// </summary>
public static partial class ScriptAnalyzer
{
    /// <param name="powerShell">Путь к powershell.exe/pwsh.exe для разбора param(); пусто — powershell.exe.</param>
    public static async Task<ScriptAnalysis> AnalyzeAsync(RunnerKind runner, string text, string? powerShell, CancellationToken ct = default) =>
        runner == RunnerKind.PowerShell
            ? await AnalyzePowerShellAsync(text, powerShell, ct)
            : Analyze(runner, text);

    public static ScriptAnalysis Analyze(RunnerKind runner, string text) => runner switch
    {
        RunnerKind.TSql => new(TSql(text), []),
        RunnerKind.PlPgSql => new(PlPgSql(text), []),
        RunnerKind.Python => new(Python(text), EnvConnections(text)),
        RunnerKind.PowerShell => throw new InvalidOperationException("PowerShell разбирается асинхронно: AnalyzeAsync"),
        _ => throw new ArgumentOutOfRangeException(nameof(runner)),
    };

    /// <summary>Похож ли SQL на PostgreSQL: по нему выбирается runner для .sql.</summary>
    public static bool LooksLikePostgres(string text) => PostgresHintRegex().IsMatch(text);

    // ---- T-SQL ----

    private static List<DetectedParameter> TSql(string text)
    {
        var declared = TSqlParamsRegion.Names(text);
        return TSqlDeclarations.Find(text).Select(d =>
        {
            var type = SqlType(d.SqlType, out var multiline);
            var (defaults, note) = SqlDefault(d.Initializer, type);
            var p = new ScriptParameter(d.Name, type)
            {
                Multiline = multiline,
                Required = d.Initializer is not null && !IsSqlNull(d.Initializer),
                Default = defaults,
            };
            var source = $"@{d.Name} {d.SqlType}" + (d.Initializer is null ? "" : " = " + d.Initializer);
            return new DetectedParameter(p, source, note, declared.Contains(d.Name));
        }).ToList();
    }

    internal static ParameterType SqlType(string sqlType, out bool multiline)
    {
        var name = sqlType.Split('(')[0].Trim().Trim('[', ']').ToLowerInvariant();
        multiline = false;
        switch (name)
        {
            case "int" or "bigint" or "smallint" or "tinyint":
                return ParameterType.Int;
            case "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real":
                return ParameterType.Decimal;
            case "bit":
                return ParameterType.Bool;
            case "date":
                return ParameterType.Date;
            case "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset":
                return ParameterType.DateTime;
            case "uniqueidentifier":
                return ParameterType.Guid;
            default:
                multiline = sqlType.Contains("(max)", StringComparison.OrdinalIgnoreCase) || name is "text" or "ntext" or "xml";
                return ParameterType.String;
        }
    }

    private static bool IsSqlNull(string expression) => expression.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);

    /// <summary>Значение по умолчанию из выражения: литерал или текущая дата со сдвигом; иначе — пусто и пояснение.</summary>
    internal static (IReadOnlyList<string> Values, string? Note) SqlDefault(string? expression, ParameterType type)
    {
        if (expression is null || IsSqlNull(expression))
            return ([], null);

        var raw = expression.Trim();
        var m = SqlStringRegex().Match(raw);
        if (m.Success)
        {
            var value = m.Groups[1].Value.Replace("''", "'");
            if (type is ParameterType.Date or ParameterType.DateTime)
            {
                return DateTime.TryParseExact(value, SqlDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? ([ParameterValues.FormatDate(d, type == ParameterType.DateTime)], null)
                    : ([], $"Дата «{value}» не распознана");
            }
            return ([value], null);
        }

        var compact = WhitespaceRegex().Replace(raw, "").ToLowerInvariant();
        if (SqlNumberRegex().IsMatch(compact))
        {
            if (type == ParameterType.Bool)
                return compact is "0" or "1" ? ([compact == "1" ? "true" : "false"], null) : ([], null);
            return ([compact.TrimStart('+')], null);
        }

        if (SqlDateExpression(compact) is { } dateExpression && type is ParameterType.Date or ParameterType.DateTime)
            return ([dateExpression], null);

        return ([], $"Значение в скрипте вычисляется ({raw}) — задайте значение по умолчанию вручную");
    }

    private static readonly string[] SqlDateFormats =
    [
        "yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyyMMdd HH:mm", "yyyyMMdd HH:mm:ss", "dd.MM.yyyy",
    ];

    /// <summary><c>getdate()</c> → now, <c>cast(getdate() as date)</c> → today, <c>dateadd(day, -1, …)</c> → today-1d.</summary>
    private static string? SqlDateExpression(string compact)
    {
        if (compact is "getdate()" or "sysdatetime()" or "current_timestamp" or "sysdatetimeoffset()")
            return "now";
        if (SqlTodayRegex().IsMatch(compact))
            return "today";

        var m = SqlDateAddRegex().Match(compact);
        if (!m.Success || SqlDateExpression(m.Groups["base"].Value) is not { } baseExpression)
            return null;
        var unit = m.Groups["unit"].Value switch
        {
            "day" or "dd" or "d" => "d",
            "hour" or "hh" => "h",
            "minute" or "mi" or "n" => "m",
            _ => null,
        };
        if (unit is null || !int.TryParse(m.Groups["amount"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
            return null;
        if (amount == 0)
            return baseExpression;
        if (baseExpression is not ("now" or "today"))
            return null;
        return $"{baseExpression}{(amount < 0 ? "-" : "+")}{Math.Abs(amount)}{unit}";
    }

    // ---- PL/pgSQL ----

    private static List<DetectedParameter> PlPgSql(string text)
    {
        // Переменные DECLARE блока DO (имя параметра — как у переменной); связанные с параметром — уже параметры.
        var result = new List<DetectedParameter>();
        foreach (var d in PgDoBlock.Find(text))
        {
            if (!IdentifierRegex().IsMatch(d.Name)
                || result.Any(r => r.Parameter.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var type = PgType(d.SqlType, out var multiline);
            var initializer = d.IsParameter ? PgDoBlock.Fallback(d.Initializer!) : d.Initializer;
            var (defaults, note) = PgDefault(initializer, type);
            var p = new ScriptParameter(d.Name, type)
            {
                Multiline = multiline,
                Required = initializer is not null && !IsSqlNull(initializer),
                Default = defaults,
            };
            result.Add(new DetectedParameter(p, $"{d.Name} {d.SqlType}" + (d.Initializer is null ? "" : " := " + d.Initializer),
                note, d.IsParameter));
        }

        var names = new List<(string Name, string Source)>();
        void Add(string name, string source)
        {
            if (!names.Any(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                names.Add((name, source));
        }

        // @имя — вне строк, комментариев и $$-блоков (внутри DO параметры доступны только через current_setting).
        foreach (Match m in PgSegmentRegex().Matches(text))
        {
            if (m.Groups["code"].Success)
            {
                foreach (Match v in PgParameterRegex().Matches(m.Value))
                    Add(v.Groups[1].Value, "@" + v.Groups[1].Value);
            }
        }

        foreach (Match m in PgSettingRegex().Matches(text))
            Add(m.Groups[1].Value, m.Value);

        result.AddRange(names
            .Where(n => IdentifierRegex().IsMatch(n.Name)
                        && !result.Any(r => r.Parameter.Name.Equals(n.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(n => new DetectedParameter(new ScriptParameter(n.Name, ParameterType.String), n.Source)));
        return result;
    }

    private static ParameterType PgType(string sqlType, out bool multiline)
    {
        multiline = false;
        var name = sqlType.Split('(')[0].Trim().ToLowerInvariant();
        switch (name)
        {
            case "int" or "integer" or "int2" or "int4" or "int8" or "bigint" or "smallint":
                return ParameterType.Int;
            case "numeric" or "decimal" or "real" or "double precision" or "float4" or "float8" or "money":
                return ParameterType.Decimal;
            case "bool" or "boolean":
                return ParameterType.Bool;
            case "date":
                return ParameterType.Date;
            case "uuid":
                return ParameterType.Guid;
            default:
                if (name.StartsWith("timestamp", StringComparison.Ordinal))
                    return ParameterType.DateTime;
                multiline = name is "text" or "json" or "jsonb" or "xml";
                return ParameterType.String;
        }
    }

    /// <summary>Как <see cref="SqlDefault"/>, плюс true/false, <c>current_date</c>, <c>now()</c> и сдвиги на interval.</summary>
    private static (IReadOnlyList<string> Values, string? Note) PgDefault(string? expression, ParameterType type)
    {
        if (expression is null)
            return ([], null);
        var raw = PgCastRegex().Replace(expression.Trim(), "");
        var compact = WhitespaceRegex().Replace(raw, "").ToLowerInvariant();
        if (type == ParameterType.Bool && compact is "true" or "false")
            return ([compact], null);
        if (type is ParameterType.Date or ParameterType.DateTime && PgDateExpression(compact) is { } date)
            return ([date], null);
        return SqlDefault(raw, type);
    }

    /// <summary><c>current_date</c> → today, <c>now()</c> → now, <c>current_date - 1</c> → today-1d, <c>now() - interval '2 hours'</c> → now-2h.</summary>
    private static string? PgDateExpression(string compact)
    {
        var m = PgDateRegex().Match(compact);
        if (!m.Success)
            return null;
        var baseExpression = m.Groups["base"].Value == "current_date" ? "today" : "now";
        if (!m.Groups["sign"].Success)
            return baseExpression;
        if (m.Groups["days"].Success)
            return baseExpression == "today" ? $"today{m.Groups["sign"].Value}{m.Groups["days"].Value}d" : null;
        var unit = m.Groups["unit"].Value[0] switch { 'd' => "d", 'h' => "h", _ => "m" };
        return $"{baseExpression}{m.Groups["sign"].Value}{m.Groups["n"].Value}{unit}";
    }

    // ---- Python ----

    private static List<DetectedParameter> Python(string text)
    {
        var result = new List<DetectedParameter>();
        foreach (Match m in PythonAddArgumentRegex().Matches(text))
        {
            var open = m.Index + m.Length - 1;
            var close = PythonLiterals.MatchingParen(text, open);
            if (close < 0)
                continue;
            var args = PythonLiterals.SplitArguments(text[(open + 1)..close]);
            var source = WhitespaceRegex().Replace(text[m.Index..(close + 1)], " ");

            var options = args.Where(a => a.Key is null).Select(a => PythonLiterals.String(a.Value)).OfType<string>().ToList();
            var kw = args.Where(a => a.Key is not null).ToDictionary(a => a.Key!, a => a.Value);
            var option = options.FirstOrDefault(o => o.StartsWith("--")) ?? options.FirstOrDefault(o => o.StartsWith('-')) ?? options.FirstOrDefault();
            if (option is null || option is "-h" or "--help")
                continue;

            var notes = new List<string>();
            if (!option.StartsWith('-'))
                notes.Add("позиционный аргумент: приложение передаёт значения как --имя, добавьте в скрипт «--»");
            var name = option.TrimStart('-');
            if (name.Contains('-'))
            {
                notes.Add($"приложение передаст --{name.Replace('-', '_')}, а не {option}: замените в скрипте «-» на «_»");
                name = name.Replace('-', '_');
            }
            if (!IdentifierRegex().IsMatch(name))
                continue;

            var action = kw.TryGetValue("action", out var a) ? PythonLiterals.String(a) : null;
            var choices = kw.TryGetValue("choices", out var c) ? PythonLiterals.List(c) : null;
            var defaultValue = kw.TryGetValue("default", out var d) ? PythonLiterals.Scalar(d) : null;
            var multi = kw.TryGetValue("nargs", out var n) && PythonLiterals.String(n) is "+" or "*";
            var required = kw.TryGetValue("required", out var r) && r.Trim() == "True";

            var type = action is "store_true" or "store_false" ? ParameterType.Bool
                : choices is { Count: > 0 } ? ParameterType.List
                : kw.TryGetValue("type", out var t) ? PythonType(t.Trim())
                : defaultValue is null ? ParameterType.String
                : InferType(defaultValue);

            var p = new ScriptParameter(name, type)
            {
                Help = kw.TryGetValue("help", out var h) ? PythonLiterals.String(h) : null,
                Required = required || (defaultValue is not null && type != ParameterType.Bool),
                Multi = type == ParameterType.List && multi,
                Items = choices?.Select(i => new ListItem(i, i)).ToList() ?? [],
                Default = type == ParameterType.Bool
                    ? [action == "store_false" ? "true" : "false"]
                    : defaultValue is null ? [] : [defaultValue],
            };
            if (type == ParameterType.Bool && action == "store_false")
                notes.Add("store_false: приложение передаёт флаг, только когда значение — да");

            result.Add(new DetectedParameter(p, source, notes.Count > 0 ? string.Join("; ", notes) : null));
        }

        return result;
    }

    private static ParameterType PythonType(string type) => type switch
    {
        "int" => ParameterType.Int,
        "float" or "Decimal" or "decimal.Decimal" => ParameterType.Decimal,
        "datetime.date.fromisoformat" or "date.fromisoformat" => ParameterType.Date,
        "datetime.datetime.fromisoformat" or "datetime.fromisoformat" => ParameterType.DateTime,
        "uuid.UUID" or "UUID" => ParameterType.Guid,
        _ => ParameterType.String,
    };

    /// <summary>Тип по значению по умолчанию: целое, дробное, да/нет; иначе строка.</summary>
    private static ParameterType InferType(string value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? ParameterType.Int
        : ParameterValues.TryParseDecimal(value, out _) ? ParameterType.Decimal
        : value is "true" or "false" ? ParameterType.Bool
        : ParameterType.String;

    // ---- PowerShell ----

    private static async Task<ScriptAnalysis> AnalyzePowerShellAsync(string text, string? powerShell, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("dsm-analyze-").FullName;
        try
        {
            var bom = new UTF8Encoding(true);
            var script = Path.Combine(dir, "script.ps1");
            var reader = Path.Combine(dir, "read-params.ps1");
            await File.WriteAllTextAsync(script, text, bom, ct);
            await File.WriteAllTextAsync(reader, PowerShellReader, bom, ct);

            var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(powerShell) ? "powershell.exe" : powerShell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", reader, "-Path", script })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var json = (await stdout).Trim();
            if (process.ExitCode != 0 || json.Length == 0)
                return new([], EnvConnections(text), $"Не удалось разобрать param(): {(await stderr).Trim()}");
            return new(ParsePowerShellParams(json), EnvConnections(text), ParamBlock: ParsePowerShellBlock(json));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or JsonException)
        {
            return new([], EnvConnections(text), $"Не удалось разобрать param(): {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    public static PowerShellParamBlock? ParsePowerShellBlock(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ScriptParamsWriter.ParseBlock(doc.RootElement);
    }

    public static List<DetectedParameter> ParsePowerShellParams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<DetectedParameter>();
        foreach (var e in Array(doc.RootElement.GetProperty("params")))
        {
            var name = e.GetProperty("name").GetString()!;
            if (!IdentifierRegex().IsMatch(name))
                continue;
            var typeName = Str(e, "type");
            var defaultText = Str(e, "default");
            var defaultValue = Str(e, "value");
            var set = Array(e.GetProperty("set")).Select(s => s.ToString()).ToList();

            if (defaultText is "$true" or "$false")
                defaultValue = defaultText == "$true" ? "true" : "false";

            var isArray = typeName?.EndsWith("[]") == true;
            var type = set.Count > 0 ? ParameterType.List
                : typeName is null ? (defaultValue is null ? ParameterType.String : InferType(defaultValue))
                : PowerShellType(isArray ? typeName[..^2] : typeName);
            if (type == ParameterType.Bool && defaultValue is null)
                defaultValue = "false";
            // Массив целых — номера и диапазоны; «(1..5) + (7..9)», «1, 3, 5..7» переводим в «1..5, 7..9».
            if (isArray && type == ParameterType.Int && set.Count == 0)
            {
                type = ParameterType.Range;
                defaultValue = defaultText is null ? null : RangeDefault(defaultText);
            }

            string? note = null;
            if (defaultText is not null && defaultValue is null)
                note = $"Значение в скрипте вычисляется ({defaultText}) — задайте значение по умолчанию вручную";
            else if (isArray && set.Count == 0 && type != ParameterType.Range)
                note = "Массив: в форме вводится одно значение";

            var p = new ScriptParameter(name, type)
            {
                Help = Str(e, "help"),
                Required = e.GetProperty("mandatory").GetBoolean() || defaultValue is not null,
                Multi = isArray && set.Count > 0,
                Items = set.Select(s => new ListItem(s, s)).ToList(),
                Default = defaultValue is null ? [] : [defaultValue],
            };
            var source = (typeName is null ? "" : $"[{typeName}]") + "$" + name + (defaultText is null ? "" : " = " + defaultText);
            result.Add(new DetectedParameter(p, source, note));
        }

        return result;

        static string? Str(JsonElement e, string property) =>
            e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ConvertTo-Json в Windows PowerShell сворачивает массив из одного элемента в объект.
        static IEnumerable<JsonElement> Array(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Array => e.EnumerateArray(),
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            _ => [e],
        };
    }

    internal static string? RangeDefault(string expression)
    {
        var text = string.Join(", ", expression.Replace("@(", "(").Split(['+', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return ParameterValues.TryParseRange(text, out _) is null ? text : null;
    }

    private static ParameterType PowerShellType(string type) =>
        type.ToLowerInvariant().Replace("system.", "") switch
        {
            "int" or "int32" or "int64" or "long" or "int16" or "short" or "byte" => ParameterType.Int,
            "decimal" or "double" or "float" or "single" => ParameterType.Decimal,
            "switch" or "switchparameter" or "management.automation.switchparameter" or "bool" or "boolean" => ParameterType.Bool,
            "datetime" => ParameterType.DateTime,
            "guid" => ParameterType.Guid,
            "io.fileinfo" => ParameterType.File,
            "io.directoryinfo" => ParameterType.Folder,
            _ => ParameterType.String,
        };

    /// <summary>Разбирает param() через AST PowerShell и печатает JSON.</summary>
    private const string PowerShellReader = """
        param([string]$Path)
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
        $helpContent = $ast.GetHelpContent()
        $result = New-Object System.Collections.Generic.List[object]
        if ($ast.ParamBlock) {
            foreach ($p in $ast.ParamBlock.Parameters) {
                $name = $p.Name.VariablePath.UserPath
                $mandatory = $false; $help = $null; $set = @(); $type = $null
                foreach ($a in $p.Attributes) {
                    if ($a -is [System.Management.Automation.Language.TypeConstraintAst]) {
                        if (-not $type) { $type = $a.TypeName.FullName }
                    } elseif ($a.TypeName.Name -eq 'Parameter') {
                        foreach ($na in $a.NamedArguments) {
                            if ($na.ArgumentName -eq 'Mandatory') { $mandatory = $na.ExpressionOmitted -or $na.Argument.Extent.Text -eq '$true' }
                            if ($na.ArgumentName -eq 'HelpMessage') { $help = [string]$na.Argument.SafeGetValue() }
                        }
                    } elseif ($a.TypeName.Name -eq 'ValidateSet') {
                        $set = @($a.PositionalArguments | ForEach-Object { [string]$_.SafeGetValue() })
                    }
                }
                if (-not $help -and $helpContent -and $helpContent.Parameters.ContainsKey($name.ToUpperInvariant())) {
                    $help = $helpContent.Parameters[$name.ToUpperInvariant()].Trim()
                }
                $default = $null; $value = $null
                if ($p.DefaultValue) {
                    $default = $p.DefaultValue.Extent.Text
                    if ($p.DefaultValue -is [System.Management.Automation.Language.ConstantExpressionAst] -or
                        ($p.DefaultValue -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -and $p.DefaultValue.NestedExpressions.Count -eq 0)) {
                        $value = [string]$p.DefaultValue.Value
                    }
                }
                $result.Add([pscustomobject]@{ name = $name; type = $type; default = $default; value = $value; mandatory = [bool]$mandatory; help = $help; set = $set })
            }
        }
        # Куда дописывать параметры: за последним в param(), перед «)» пустого param() или, без блока, перед первым кодом.
        $block = @{ exists = $false; afterLast = -1; insertAt = 0 }
        if ($ast.ParamBlock) {
            $block.exists = $true
            $ps = $ast.ParamBlock.Parameters
            if ($ps.Count -gt 0) { $block.afterLast = $ps[$ps.Count - 1].Extent.EndOffset }
            $block.insertAt = $ast.ParamBlock.Extent.EndOffset - 1
        } else {
            $first = $tokens | Where-Object { $_.Kind -notin 'Comment', 'NewLine', 'LineContinuation', 'EndOfInput' } | Select-Object -First 1
            $block.insertAt = if ($first) { $first.Extent.StartOffset } else { $ast.Extent.EndOffset }
            if ($ast.UsingStatements.Count -gt 0) { $block.insertAt = $ast.UsingStatements[$ast.UsingStatements.Count - 1].Extent.EndOffset }
        }
        ConvertTo-Json -InputObject @{ params = [object[]]$result.ToArray(); block = $block } -Depth 4 -Compress
        """;

    // ---- общее ----

    /// <summary>Подключения по переменным окружения <c>DB_&lt;ИМЯ&gt;_CONNSTR</c> и т.п.</summary>
    private static List<string> EnvConnections(string text) =>
        EnvConnectionRegex().Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Where(n => IdentifierRegex().IsMatch(n))
            .Distinct().ToList();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    public static partial Regex IdentifierRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[Nn]?'((?:[^']|'')*)'$")]
    private static partial Regex SqlStringRegex();

    [GeneratedRegex(@"^[-+]?(\d+(\.\d*)?|\.\d+)$")]
    private static partial Regex SqlNumberRegex();

    [GeneratedRegex(@"^(cast\((getdate\(\)|sysdatetime\(\)|current_timestamp)asdate\)|convert\(date,(getdate\(\)|sysdatetime\(\)|current_timestamp)\))$")]
    private static partial Regex SqlTodayRegex();

    [GeneratedRegex(@"^dateadd\((?<unit>\w+),(?<amount>[-+]?\d+),(?<base>.+)\)$")]
    private static partial Regex SqlDateAddRegex();

    [GeneratedRegex(@"(?<str>'(?:[^']|'')*')|(?<cmt>--[^\n]*|/\*.*?\*/)|(?<dollar>\$(?<tag>[A-Za-z_]*)\$.*?\$\k<tag>\$)|(?<qid>""[^""]*"")|(?<code>[^'\-/$""]+|[\-/$])", RegexOptions.Singleline)]
    private static partial Regex PgSegmentRegex();

    [GeneratedRegex(@"(?<![@\w])@([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex PgParameterRegex();

    [GeneratedRegex(@"current_setting\s*\(\s*'param\.([A-Za-z_][A-Za-z0-9_]*)'", RegexOptions.IgnoreCase)]
    private static partial Regex PgSettingRegex();

    [GeneratedRegex(@"\$\$|\$[A-Za-z_]+\$|::|\bcurrent_setting\s*\(|\bplpgsql\b|\bilike\b|\bLANGUAGE\s+sql\b|\bRAISE\s+NOTICE\b|\bjsonb?\b|\bserial\b|\bnow\(\)", RegexOptions.IgnoreCase)]
    private static partial Regex PostgresHintRegex();

    // Приведение типа в конце значения по умолчанию: '2026-01-01'::date.
    [GeneratedRegex(@"\s*::\s*[A-Za-z][A-Za-z0-9_ ]*(\(\d+(,\s*\d+)?\))?\s*$")]
    private static partial Regex PgCastRegex();

    [GeneratedRegex(@"^(?<base>current_date|now\(\)|current_timestamp|localtimestamp(?:\(\d\))?)(?:(?<sign>[-+])(?:(?<days>\d+)|interval'(?<n>\d+)(?<unit>days?|hours?|minutes?|mins?)'))?$")]
    private static partial Regex PgDateRegex();

    [GeneratedRegex(@"\.add_argument\s*\(")]
    private static partial Regex PythonAddArgumentRegex();

    [GeneratedRegex(@"\bDB_([A-Za-z0-9_]+?)_(?:CONNSTR|SERVER|PORT|DATABASE|USER|PASSWORD|INTEGRATED|ODBC)\b")]
    private static partial Regex EnvConnectionRegex();
}
