using System.Text.Json.Serialization;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Connections;

public enum AuthKind
{
    /// <summary>MS SQL: Windows-аутентификация.</summary>
    Windows,

    /// <summary>Логин и пароль (пароль — в Credential Manager или только на время сессии).</summary>
    Password,

    /// <summary>PostgreSQL: пароль из pgpass.conf (его читает Npgsql).</summary>
    PgPass,
}

/// <summary>Подключение к серверу без выбора БД: что хранится в списке недавних.</summary>
public sealed record SavedConnection(DbKind Type, string Server, int? Port, AuthKind Auth, string? User)
{
    /// <summary>Ключ для поиска пароля и сравнения: регистр сервера и логина не важен.</summary>
    [JsonIgnore]
    public string Key => string.Join("|", Type, Server.Trim().ToLowerInvariant(), Port, Auth, User?.Trim().ToLowerInvariant());

    [JsonIgnore]
    public string ServerDisplay => Server + (Port is { } port ? $":{port}" : "");

    [JsonIgnore]
    public string AuthDisplay => Auth switch
    {
        AuthKind.Windows => "Windows",
        AuthKind.PgPass => $"{User} (pgpass)",
        _ => string.IsNullOrEmpty(User) ? "логин не указан" : User,
    };

    [JsonIgnore]
    public string Display => $"{ServerDisplay} — {AuthDisplay}";

    public bool SameAs(SavedConnection other) => Key == other.Key;
}

/// <summary>Выбор для слота подключения скрипта: сервер и БД.</summary>
public sealed record ConnectionChoice(SavedConnection Connection, string? Database)
{
    [JsonIgnore]
    public string Display => Connection.ServerDisplay + (Database is { Length: > 0 } db ? $" / {db}" : "") + $" ({Connection.AuthDisplay})";
}
