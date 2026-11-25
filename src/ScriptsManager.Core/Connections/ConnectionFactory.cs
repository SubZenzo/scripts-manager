using System.Data.Common;
using ScriptsManager.Core.Manifests;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace ScriptsManager.Core.Connections;

/// <summary>Строки подключения и служебные запросы к серверу.</summary>
public sealed class ConnectionFactory
{
    public const string ApplicationName = "Scripts Manager";

    public string BuildConnectionString(SavedConnection connection, string? database, string? password)
    {
        if (connection.Type == DbKind.MsSql)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = connection.Server.Trim(),
                ApplicationName = ApplicationName,
                // Сертификаты тестовых серверов не доверенные; шифрование оставляем по возможности.
                Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = true,
                ConnectTimeout = 15,
                // У имени бывает несколько A-записей, часть недоступна (myserver): без этого
                // SqlClient перебирает адреса по очереди и ждёт тайм-аут на каждом.
                MultiSubnetFailover = true,
            };
            if (!string.IsNullOrEmpty(database))
                b.InitialCatalog = database;
            if (connection.Auth == AuthKind.Windows)
            {
                b.IntegratedSecurity = true;
            }
            else
            {
                b.UserID = connection.User ?? "";
                b.Password = password ?? "";
            }

            return b.ConnectionString;
        }

        var pg = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Server.Trim(),
            Port = connection.Port ?? 5432,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            Username = connection.User,
            ApplicationName = ApplicationName,
            Timeout = 15,
        };
        // Для PgPass пароль не задаём: Npgsql сам читает pgpass.conf.
        if (connection.Auth == AuthKind.Password)
            pg.Password = password ?? "";
        return pg.ConnectionString;
    }

    /// <summary>
    /// Строка для скриптов PowerShell/Python: для MS SQL — только ключи, которые понимают и
    /// System.Data.SqlClient (Windows PowerShell), и Microsoft.Data.SqlClient; для PostgreSQL — строка Npgsql.
    /// </summary>
    public static string PortableConnectionString(SavedConnection connection, string? database, string? password)
    {
        if (connection.Type == DbKind.Postgres)
            return new ConnectionFactory().BuildConnectionString(connection, database, password);

        var b = new DbConnectionStringBuilder
        {
            ["Data Source"] = connection.Server.Trim(),
            ["Application Name"] = ApplicationName,
            ["TrustServerCertificate"] = "True",
            ["MultiSubnetFailover"] = "True",
        };
        if (!string.IsNullOrEmpty(database))
            b["Initial Catalog"] = database;
        if (connection.Auth == AuthKind.Windows)
        {
            b["Integrated Security"] = "True";
        }
        else
        {
            b["User ID"] = connection.User ?? "";
            b["Password"] = password ?? "";
        }

        return b.ConnectionString;
    }

    /// <summary>Строка ODBC для pyodbc (драйвер «ODBC Driver 17 for SQL Server»).</summary>
    public static string OdbcConnectionString(SavedConnection connection, string? database, string? password)
    {
        var b = new DbConnectionStringBuilder(useOdbcRules: true)
        {
            ["Driver"] = "{ODBC Driver 17 for SQL Server}",
            ["Server"] = connection.Server.Trim(),
            ["TrustServerCertificate"] = "yes",
            ["MultiSubnetFailover"] = "yes",
        };
        if (!string.IsNullOrEmpty(database))
            b["Database"] = database;
        if (connection.Auth == AuthKind.Windows)
        {
            b["Trusted_Connection"] = "yes";
        }
        else
        {
            b["UID"] = connection.User ?? "";
            b["PWD"] = password ?? "";
        }

        return b.ConnectionString;
    }

    public DbConnection CreateConnection(SavedConnection connection, string? database, string? password)
    {
        var cs = BuildConnectionString(connection, database, password);
        return connection.Type == DbKind.MsSql ? new SqlConnection(cs) : new NpgsqlConnection(cs);
    }

    /// <summary>Базы, к которым есть доступ; заодно проверяет подключение.</summary>
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(SavedConnection connection, string? password, CancellationToken ct)
    {
        await using var db = CreateConnection(connection, null, password);
        await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = connection.Type == DbKind.MsSql
            ? "select name from sys.databases where state = 0 and has_dbaccess(name) = 1 order by name"
            : "select datname from pg_database where datallowconn and not datistemplate order by datname";

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }
}
