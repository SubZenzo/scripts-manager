namespace ScriptsManager.Core.Manifests;

public enum RunnerKind { TSql, PlPgSql, PowerShell, Python }

public enum OutputKind { Grid, Log }

public enum ParameterType { String, Text, Int, Decimal, Bool, Date, DateTime, Guid, List, Folder, File, Range }

public enum DbKind { MsSql, Postgres }

public enum ConnectionScope { Database, Server }

/// <summary>Имена значений перечислений в манифесте.</summary>
public static class YamlNames
{
    public static readonly IReadOnlyDictionary<string, RunnerKind> Runners = Names(
        ("tsql", RunnerKind.TSql), ("plpgsql", RunnerKind.PlPgSql),
        ("powershell", RunnerKind.PowerShell), ("python", RunnerKind.Python));

    public static readonly IReadOnlyDictionary<string, OutputKind> Outputs = Names(
        ("grid", OutputKind.Grid), ("log", OutputKind.Log));

    public static readonly IReadOnlyDictionary<string, ParameterType> ParameterTypes = Names(
        ("string", ParameterType.String), ("text", ParameterType.Text), ("int", ParameterType.Int),
        ("decimal", ParameterType.Decimal), ("bool", ParameterType.Bool), ("date", ParameterType.Date),
        ("datetime", ParameterType.DateTime), ("guid", ParameterType.Guid), ("list", ParameterType.List),
        ("folder", ParameterType.Folder), ("file", ParameterType.File), ("range", ParameterType.Range));

    public static readonly IReadOnlyDictionary<string, DbKind> DbKinds = Names(
        ("mssql", DbKind.MsSql), ("postgres", DbKind.Postgres));

    public static readonly IReadOnlyDictionary<string, ConnectionScope> Scopes = Names(
        ("database", ConnectionScope.Database), ("server", ConnectionScope.Server));

    public static string ToYaml<T>(this IReadOnlyDictionary<string, T> names, T value) where T : struct, Enum =>
        names.First(p => EqualityComparer<T>.Default.Equals(p.Value, value)).Key;

    public static string FileExtension(this RunnerKind runner) => runner switch
    {
        RunnerKind.TSql or RunnerKind.PlPgSql => ".sql",
        RunnerKind.PowerShell => ".ps1",
        RunnerKind.Python => ".py",
        _ => throw new ArgumentOutOfRangeException(nameof(runner)),
    };

    private static Dictionary<string, T> Names<T>(params (string Name, T Value)[] items) =>
        items.ToDictionary(i => i.Name, i => i.Value, StringComparer.OrdinalIgnoreCase);
}
