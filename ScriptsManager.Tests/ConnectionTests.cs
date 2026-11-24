using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace ScriptsManager.Tests;

public sealed class ConnectionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static SavedConnection MsSql(string server, AuthKind auth = AuthKind.Windows, string? user = null) =>
        new(DbKind.MsSql, server, null, auth, user);

    [Fact]
    public void MsSqlConnectionString()
    {
        var factory = new ConnectionFactory();

        var windows = new SqlConnectionStringBuilder(factory.BuildConnectionString(MsSql(" myserver4 "), "mydatabase", null));
        Assert.Equal("myserver4", windows.DataSource);
        Assert.Equal("mydatabase", windows.InitialCatalog);
        Assert.True(windows.IntegratedSecurity);
        Assert.True(windows.TrustServerCertificate);
        Assert.True(windows.MultiSubnetFailover);

        var login = new SqlConnectionStringBuilder(factory.BuildConnectionString(MsSql("myserver3", AuthKind.Password, "reader"), null, "secret"));
        Assert.Equal("myserver3", login.DataSource);
        Assert.Equal("", login.InitialCatalog);
        Assert.False(login.IntegratedSecurity);
        Assert.Equal(("reader", "secret"), (login.UserID, login.Password));
    }

    [Fact]
    public void PostgresConnectionString()
    {
        var factory = new ConnectionFactory();

        var pass = new NpgsqlConnectionStringBuilder(factory.BuildConnectionString(
            new SavedConnection(DbKind.Postgres, "myserver2", 5433, AuthKind.Password, "u"), "mydatabase2", "p"));
        Assert.Equal(("myserver2", 5433, "mydatabase2", "u", "p"), (pass.Host, pass.Port, pass.Database, pass.Username, pass.Password));

        var pgpass = new NpgsqlConnectionStringBuilder(factory.BuildConnectionString(
            new SavedConnection(DbKind.Postgres, "myserver2", null, AuthKind.PgPass, "u"), null, "ignored"));
        Assert.Equal((5432, "postgres"), (pgpass.Port, pgpass.Database));
        Assert.Null(pgpass.Password);
    }

    [Fact]
    public void RecentAndLastChoices()
    {
        var store = new ConnectionStore(Path.Combine(_dir.Path, "connections.json"));
        var myServer = MsSql("myserver");
        var myServer3 = MsSql("myserver3", AuthKind.Password, "reader");
        var pg = new SavedConnection(DbKind.Postgres, "myserver2", 5432, AuthKind.PgPass, "u");

        store.Touch(myServer);
        store.Touch(myServer3);
        store.Touch(pg);
        store.Touch(MsSql("MYSERVER"));

        Assert.Equal(["MYSERVER", "myserver3"], store.GetRecent(DbKind.MsSql).Select(c => c.Server));
        Assert.Equal(pg, Assert.Single(store.GetRecent(DbKind.Postgres)));

        store.SetLastChoice(Path.Combine(_dir.Path, "s.yaml"), "connection", new ConnectionChoice(myServer3, "mydatabase"));
        var last = store.GetLastChoice(Path.Combine(_dir.Path, "S.yaml"), "connection");
        Assert.Equal(new ConnectionChoice(myServer3, "mydatabase"), last);
        Assert.Null(store.GetLastChoice(Path.Combine(_dir.Path, "s.yaml"), "other"));

        store.Forget(myServer3);
        Assert.Equal(["MYSERVER"], store.GetRecent(DbKind.MsSql).Select(c => c.Server));
        Assert.Contains("\"MsSql\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void RecentIsLimited()
    {
        var store = new ConnectionStore(Path.Combine(_dir.Path, "connections.json"));
        for (var i = 0; i < ConnectionStore.MaxRecent + 5; i++)
            store.Touch(MsSql($"s{i}"));

        var recent = store.GetRecent(DbKind.MsSql);
        Assert.Equal(ConnectionStore.MaxRecent, recent.Count);
        Assert.Equal($"s{ConnectionStore.MaxRecent + 4}", recent[0].Server);
    }
}
