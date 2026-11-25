using System.Diagnostics;
using System.IO;

namespace ScriptsManager.App.Infrastructure;

public static class FileLauncher
{
    // Для этих расширений действие по умолчанию — запуск, а не открытие.
    private static readonly string[] Executable = [".py", ".ps1", ".bat", ".cmd", ".exe", ".vbs", ".js"];

    /// <summary>Открывает файл в редакторе, не запуская его.</summary>
    public static void OpenForEdit(string path)
    {
        if (!Executable.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        var edit = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" };
        if (edit.Verbs.Contains("edit", StringComparer.OrdinalIgnoreCase))
            Process.Start(edit);
        else
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Удаляет файл в корзину Windows.</summary>
    public static void MoveToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
}
