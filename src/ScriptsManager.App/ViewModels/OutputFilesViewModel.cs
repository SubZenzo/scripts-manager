using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.Core.Authoring;

namespace ScriptsManager.App.ViewModels;

public sealed record OutputFileRow(string Path, string Name, DateTime Modified, long Size)
{
    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string SizeText => Size switch
    {
        < 1024 => $"{Size} Б",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} КБ",
        _ => $"{Size / 1024.0 / 1024.0:0.#} МБ",
    };
}

/// <summary>Вкладка «Вывод»: файлы, которые скрипт пишет в OUTPUT_DIR (логи и т.п.), новые сверху.</summary>
public sealed partial class OutputFilesViewModel(string folder) : ObservableObject
{
    /// <summary>Сколько показывать от большого файла — конец, там свежие записи лога.</summary>
    private const int MaxShownBytes = 4 * 1024 * 1024;

    public string Folder { get; } = folder;

    [ObservableProperty]
    private IReadOnlyList<OutputFileRow> _files = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand), nameof(DeleteFileCommand))]
    private OutputFileRow? _selectedFile;

    [ObservableProperty]
    private string? _content;

    partial void OnSelectedFileChanged(OutputFileRow? value) => Content = value is null ? null : ReadContent(value.Path);

    /// <summary>Перечитывает список; выбранный файл остаётся выбранным и перечитывается (лог мог дописаться).</summary>
    [RelayCommand]
    public void Refresh()
    {
        var selected = SelectedFile?.Path;
        try
        {
            Files = Directory.Exists(Folder)
                ? new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories)
                    .Select(f => new OutputFileRow(f.FullName, System.IO.Path.GetRelativePath(Folder, f.FullName), f.LastWriteTime, f.Length))
                    .OrderByDescending(f => f.Modified)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Files = [];
            Content = $"Не удалось прочитать папку: {ex.Message}";
            return;
        }

        var row = Files.FirstOrDefault(f => f.Path.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? Files.FirstOrDefault();
        if (row == SelectedFile)
            OnSelectedFileChanged(row);
        else
            SelectedFile = row;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFile() => FileLauncher.OpenForEdit(SelectedFile!.Path);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteFile()
    {
        var file = SelectedFile!;
        if (!Dialogs.Confirm($"Удалить «{file.Name}»? Файл удаляется без корзины."))
            return;

        try
        {
            File.Delete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Content = $"Не удалось удалить файл: {ex.Message}";
            return;
        }

        SelectedFile = null;
        Refresh();
    }

    private bool HasSelection() => SelectedFile is not null;

    private static string ReadContent(string path)
    {
        try
        {
            // Лог может дописываться прямо сейчас — читаем, не мешая записи.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skipped = Math.Max(0, stream.Length - MaxShownBytes);
            stream.Seek(skipped, SeekOrigin.Begin);
            var bytes = new byte[stream.Length - skipped];
            stream.ReadExactly(bytes);
            if (skipped == 0)
                return ScriptFiles.Decode(bytes);

            // С середины — с начала следующей строки, чтобы не разрезать символ.
            var start = Array.IndexOf(bytes, (byte)'\n') + 1;
            return $"… показаны последние {bytes.Length / 1024 / 1024} МБ из {stream.Length / 1024 / 1024} МБ\n"
                + ScriptFiles.Decode(bytes[start..]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Не удалось прочитать файл: {ex.Message}";
        }
    }
}
