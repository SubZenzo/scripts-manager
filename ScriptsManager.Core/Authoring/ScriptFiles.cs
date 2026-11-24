using System.Text;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Core.Authoring;

/// <summary>Чтение скрипта для импорта и сохранение скрипта с манифестом.</summary>
public static class ScriptFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Текст файла: UTF-8 (с BOM или без), UTF-16 с BOM, иначе Windows-1251.</summary>
    public static string Read(string path) => Decode(File.ReadAllBytes(path));

    public static string Decode(byte[] bytes)
    {
        using (var reader = new StreamReader(new MemoryStream(bytes), StrictUtf8, detectEncodingFromByteOrderMarks: true))
        {
            try
            {
                return reader.ReadToEnd();
            }
            catch (DecoderFallbackException)
            {
            }
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251).GetString(bytes);
    }

    /// <summary>Имя файла из названия скрипта: без символов, недопустимых в Windows.</summary>
    public static string FileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "script" : name;
    }

    /// <summary>
    /// Записывает манифест и, если <paramref name="scriptText"/> не null, скрипт (UTF-8; .ps1 и .sql — с BOM:
    /// Windows PowerShell без BOM читает файл в ANSI). Существующий скрипт сохраняет свой BOM.
    /// </summary>
    public static void Save(ScriptManifest manifest, string manifestPath, string? scriptText)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        if (scriptText is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifest.ScriptPath))!);
            var bom = File.Exists(manifest.ScriptPath)
                ? HasBom(manifest.ScriptPath) || manifest.Runner == RunnerKind.PowerShell
                : manifest.Runner != RunnerKind.Python;
            File.WriteAllText(manifest.ScriptPath, scriptText, new UTF8Encoding(bom));
        }

        File.WriteAllText(manifestPath, ManifestWriter.Write(manifest), new UTF8Encoding(false));
    }

    private static bool HasBom(string path)
    {
        Span<byte> head = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == 3 && head.SequenceEqual(Encoding.UTF8.Preamble);
    }
}
