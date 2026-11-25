using System.Data;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScriptsManager.Core.Authoring;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;
using Microsoft.Data.SqlClient;

namespace ScriptsManager.Core.Runs;

/// <summary>
/// Выполняет T-SQL на первом подключении манифеста: пакеты по GO, параметры <c>@имя</c>,
/// PRINT и предупреждения — в лог, результирующие наборы — таблицами.
/// </summary>
public sealed class TSqlRunner(ConnectionFactory factory) : IScriptRunner
{
    // Кириллица без \u-экранирования: OPENJSON разберёт любой вариант, а PRINT так читаемее.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task RunAsync(RunRequest request, IRunOutput output, CancellationToken ct)
    {
        var manifest = request.Manifest;
        var slot = manifest.Connections.FirstOrDefault()
                   ?? throw new ScriptFailedException("В манифесте нет подключения: T-SQL выполняется на первом из connections");
        if (slot.Type != DbKind.MsSql)
            throw new ScriptFailedException($"Подключение «{slot.Name}» должно быть MS SQL");

        var run = request.Connections[slot.Name];
        // Блок --#region Params нужен только для запуска вне приложения: параметры передаются отдельно.
        var batches = SqlBatches.Split(TSqlParamsRegion.Blank(await File.ReadAllTextAsync(manifest.ScriptPath, ct)));
        var parameters = BuildParameters(request);
        var showGrid = manifest.Output.Contains(OutputKind.Grid);

        output.Log(LogKind.Info, $"Подключение: {run.Choice.Display}");
        await using var connection = (SqlConnection)factory.CreateConnection(run.Choice.Connection, run.Choice.Database, run.Password);
        connection.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                output.Log(error.Class > 10 ? LogKind.Error : LogKind.Message, error.Message);
        };
        await connection.OpenAsync(ct);

        for (var b = 0; b < batches.Count; b++)
        {
            var batch = batches[b];
            if (batches.Count > 1)
                output.Log(LogKind.Info, $"Пакет {b + 1} из {batches.Count} (строка {batch.StartLine})" +
                                         (batch.Repeat > 1 ? $", повторов: {batch.Repeat}" : ""));

            for (var r = 0; r < batch.Repeat; r++)
                RunBatch(connection, batch, parameters, showGrid, output, ct);
        }
    }

    /// <summary>
    /// Пакет выполняется синхронно (runner и так работает в фоновом потоке): в асинхронном режиме
    /// SqlClient не может отменить запрос, если сервер уже прислал сообщение (PRINT, RAISERROR WITH NOWAIT),
    /// и Cancel ждёт окончания запроса.
    /// </summary>
    private static void RunBatch(SqlConnection connection, SqlBatch batch, IReadOnlyList<SqlParameter> parameters,
        bool showGrid, IRunOutput output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = batch.Text;
        cmd.CommandTimeout = 0;
        foreach (var p in parameters)
            cmd.Parameters.Add(((ICloneable)p).Clone());

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
        catch (SqlException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Остановлено", ex, ct);
        }
        catch (SqlException ex)
        {
            foreach (SqlError error in ex.Errors)
            {
                // Номер строки сервер считает от начала пакета.
                var line = error.LineNumber > 0 ? $", строка {batch.StartLine + error.LineNumber - 1}" : "";
                output.Log(LogKind.Error, $"Сообщение {error.Number}, уровень {error.Class}, состояние {error.State}{line}: {error.Message}");
            }

            throw new ScriptFailedException("Скрипт завершился с ошибкой");
        }
    }

    /// <summary>
    /// Параметры формы как типизированные <c>@имя</c>; для остальных подключений —
    /// <c>@слот_server</c> и <c>@слот_database</c>.
    /// </summary>
    public static IReadOnlyList<SqlParameter> BuildParameters(RunRequest request)
    {
        var result = new List<SqlParameter>();
        foreach (var p in request.Manifest.Parameters)
            result.Add(CreateParameter(p, request.Values.GetValueOrDefault(p.Name)));

        foreach (var slot in request.Manifest.Connections.Skip(1))
        {
            if (!request.Connections.TryGetValue(slot.Name, out var c))
                continue;
            result.Add(Text($"{slot.Name}_server", c.Choice.Connection.ServerDisplay));
            result.Add(Text($"{slot.Name}_database", c.Choice.Database));
        }

        return result;
    }

    public static SqlParameter CreateParameter(ScriptParameter parameter, object? value)
    {
        var p = new SqlParameter("@" + parameter.Name, SqlTypeOf(parameter));
        switch (p.SqlDbType)
        {
            case SqlDbType.NVarChar:
                p.Size = -1;
                break;
            case SqlDbType.Decimal:
                p.Precision = 38;
                p.Scale = 10;
                break;
            case SqlDbType.DateTime2:
                p.Scale = 7;
                break;
        }

        p.Value = value switch
        {
            null => DBNull.Value,
            string[] list => JsonSerializer.Serialize(list, JsonOptions),
            long[] numbers => JsonSerializer.Serialize(numbers, JsonOptions),
            _ => value,
        };
        return p;
    }

    private static SqlDbType SqlTypeOf(ScriptParameter p) => p.Type switch
    {
        _ when p.Multi || p.Type == ParameterType.Range => SqlDbType.NVarChar,
        ParameterType.Int => SqlDbType.BigInt,
        ParameterType.Decimal => SqlDbType.Decimal,
        ParameterType.Bool => SqlDbType.Bit,
        ParameterType.Date => SqlDbType.Date,
        ParameterType.DateTime => SqlDbType.DateTime2,
        ParameterType.Guid => SqlDbType.UniqueIdentifier,
        _ => SqlDbType.NVarChar,
    };

    private static SqlParameter Text(string name, string? value) =>
        new("@" + name, SqlDbType.NVarChar, -1) { Value = (object?)value ?? DBNull.Value };
}
