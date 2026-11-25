using System.Data;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Runs;

public enum LogKind
{
    /// <summary>Служебное сообщение приложения (подключение, пакеты, число строк).</summary>
    Info,

    /// <summary>Вывод скрипта: PRINT, RAISE NOTICE, stdout.</summary>
    Message,

    /// <summary>Ошибка скрипта или запуска.</summary>
    Error,
}

/// <summary>Куда runner пишет вывод; вызывается из фонового потока.</summary>
public interface IRunOutput
{
    void Log(LogKind kind, string text);

    /// <summary>
    /// Результирующий набор. Столбцы называются c0, c1, …, настоящее имя — в <see cref="DataColumn.Caption"/>
    /// (имена из запроса могут содержать символы, ломающие привязку в WPF).
    /// </summary>
    void Result(DataTable table);
}

public sealed record RunConnection(ConnectionChoice Choice, string? Password);

public sealed record RunRequest(
    ScriptManifest Manifest,
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyDictionary<string, RunConnection> Connections)
{
    /// <summary>Папка вывода скрипта (OUTPUT_DIR для PowerShell и Python); создаётся при запуске.</summary>
    public string? OutputDir { get; init; }
}

public interface IScriptRunner
{
    Task RunAsync(RunRequest request, IRunOutput output, CancellationToken ct);
}

/// <summary>Скрипт завершился с ошибкой; подробности уже записаны в лог.</summary>
public sealed class ScriptFailedException(string message) : Exception(message);
