using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.Core.Runs;
using Microsoft.Win32;

namespace ScriptsManager.App.ViewModels;

public enum RunState { Running, Succeeded, Failed, Cancelled }

/// <summary>Вкладка «Сообщения» запуска.</summary>
public sealed class MessagesTab(RunViewModel run)
{
    public RunViewModel Run { get; } = run;
    public string Title => "Сообщения";
}

/// <summary>Вкладка с результирующим набором.</summary>
public sealed partial class ResultSetViewModel(string title, string fileName, DataTable table)
{
    public string Title { get; } = title;
    public DataTable Table { get; } = table;
    public DataView View => Table.DefaultView;

    /// <summary>Сортировка — только для небольших результатов.</summary>
    public const int MaxSortRows = 5000;

    public bool CanSort => Table.Rows.Count <= MaxSortRows;

    /// <summary>Вся таблица; строки в порядке показа (с учётом сортировки).</summary>
    public TableSlice All => new(Table.Columns.Cast<DataColumn>().ToList(), View.Cast<DataRowView>().Select(r => r.Row).ToList());

    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = "Excel|*.xlsx|CSV|*.csv", FileName = fileName, AddExtension = true };
        if (dialog.ShowDialog() != true)
            return;

        if (Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            // UTF-8 с BOM — чтобы Excel сразу открыл кириллицу.
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
            ResultTables.WriteCsv(Table, writer);
        }
        else
        {
            using var stream = File.Create(dialog.FileName);
            TableExport.WriteXlsx(All, stream);
        }
    }
}

/// <summary>Один запуск скрипта: лог, таблицы, состояние, остановка.</summary>
public sealed partial class RunViewModel : ObservableObject, IRunOutput
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;
    private readonly StringBuilder _log = new();
    private int _resultCount;

    public RunViewModel(string manifestPath, string scriptTitle, string connection)
    {
        ManifestPath = manifestPath;
        ScriptTitle = scriptTitle;
        Connection = connection;
        StartedAt = DateTime.Now;
        Tabs.Add(new MessagesTab(this));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateElapsed(), _dispatcher);
    }

    /// <summary>Манифест скрипта — по нему запуск находит свой скрипт в дереве.</summary>
    public string ManifestPath { get; }
    public string ScriptTitle { get; }
    public DateTime StartedAt { get; }
    public string Connection { get; }

    /// <summary>Завершённый запуск посмотрели (до этого скрипт отмечен в дереве и в списке запусков).</summary>
    [ObservableProperty]
    private bool _isSeen;

    /// <summary>Своё название запуска, чтобы отличать результаты; null — название скрипта.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(Caption))]
    private string? _name;

    public string DisplayName => Name ?? ScriptTitle;

    /// <summary>Время, подключение, состояние.</summary>
    public string Details => $"{StartedAt:HH:mm:ss}" + (Connection.Length > 0 ? $" · {Connection}" : "") + $" · {StateName}";

    /// <summary>Строка выбора запуска в карточке: своё название (если задано) и подробности.</summary>
    public string Caption => (Name is null ? "" : Name + " · ") + Details;

    public string StateName => State switch
    {
        RunState.Running => "выполняется",
        RunState.Succeeded => "выполнено",
        RunState.Failed => "ошибка",
        _ => "остановлено",
    };
    public ObservableCollection<object> Tabs { get; } = [];

    /// <summary>Весь лог (для заполнения TextBox при показе вкладки).</summary>
    public string LogText => _log.ToString();

    /// <summary>Новая порция лога — TextBox дописывает её сам, не перерисовывая весь текст.</summary>
    public event Action<string>? LogAppended;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(StatusText), nameof(Caption), nameof(Details), nameof(StateName))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private RunState _state = RunState.Running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private TimeSpan _elapsed;

    [ObservableProperty]
    private int _selectedTabIndex;

    public bool IsRunning => State == RunState.Running;

    public string StatusText => (State switch
    {
        RunState.Running => "Выполняется",
        RunState.Succeeded => "Выполнено",
        RunState.Failed => "Ошибка",
        _ => "Остановлено",
    }) + $" · {Elapsed:hh\\:mm\\:ss}" + (Connection.Length > 0 ? $" · {Connection}" : "") + $" · начато в {StartedAt:HH:mm:ss}";

    /// <summary>С чем запущен — для повторного запуска.</summary>
    public RunRequest? Request { get; private set; }

    public async Task RunAsync(IScriptRunner runner, RunRequest request)
    {
        Request = request;
        _stopwatch.Start();
        _timer.Start();
        try
        {
            await Task.Run(() => runner.RunAsync(request, this, _cts.Token));
            Finish(RunState.Succeeded, "Выполнено");
        }
        catch (OperationCanceledException)
        {
            Finish(RunState.Cancelled, "Остановлено пользователем");
        }
        catch (ScriptFailedException ex)
        {
            Finish(RunState.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            AppendLog(LogKind.Error, ex.GetBaseException().Message);
            Finish(RunState.Failed, "Скрипт завершился с ошибкой");
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts.Cancel();

    public void Log(LogKind kind, string text) => _dispatcher.BeginInvoke(() => AppendLog(kind, text));

    public void Result(DataTable table) => _dispatcher.BeginInvoke(() =>
    {
        _resultCount++;
        Tabs.Add(new ResultSetViewModel($"Результат {_resultCount} ({table.Rows.Count})",
            $"{ScriptTitle} {_resultCount}", table));
        // Как в SSMS: первыми показываем результаты.
        if (_resultCount == 1)
            SelectedTabIndex = 1;
    });

    private void Finish(RunState state, string message)
    {
        _stopwatch.Stop();
        _timer.Stop();
        UpdateElapsed();
        AppendLog(state == RunState.Failed ? LogKind.Error : LogKind.Info,
            $"{message} за {Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} с");
        State = state;
        if (state != RunState.Succeeded)
            SelectedTabIndex = 0;
    }

    private void AppendLog(LogKind kind, string text)
    {
        var line = (kind == LogKind.Error ? "ОШИБКА: " : "") + text + Environment.NewLine;
        _log.Append(line);
        LogAppended?.Invoke(line);
    }

    private void UpdateElapsed() => Elapsed = _stopwatch.Elapsed;
}
