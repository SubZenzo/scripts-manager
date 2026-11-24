using ScriptsManager.Core.Manifests;

namespace ScriptsManager.App.ViewModels;

/// <summary>Подписи значений манифеста для UI.</summary>
public static class Display
{
    public static string Name(this RunnerKind runner) => runner switch
    {
        RunnerKind.TSql => "T-SQL",
        RunnerKind.PlPgSql => "PL/pgSQL",
        RunnerKind.PowerShell => "PowerShell",
        RunnerKind.Python => "Python",
        _ => runner.ToString(),
    };

    public static string Badge(this RunnerKind runner) => runner switch
    {
        RunnerKind.TSql => "SQL",
        RunnerKind.PlPgSql => "PG",
        RunnerKind.PowerShell => "PS",
        RunnerKind.Python => "PY",
        _ => "",
    };

    /// <summary>Имя встроенной подсветки AvalonEdit.</summary>
    public static string Highlighting(this RunnerKind runner) => runner switch
    {
        RunnerKind.PowerShell => "PowerShell",
        RunnerKind.Python => "Python",
        _ => "TSQL",
    };

    public static string Name(this DbKind kind) => kind == DbKind.MsSql ? "MS SQL" : "PostgreSQL";

    public static string Name(this ConnectionTarget target) =>
        target.Server + (target.Port is { } port ? $":{port}" : "") + (target.Database is { } db ? $" / {db}" : "");
}
