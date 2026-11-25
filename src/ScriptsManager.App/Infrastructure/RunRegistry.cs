using System.Collections.ObjectModel;
using System.ComponentModel;
using ScriptsManager.App.ViewModels;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Runs;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.App.Infrastructure;

/// <summary>Запуски за сеанс (живут дольше карточки скрипта; один скрипт можно запускать несколько раз) и runners по языкам.</summary>
public sealed class RunRegistry(ConnectionService connections, Func<AppSettings> settings)
{
    /// <summary>Все запуски, новые сверху.</summary>
    public ObservableCollection<RunViewModel> All { get; } = [];

    /// <summary>Добавили или убрали запуск, поменялось состояние или отметка «посмотрели».</summary>
    public event Action? Changed;

    /// <summary>Запуск завершился (успешно, с ошибкой или остановлен).</summary>
    public event Action<RunViewModel>? Finished;

    /// <summary>Запуски скрипта, новые сверху.</summary>
    public IReadOnlyList<RunViewModel> For(string manifestPath) =>
        All.Where(r => r.ManifestPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase)).ToList();

    public RunViewModel? Latest(string manifestPath) => For(manifestPath).FirstOrDefault();

    public IEnumerable<RunViewModel> Running => All.Where(r => r.IsRunning);

    public void Add(RunViewModel run)
    {
        run.PropertyChanged += OnRunChanged;
        All.Insert(0, run);
        Changed?.Invoke();
    }

    /// <summary>Убрать завершённые запуски (их результаты больше не нужны).</summary>
    public void RemoveFinished()
    {
        foreach (var run in All.Where(r => !r.IsRunning).ToList())
            Detach(run);
        Changed?.Invoke();
    }

    /// <summary>Убрать один завершённый запуск.</summary>
    public void Remove(RunViewModel run)
    {
        if (run.IsRunning)
            return;
        Detach(run);
        Changed?.Invoke();
    }

    private void Detach(RunViewModel run)
    {
        run.PropertyChanged -= OnRunChanged;
        All.Remove(run);
    }

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RunViewModel.State) or nameof(RunViewModel.IsSeen) or nameof(RunViewModel.Name)))
            return;
        var run = (RunViewModel)sender!;
        if (e.PropertyName == nameof(RunViewModel.State) && !run.IsRunning)
            Finished?.Invoke(run);
        Changed?.Invoke();
    }

    /// <summary>Runner для языка; пути к интерпретаторам — из текущих настроек.</summary>
    public IScriptRunner? CreateRunner(RunnerKind kind) => kind switch
    {
        RunnerKind.TSql => new TSqlRunner(connections.Factory),
        RunnerKind.PlPgSql => new PlPgSqlRunner(connections.Factory),
        RunnerKind.PowerShell => new ProcessRunner(kind, settings().PowerShellPath),
        RunnerKind.Python => new ProcessRunner(kind, settings().PythonPath),
        _ => null,
    };
}
