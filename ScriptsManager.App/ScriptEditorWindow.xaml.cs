using System.ComponentModel;
using System.Windows;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.App.ViewModels;

namespace ScriptsManager.App;

public partial class ScriptEditorWindow : Window
{
    private readonly ScriptEditorViewModel _viewModel;
    private bool _syncing;

    public ScriptEditorWindow(ScriptEditorViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.FitOnStartup(this);   // 1180×780 не помещаются на небольших экранах
        _viewModel = viewModel;
        DataContext = viewModel;

        // У AvalonEdit нет привязки Text: синхронизируем вручную.
        Editor.Text = viewModel.ScriptText;
        Editor.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;
            _syncing = true;
            _viewModel.ScriptText = Editor.Text;
            _syncing = false;
        };
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DescriptionEditor.Text = viewModel.ExtendedDescription;
        DescriptionEditor.TextChanged += (_, _) => _viewModel.ExtendedDescription = DescriptionEditor.Text;
        DataObject.AddPastingHandler(Editor.TextArea, (_, _) =>
        {
            var wasEmpty = Editor.Text.Trim().Length == 0;
            Dispatcher.BeginInvoke(async () => await _viewModel.OnPastedAsync(wasEmpty));
        });

        Loaded += (_, _) =>
        {
            if (viewModel.IsNew)
                Editor.Focus();
            else
                TitleBox.Focus();
        };
    }

    public string? ManifestPath { get; private set; }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScriptEditorViewModel.ScriptText) || _syncing)
            return;
        _syncing = true;
        Editor.Text = _viewModel.ScriptText;
        _syncing = false;
    }

    // Preview-события: иначе файл перехватывает AvalonEdit. Перетаскивание текста внутри редактора не трогаем.
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            e.Handled = true;
            await _viewModel.LoadFileAsync(files[0]);
        }
    }

    // Тег, выбранный в выпадающем списке, добавляется сразу.
    private void TagBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (TagBox.SelectedItem is not string tag)
            return;
        _viewModel.NewTag = tag;
        _viewModel.AddTagCommand.Execute(null);
        TagBox.SelectedItem = null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // Введённый, но не добавленный тег не теряется.
        if (_viewModel.NewTag.Trim().Length > 0)
            _viewModel.AddTagCommand.Execute(null);

        var (path, error) = await _viewModel.SaveAsync(Dialogs.Confirm);
        if (error is not null)
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (path is null)
            return;

        ManifestPath = path;
        DialogResult = true;
    }
}
