using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.App.ViewModels;
using ScriptsManager.Core.Connections;
using ScriptsManager.Core.Runs;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        WindowFit.FitOnStartup(this);   // иначе на экранах меньше 1200×750 окно не помещается
        var connections = new ConnectionService(ConnectionStore.Default, new WindowsCredentialStore(), new ConnectionFactory());
        _viewModel = new MainViewModel(SettingsStore.Default, connections)
        {
            EditSettings = EditSettings,
            EditScriptDialog = EditScript,
        };
        DataContext = _viewModel;
        _viewModel.IsWindowActive = () => IsActive;
        _viewModel.RunFinished += OnRunFinished;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HasRuns))
                UpdateRunsRow();
        };
        Closed += (_, _) => _tray.Dispose();
    }

    private readonly TrayNotifier _tray = new();

    // Окно неактивно — мигание на панели задач и уведомление Windows; щелчок по нему открывает результат.
    private void OnRunFinished(RunViewModel run)
    {
        if (IsActive || run.IsSeen)
            return;
        TaskbarFlash.Flash(this);
        _tray.Show(MainViewModel.FinishedText(run), run.Details, run.State == RunState.Failed, () =>
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            _viewModel.OpenRun(run);
        });
    }

    // Список запусков: появляется с первым запуском (высота по умолчанию — 200), прячется, когда пуст;
    // перетащенную высоту помним.
    private GridLength _runsHeight = new(200);

    private void UpdateRunsRow()
    {
        if (_viewModel.HasRuns)
        {
            if (RunsRow.Height.Value == 0)
                RunsRow.Height = _runsHeight;
        }
        else if (RunsRow.Height.Value > 0)
        {
            _runsHeight = RunsRow.Height;
            RunsRow.Height = new GridLength(0);
        }
    }

    private void Window_Activated(object? sender, EventArgs e) => _viewModel.WindowActivated();

    private static RunViewModel? RunOf(object sender) => (sender as FrameworkElement)?.DataContext as RunViewModel;

    private void RunMenu_Open(object sender, RoutedEventArgs e)
    {
        if (RunOf(sender) is { } run)
            _viewModel.OpenRun(run);
    }

    private void RunMenu_Rename(object sender, RoutedEventArgs e)
    {
        if (RunOf(sender) is { } run)
            _viewModel.RenameRun(run);
    }

    private void RunMenu_Delete(object sender, RoutedEventArgs e)
    {
        if (RunOf(sender) is { } run)
            _viewModel.DeleteRun(run);
    }

    // Щелчок по запуску в списке — открыть его результат (и повторный щелчок по тому же).
    private void RunsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement((ListBox)sender, source) is ListBoxItem { DataContext: RunViewModel run })
            _viewModel.OpenRun(run);
    }

    private string? EditScript(ScriptEditorViewModel editor)
    {
        var window = new ScriptEditorWindow(editor) { Owner = this };
        return window.ShowDialog() == true ? window.ManifestPath : null;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // Перетащенный файл скрипта открывается в окне добавления.
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            Dispatcher.BeginInvoke(() => _viewModel.AddScriptFromFile(files[0]));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var running = _viewModel.Runs.Running.ToList();
        if (running.Count > 0)
        {
            if (!Dialogs.Confirm($"Выполняется скриптов: {running.Count}. Остановить их и закрыть приложение?"))
            {
                e.Cancel = true;
                return;
            }

            foreach (var run in running)
                run.StopCommand.Execute(null);
        }

        base.OnClosing(e);
    }

    private AppSettings? EditSettings(AppSettings settings)
    {
        var window = new SettingsWindow(new SettingsViewModel(settings)) { Owner = this };
        return window.ShowDialog() == true ? window.Result : null;
    }

    private void OutputFile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is OutputFilesViewModel output && output.OpenFileCommand.CanExecute(null))
            output.OpenFileCommand.Execute(null);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.Select(e.NewValue as TreeNodeViewModel);

    // Столбцы таблицы называются c0, c1…: настоящее имя — в Caption (в имени могут быть «.», «[», «_»).
    private void ResultGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is not DataGrid { ItemsSource: DataView view } || e.Column is not DataGridTextColumn column)
            return;

        column.Header = new TextBlock { Text = view.Table!.Columns[e.PropertyName]!.Caption };
        column.MaxWidth = 500;
        // Сортировка хранится в DataView и переживает переключение вкладок; стрелку в заголовке восстанавливаем.
        if (((DataGrid)sender!).Items.SortDescriptions.FirstOrDefault(s => s.PropertyName == e.PropertyName) is { PropertyName: not null } sort)
            column.SortDirection = sort.Direction;
        if (column.Binding is Binding binding)
            binding.Converter = ResultCellConverter.Instance;
        column.ElementStyle = NullStyle;
    }

    private static readonly Style NullStyle = CreateNullStyle();

    private static Style CreateNullStyle()
    {
        // Одна строка в ячейке; NULL — серым, как в SSMS.
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(MaxHeightProperty, 18.0));
        // Отступ, чтобы текст соседних столбцов не сливался.
        style.Setters.Add(new Setter(MarginProperty, new Thickness(4, 1, 6, 1)));
        var trigger = new Trigger { Property = TextBlock.TextProperty, Value = "NULL" };
        trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Gray));
        style.Triggers.Add(trigger);
        return style;
    }

    private void ResultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.CurrentCell is not { Column: not null, Item: DataRowView row } cell
            || e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(grid, source) is null)
            return;

        var name = ((DataView)grid.ItemsSource).Table!.Columns[cell.Column.SortMemberPath]!.Caption;
        Dialogs.ShowText(name, ResultTables.Format(row[cell.Column.SortMemberPath]));
    }

    // Своё копирование: стандартное берёт в заголовок TextBlock (его ToString), а не имя столбца.
    private void ResultGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ~ModifierKeys.Shift) != ModifierKeys.Control)
            return;

        ResultGridAction((DataGrid)sender, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "copy-headers" : "copy");
        e.Handled = true;
    }

    private void ResultGridAction_Click(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        ResultGridAction((DataGrid)((ContextMenu)element.Parent).PlacementTarget, (string)element.Tag);
    }

    // «Копировать как ▾» над таблицей: то же меню форматов, что в контекстном меню.
    private void CopyAsButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var grid = ((Panel)((FrameworkElement)button.Parent).Parent).Children.OfType<DataGrid>().Single();
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var item in CopyAsItems(grid))
            menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private void CopyAsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (MenuItem)sender;
        if (e.OriginalSource != menu)
            return;

        menu.Items.Clear();
        foreach (var item in CopyAsItems((DataGrid)((ContextMenu)menu.Parent).PlacementTarget))
            menu.Items.Add(item);
    }

    private static IEnumerable<Control> CopyAsItems(DataGrid grid)
    {
        foreach (var format in TableExport.Formats)
        {
            var item = new MenuItem { Header = format.Name };
            item.Click += (_, _) => ResultGridAction(grid, "copy-as", format);
            yield return item;
        }
        yield return new Separator();
        var show = new MenuItem { Header = "Показать текстом…" };
        show.Click += (_, _) => ResultGridAction(grid, "show");
        yield return show;
    }

    /// <param name="format">Для «copy-as».</param>
    private static void ResultGridAction(DataGrid grid, string action, TableFormat? format = null)
    {
        var result = (ResultSetViewModel)grid.DataContext;
        var all = result.All;
        var order = all.Rows.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i);
        var cells = grid.SelectedCells
            .Where(c => c is { Column: not null, Item: DataRowView })
            .Select(c => (Row: ((DataRowView)c.Item).Row, Column: c.Column, Data: result.Table.Columns[c.Column.SortMemberPath]!))
            .ToList();
        var rows = cells.Select(c => c.Row).Distinct().OrderBy(r => order[r]).ToList();
        var columns = cells.DistinctBy(c => c.Column).OrderBy(c => c.Column.DisplayIndex).Select(c => c.Data).ToList();
        // Прямоугольник из выделенных строк и столбцов; при копировании невыделенные ячейки в нём пустые, как в Excel.
        var selection = new TableSlice(columns, rows);

        switch (action)
        {
            case "copy" or "copy-headers" when cells.Count > 0:
                ClipboardText.Set(cells.Count == 1 && action == "copy"
                    ? ResultTables.Format(rows[0][columns[0]])
                    : TableExport.Tsv(selection with { Selected = cells.Select(c => (c.Row, c.Data)).ToHashSet() }, action == "copy-headers"));
                break;
            // Одна ячейка или ничего — значит, вся таблица.
            case "copy-as":
                TableTextViewModel.LastFormat = format!;
                ClipboardText.Set(format!.Render(cells.Count > 1 ? selection : all));
                break;
            case "show":
                var viewModel = new TableTextViewModel(result.Title, all, cells.Count > 1 ? selection : null);
                new TableTextWindow(viewModel) { Owner = GetWindow(grid) }.ShowDialog();
                break;
        }
    }

    // Правый щелчок по заголовку — сортировка (левый занят выделением столбца).
    private void ResultColumnHeader_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var header = (DataGridColumnHeader)sender;
        DependencyObject? parent = header;
        while (parent is not null and not DataGrid)
            parent = VisualTreeHelper.GetParent(parent);
        if (header.Column is not { } column || parent is not DataGrid grid)
            return;

        var canSort = ((ResultSetViewModel)grid.DataContext).CanSort;
        var menu = new ContextMenu { PlacementTarget = header };
        void Add(string text, ListSortDirection? direction)
        {
            var item = new MenuItem
            {
                Header = text,
                IsEnabled = canSort,
                IsChecked = column.SortDirection == direction && (direction is not null || grid.Items.SortDescriptions.Count == 0),
            };
            item.Click += (_, _) => SortResult(grid, column, direction);
            menu.Items.Add(item);
        }

        Add("По возрастанию", ListSortDirection.Ascending);
        Add("По убыванию", ListSortDirection.Descending);
        Add("Без сортировки", null);
        if (!canSort)
            menu.Items.Add(new MenuItem { Header = $"Сортировка — не больше {ResultSetViewModel.MaxSortRows} строк", IsEnabled = false });
        menu.IsOpen = true;
    }

    private static void SortResult(DataGrid grid, DataGridColumn column, ListSortDirection? direction)
    {
        grid.UnselectAllCells();
        foreach (var c in grid.Columns)
            c.SortDirection = null;
        grid.Items.SortDescriptions.Clear();
        if (direction is { } d)
        {
            grid.Items.SortDescriptions.Add(new SortDescription(column.SortMemberPath, d));
            column.SortDirection = d;
        }
    }

    // Щелчок по заголовку столбца выделяет столбец (как по заголовку строки — строку): Ctrl — добавить, Shift — диапазон.
    private void ResultColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        var header = (DataGridColumnHeader)sender;
        DependencyObject? parent = header;
        while (parent is not null and not DataGrid)
            parent = VisualTreeHelper.GetParent(parent);
        if (header.Column is not { } column || parent is not DataGrid grid)
            return;

        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var anchor = shift && grid.Tag is DataGridColumn previous && grid.Columns.Contains(previous) ? previous : column;
        if (!shift)
            grid.Tag = column;
        var (from, to) = (Math.Min(anchor.DisplayIndex, column.DisplayIndex), Math.Max(anchor.DisplayIndex, column.DisplayIndex));
        var columns = grid.Columns.Where(c => c.DisplayIndex >= from && c.DisplayIndex <= to).ToList();

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            grid.UnselectAllCells();
        foreach (var item in grid.Items)
            foreach (var c in columns)
                grid.SelectedCells.Add(new DataGridCellInfo(item, c));
        if (grid.Items.Count > 0)
            grid.CurrentCell = new DataGridCellInfo(grid.Items[0], column);
        grid.Focus();
    }
}
