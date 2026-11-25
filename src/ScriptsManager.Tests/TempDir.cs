namespace ScriptsManager.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("dsm-tests-").FullName;

    /// <param name="bom">UTF-8 с BOM — для .ps1: Windows PowerShell без BOM читает файл в ANSI.</param>
    public string Write(string relativePath, string content = "", bool bom = false)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(bom));
        return full;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
