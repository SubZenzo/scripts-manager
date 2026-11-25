using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.App.Infrastructure;

/// <summary>Недавние подключения, пароли (Credential Manager или только на сессию) и запросы к серверу.</summary>
public sealed class ConnectionService(ConnectionStore store, ICredentialStore credentials, ConnectionFactory factory)
{
    private readonly Dictionary<string, string> _sessionPasswords = [];

    public ConnectionStore Store { get; } = store;
    public ConnectionFactory Factory { get; } = factory;

    public IReadOnlyList<SavedConnection> GetRecent(DbKind type)
    {
        try
        {
            return Store.GetRecent(type);
        }
        catch (Exception)
        {
            // Повреждённый connections.json не должен ломать форму; при сохранении файл перезапишется.
            return [];
        }
    }

    public string? GetPassword(SavedConnection connection)
    {
        if (connection.Auth != AuthKind.Password)
            return null;
        if (_sessionPasswords.TryGetValue(connection.Key, out var password))
            return password;
        try
        {
            return credentials.GetPassword(connection);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool HasPassword(SavedConnection connection) =>
        connection.Auth != AuthKind.Password || GetPassword(connection) is not null;

    /// <summary>Пароль на сессию; с <paramref name="remember"/> — ещё и в Credential Manager.</summary>
    public void SetPassword(SavedConnection connection, string password, bool remember)
    {
        _sessionPasswords[connection.Key] = password;
        if (remember)
            credentials.SavePassword(connection, password);
        else
            credentials.DeletePassword(connection);
    }

    public Task<IReadOnlyList<string>> ListDatabasesAsync(SavedConnection connection, CancellationToken ct) =>
        Factory.ListDatabasesAsync(connection, GetPassword(connection), ct);

    /// <summary>
    /// Подключение для сервера из манифеста или пресета: способ входа берётся из недавних,
    /// иначе Windows для MS SQL и логин/пароль для PostgreSQL.
    /// </summary>
    public SavedConnection Resolve(DbKind type, ConnectionTarget target) =>
        GetRecent(type).FirstOrDefault(c => c.Server.Equals(target.Server, StringComparison.OrdinalIgnoreCase)
                                            && (target.Port is null || c.Port == target.Port))
        ?? new SavedConnection(type, target.Server, target.Port, type == DbKind.MsSql ? AuthKind.Windows : AuthKind.Password, null);

    public void Remember(string manifestPath, string slot, ConnectionChoice choice)
    {
        Store.Touch(choice.Connection);
        Store.SetLastChoice(manifestPath, slot, choice);
    }
}
