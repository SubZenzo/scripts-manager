using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.App.ViewModels;

public sealed record AuthOption(AuthKind Kind, string Name);

/// <summary>Окно подключения как в SSMS: сервер, способ входа, БД.</summary>
public sealed partial class ConnectionDialogViewModel : ObservableObject
{
    private readonly ConnectionService _service;
    private readonly IReadOnlyList<SavedConnection> _recent;
    private CancellationTokenSource? _loading;

    public ConnectionDialogViewModel(ConnectionService service, DbKind type, bool needsDatabase, ConnectionChoice? current)
    {
        _service = service;
        Type = type;
        NeedsDatabase = needsDatabase;
        _recent = service.GetRecent(type);

        AuthOptions = type == DbKind.MsSql
            ? [new(AuthKind.Windows, "Windows"), new(AuthKind.Password, "SQL Server (логин и пароль)")]
            : [new(AuthKind.Password, "Логин и пароль"), new(AuthKind.PgPass, "Логин, пароль из pgpass.conf")];

        // Подсказки сервера — только недавние подключения.
        Servers = _recent.Select(c => c.Server).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var c = current?.Connection;
        _server = c?.Server ?? "";
        _port = c?.Port?.ToString(CultureInfo.InvariantCulture) ?? (type == DbKind.Postgres ? "5432" : "");
        _selectedAuth = AuthOptions.FirstOrDefault(a => a.Kind == c?.Auth) ?? AuthOptions[0];
        _user = c?.User ?? "";
        _database = current?.Database ?? "";
        Password = c is null ? "" : service.GetPassword(c) ?? "";
        HasStoredPassword = c is not null && service.GetPassword(c) is not null;
    }

    public DbKind Type { get; }
    public string Title => Type == DbKind.MsSql ? "Подключение к MS SQL" : "Подключение к PostgreSQL";
    public bool IsPostgres => Type == DbKind.Postgres;
    public bool NeedsDatabase { get; }
    public IReadOnlyList<AuthOption> AuthOptions { get; }
    public IReadOnlyList<string> Servers { get; }
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>Пароль из PasswordBox (у него нет привязки).</summary>
    public string Password { get; set; }

    /// <summary>Пароль уже был сохранён — показываем его точками при открытии окна.</summary>
    public bool HasStoredPassword { get; }

    [ObservableProperty]
    private string _server;

    [ObservableProperty]
    private string _port;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsUser), nameof(NeedsPassword))]
    private AuthOption _selectedAuth;

    [ObservableProperty]
    private string _user;

    [ObservableProperty]
    private bool _rememberPassword = true;

    [ObservableProperty]
    private string _database;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isBusy;

    public bool NeedsUser => SelectedAuth.Kind != AuthKind.Windows;
    public bool NeedsPassword => SelectedAuth.Kind == AuthKind.Password;

    partial void OnServerChanged(string value)
    {
        Databases.Clear();
        // Выбрали недавний сервер — подставляем способ входа, если логин ещё не заполнен.
        if (User.Length == 0 && _recent.FirstOrDefault(c => c.Server.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) is { } recent)
        {
            SelectedAuth = AuthOptions.FirstOrDefault(a => a.Kind == recent.Auth) ?? SelectedAuth;
            User = recent.User ?? "";
            if (recent.Port is { } port)
                Port = port.ToString(CultureInfo.InvariantCulture);
        }
    }

    public SavedConnection BuildConnection() => new(
        Type,
        Server.Trim(),
        IsPostgres && int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null,
        SelectedAuth.Kind,
        NeedsUser ? User.Trim() : null);

    /// <summary>Ошибка заполнения или null.</summary>
    public string? Validate()
    {
        if (Server.Trim().Length == 0)
            return "Укажите сервер";
        if (IsPostgres && !(int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536))
            return "Неверный порт";
        if (NeedsUser && User.Trim().Length == 0)
            return "Укажите логин";
        if (NeedsDatabase && Database.Trim().Length == 0)
            return "Укажите базу данных";
        return null;
    }

    /// <summary>Загружает список БД — заодно проверка подключения.</summary>
    [RelayCommand]
    private async Task LoadDatabasesAsync()
    {
        var error = Validate();
        if (error is not null && error != "Укажите базу данных")
        {
            SetStatus(error, true);
            return;
        }

        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var ct = _loading.Token;
        var connection = BuildConnection();
        IsBusy = true;
        SetStatus("Подключение…", false);
        try
        {
            var factory = _service.Factory;
            var password = NeedsPassword ? Password : null;
            var names = await Task.Run(() => factory.ListDatabasesAsync(connection, password, ct), ct);
            if (ct.IsCancellationRequested)
                return;
            Databases.Clear();
            foreach (var name in names)
                Databases.Add(name);
            SetStatus($"Подключение успешно, баз данных: {names.Count}", false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Окно закрыли или запустили проверку заново.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(ex.GetBaseException().Message, true);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsBusy = false;
        }
    }

    public void Cancel() => _loading?.Cancel();

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        IsError = isError;
    }

    /// <summary>Сохраняет выбор: пароль и список недавних.</summary>
    public ConnectionChoice Apply()
    {
        var connection = BuildConnection();
        if (NeedsPassword)
            _service.SetPassword(connection, Password, RememberPassword);
        _service.Store.Touch(connection);
        return new ConnectionChoice(connection, NeedsDatabase ? Database.Trim() : null);
    }
}
