using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.App.ViewModels;

/// <summary>Выбор подключения для слота из манифеста: сервер из недавних и БД.</summary>
public sealed partial class ConnectionSlotViewModel : ObservableObject
{
    private readonly ConnectionService _service;
    private SavedConnection? _databasesFor;
    private CancellationTokenSource? _loading;
    private bool _touched;

    public ConnectionSlotViewModel(ConnectionSlot slot, ConnectionService service)
    {
        Slot = slot;
        _service = service;
        foreach (var c in service.GetRecent(slot.Type))
            Options.Add(c);
    }

    public ConnectionSlot Slot { get; }
    public string Name => Slot.Name;
    public string TypeName => Slot.Type.Name();
    public bool NeedsDatabase => Slot.Scope == ConnectionScope.Database;
    public ObservableCollection<SavedConnection> Options { get; } = [];
    public ObservableCollection<string> Databases { get; } = [];

    [ObservableProperty]
    private SavedConnection? _selected;

    [ObservableProperty]
    private string _database = "";

    [ObservableProperty]
    private string? _error;

    partial void OnSelectedChanged(SavedConnection? value) => Changed();

    partial void OnDatabaseChanged(string value) => Changed();

    public ConnectionChoice? GetChoice() =>
        Selected is { } c ? new ConnectionChoice(c, NeedsDatabase ? Database.Trim() : null) : null;

    public void SetChoice(ConnectionChoice? choice)
    {
        if (choice is null)
            return;
        var existing = Options.FirstOrDefault(o => o.SameAs(choice.Connection));
        if (existing is null)
        {
            existing = choice.Connection;
            Options.Insert(0, existing);
        }

        Selected = existing;
        Database = choice.Database ?? "";
    }

    public void SetTarget(ConnectionTarget target) =>
        SetChoice(new ConnectionChoice(_service.Resolve(Slot.Type, target), target.Database));

    public bool Validate()
    {
        _touched = true;
        Changed();
        return Error is null;
    }

    private void Changed()
    {
        Error = Selected switch
        {
            null => _touched ? "Выберите подключение" : null,
            _ when NeedsDatabase && Database.Trim().Length == 0 => _touched ? "Укажите базу данных" : null,
            { Auth: AuthKind.Password, User: null or "" } => "Не указан логин — нажмите «…»",
            { Auth: AuthKind.Password } c when !_service.HasPassword(c) => "Нет пароля — нажмите «…»",
            _ => null,
        };
    }

    [RelayCommand]
    private void Edit()
    {
        var dialog = new ConnectionDialogViewModel(_service, Slot.Type, NeedsDatabase, GetChoice());
        if (Dialogs.EditConnection(dialog) is not { } choice)
            return;

        // Подключение могло измениться (пароль, логин) — пересобираем список.
        Options.Clear();
        foreach (var c in _service.GetRecent(Slot.Type))
            Options.Add(c);
        _databasesFor = null;
        Selected = null;
        SetChoice(choice);
        Changed();
    }

    /// <summary>Список БД для выпадающего списка; загружается один раз на подключение.</summary>
    [RelayCommand]
    private async Task LoadDatabasesAsync()
    {
        if (Selected is not { } connection || connection == _databasesFor)
            return;

        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var ct = _loading.Token;
        // Очистка списка сбрасывает текст редактируемого ComboBox — возвращаем его.
        var database = Database;
        try
        {
            var names = await Task.Run(() => _service.ListDatabasesAsync(connection, ct), ct);
            if (ct.IsCancellationRequested)
                return;
            Databases.Clear();
            foreach (var name in names)
                Databases.Add(name);
            Database = database;
            _databasesFor = connection;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Загрузку перезапустили для другого подключения.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Databases.Clear();
            Error = ex.GetBaseException().Message;
        }
    }
}
