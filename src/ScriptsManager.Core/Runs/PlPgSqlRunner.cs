using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;
using Npgsql;
using NpgsqlTypes;

namespace ScriptsManager.Core.Runs;

/// <summary>
/// Выполняет SQL/PL/pgSQL на первом подключении манифеста. Параметры доступны как <c>@имя</c> в обычных
/// запросах и как <c>current_setting('param.имя', true)</c> — в том числе внутри <c>DO $$ … $$</c>, куда
/// параметры передать нельзя. RAISE NOTICE — в лог, результирующие наборы — таблицами.
/// </summary>
public sealed class PlPgSqlRunner(ConnectionFactory factory) : IScriptRunner
{
    public const string SettingPrefix = "param.";

    public async Task RunAsync(RunRequest request, IRunOutput output, CancellationToken ct)
    {
        var manifest = request.Manifest;
        var slot = manifest.Connections.FirstOrDefault()
                   ?? throw new ScriptFailedException("В манифесте нет подключения: скрипт выполняется на первом из connections");
        if (slot.Type != DbKind.Postgres)
            throw new ScriptFailedException($"Подключение «{slot.Name}» должно быть PostgreSQL");

        var run = request.Connections[slot.Name];
        var script = await File.ReadAllTextAsync(manifest.ScriptPath, ct);
        var showGrid = manifest.Output.Contains(OutputKind.Grid);

        output.Log(LogKind.Info, $"Подключение: {run.Choice.Display}");
        await using var connection = (NpgsqlConnection)factory.CreateConnection(run.Choice.Connection, run.Choice.Database, run.Password);
        connection.Notice += (_, e) => output.Log(e.Notice.Severity is "WARNING" ? LogKind.Error : LogKind.Message, e.Notice.MessageText);
        await connection.OpenAsync(ct);

        SetConfig(connection, request, ct);

        using var cmd = new NpgsqlCommand(script, connection) { CommandTimeout = 0 };
        // Npgsql подставляет только параметры, которые встречаются в тексте (вне строк и $$-блоков).
        foreach (var p in manifest.Parameters)
            cmd.Parameters.Add(CreateParameter(p, request.Values.GetValueOrDefault(p.Name)));

        // Как и для MS SQL — синхронно в фоновом потоке, отмена через Cancel (отдельный запрос серверу).
        using var cancel = ct.Register(() => Task.Run(cmd.Cancel));
        try
        {
            using var reader = cmd.ExecuteReader();
            do
            {
                if (reader.FieldCount == 0)
                    continue;
                var table = ResultTables.Read(reader, ct);
                output.Log(LogKind.Info, $"({table.Rows.Count} строк)");
                if (showGrid)
                    output.Result(table);
            }
            while (reader.NextResult());
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is NpgsqlException or OperationCanceledException)
        {
            throw new OperationCanceledException("Остановлено", ex, ct);
        }
        catch (PostgresException ex)
        {
            output.Log(LogKind.Error, Describe(ex, script));
            throw new ScriptFailedException("Скрипт завершился с ошибкой");
        }
    }

    /// <summary>Значения параметров в настройки сессии: <c>param.имя</c> (null — пустая строка).</summary>
    private static void SetConfig(NpgsqlConnection connection, RunRequest request, CancellationToken ct)
    {
        foreach (var p in request.Manifest.Parameters)
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = new NpgsqlCommand("select set_config($1, $2, false)", connection);
            cmd.Parameters.Add(new NpgsqlParameter { Value = SettingPrefix + p.Name.ToLowerInvariant() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = RunValues.Text(request.Values.GetValueOrDefault(p.Name)) ?? "" });
            cmd.ExecuteNonQuery();
        }
    }

    public static NpgsqlParameter CreateParameter(ScriptParameter parameter, object? value)
    {
        var type = parameter.Multi || parameter.Type == ParameterType.Range ? NpgsqlDbType.Text : parameter.Type switch
        {
            ParameterType.Int => NpgsqlDbType.Bigint,
            ParameterType.Decimal => NpgsqlDbType.Numeric,
            ParameterType.Bool => NpgsqlDbType.Boolean,
            ParameterType.Date => NpgsqlDbType.Date,
            ParameterType.DateTime => NpgsqlDbType.Timestamp,
            ParameterType.Guid => NpgsqlDbType.Uuid,
            _ => NpgsqlDbType.Text,
        };
        return new NpgsqlParameter(parameter.Name, type)
        {
            Value = value switch
            {
                null => DBNull.Value,
                string[] list => RunValues.Json(list),
                long[] numbers => RunValues.Json(numbers),
                _ => value,
            },
        };
    }

    /// <summary>Ошибка как в psql: код, текст, строка в файле, подробности, подсказка, контекст PL/pgSQL.</summary>
    public static string Describe(PostgresException ex, string script)
    {
        var parts = new List<string> { $"{ex.Severity} {ex.SqlState}: {ex.MessageText}" };
        // Npgsql сам делит скрипт на операторы и не всегда сообщает, в каком ошибка (BatchCommand пуст),
        // а Position считается от начала оператора — тогда показываем её как есть.
        if (ex.Position > 0)
            parts.Add(LineOf(script, ex.BatchCommand?.CommandText, ex.Position) is { } line
                ? $"строка {line}"
                : $"позиция {ex.Position} от начала оператора");
        if (ex.Detail is { Length: > 0 } detail)
            parts.Add($"подробности: {detail}");
        if (ex.Hint is { Length: > 0 } hint)
            parts.Add($"подсказка: {hint}");
        if (ex.Where is { Length: > 0 } where)
            parts.Add($"контекст: {where}");
        return string.Join("; ", parts);
    }

    /// <summary>Номер строки в файле по позиции ошибки внутри отдельного оператора.</summary>
    private static int? LineOf(string script, string? statement, int position)
    {
        if (string.IsNullOrEmpty(statement))
            return null;
        var start = script.IndexOf(statement.Trim(), StringComparison.Ordinal);
        if (start < 0)
            return null;
        // position считается от начала оператора вместе с ведущими пробелами, а нашли мы его без них.
        var leading = statement.Length - statement.TrimStart().Length;
        var offset = Math.Clamp(start + position - 1 - leading, 0, script.Length);
        return script.AsSpan(0, offset).Count('\n') + 1;
    }
}
