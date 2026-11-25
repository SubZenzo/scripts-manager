using ScriptsManager.Core.Catalog;
using ScriptsManager.Core.Manifests;
using ScriptsManager.Core.Settings;

namespace ScriptsManager.Tests;

public sealed class CatalogAndSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void LoadRoot()
    {
        _dir.Write("b.sql");
        _dir.Write(".manifests/b.sql.yaml", "title: Бета\nrunner: tsql");
        _dir.Write("a.py");
        _dir.Write(".manifests/a.py.yaml", "title: Альфа\nrunner: python");
        _dir.Write("Очереди/q.sql");
        _dir.Write(".manifests/Очереди/q.sql.yaml", "runner: tsql");
        _dir.Write("empty/readme.txt");
        _dir.Write(".git/x.sql");

        var root = ScriptCatalog.LoadRoot(new ScriptRootSettings { Name = "Мои", Path = _dir.Path });

        Assert.Null(root.Error);
        Assert.Equal("Мои", root.Name);
        Assert.Equal(["Альфа", "Бета"], root.Scripts.Select(s => s.Title));
        Assert.All(root.Scripts, s => Assert.Empty(s.Result.Issues));
        var folder = Assert.Single(root.Folders);
        Assert.Equal("Очереди", folder.Name);
        var q = Assert.Single(folder.Scripts);
        Assert.Equal("q", q.Title);
        Assert.Equal(Path.Combine(_dir.Path, "Очереди", "q.sql"), q.ScriptPath);
        Assert.Equal(Path.Combine(_dir.Path, ".presets", "Очереди", "q.sql.yaml"), q.PresetsPath);
        Assert.Equal(Path.Combine(_dir.Path, ".output", "Очереди", "q"), q.OutputPath);
    }

    [Fact]
    public void DeleteScriptWithManifestAndPresets()
    {
        _dir.Write("Папка/a.ps1");
        _dir.Write(".manifests/Папка/a.ps1.yaml", "runner: powershell");
        _dir.Write(".presets/Папка/a.ps1.yaml", "- name: p\n  values: {}");
        _dir.Write(".manifests/Папка/a.ps1.md", "# a");
        _dir.Write("Папка/b.ps1");
        _dir.Write(".manifests/Папка/b.ps1.yaml", "runner: powershell");
        _dir.Write(".output/Папка/a/log.txt", "лог");

        var root = ScriptCatalog.LoadRoot(new ScriptRootSettings { Name = "r", Path = _dir.Path });
        var a = root.Folders.Single().Scripts.Single(s => s.Title == "a");
        var deleted = new List<string>();
        ScriptRemover.Delete(a, f => { deleted.Add(f); File.Delete(f); });

        Assert.Equal(Path.Combine(_dir.Path, ".manifests", "Папка", "a.ps1.md"), a.DescriptionPath);
        Assert.Equal([a.ScriptPath, a.ManifestPath, a.PresetsPath, a.DescriptionPath], deleted);
        // Опустевшая папка пресетов убрана, папка манифестов с другим скриптом и логи — остались.
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, ".presets", "Папка")));
        Assert.True(Directory.Exists(Path.Combine(_dir.Path, ".presets")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, ".manifests", "Папка", "b.ps1.yaml")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, ".output", "Папка", "a", "log.txt")));
        Assert.Equal(["b"], ScriptCatalog.LoadRoot(new ScriptRootSettings { Name = "r", Path = _dir.Path }).Folders.Single().Scripts.Select(s => s.Title));
    }

    [Fact]
    public void ScriptsWithoutManifestAreListed()
    {
        _dir.Write("Ещё/free.sql", "select 1");
        _dir.Write("Ещё/pg.sql", "do $$ begin raise notice 'x'; end $$");
        _dir.Write("Ещё/tool.py");
        _dir.Write("Ещё/notes.txt");

        var root = ScriptCatalog.LoadRoot(new ScriptRootSettings { Name = "r", Path = _dir.Path });

        var scripts = Assert.Single(root.Folders).Scripts;
        Assert.Equal(["free", "pg", "tool"], scripts.Select(s => s.Title));
        Assert.All(scripts, s => Assert.True(s.IsUnmanaged));
        Assert.Equal([RunnerKind.TSql, RunnerKind.PlPgSql, RunnerKind.Python], scripts.Select(s => s.UnmanagedRunner!.Value));
        Assert.Equal(Path.Combine(_dir.Path, ".manifests", "Ещё", "free.sql.yaml"), scripts[0].ManifestPath);
    }

    [Fact]
    public void SameNameDifferentLanguages()
    {
        _dir.Write("hello.ps1");
        _dir.Write(".manifests/hello.ps1.yaml", "runner: powershell");
        _dir.Write("hello.py");

        var root = ScriptCatalog.LoadRoot(new ScriptRootSettings { Name = "r", Path = _dir.Path });

        Assert.Equal(2, root.Scripts.Count);
        var py = Assert.Single(root.Scripts, s => s.IsUnmanaged);
        Assert.Equal(Path.Combine(_dir.Path, ".manifests", "hello.py.yaml"), py.ManifestPath);
    }

    [Fact]
    public void BrokenAndLostManifestsAreListed()
    {
        _dir.Write("bad.sql");
        _dir.Write(".manifests/bad.sql.yaml", "runner: [");
        _dir.Write("wrong.sql");
        _dir.Write(".manifests/wrong.sql.yaml", "runner: python");
        _dir.Write(".manifests/Старое/lost.ps1.yaml", "runner: powershell");

        var root = ScriptCatalog.LoadRoot(new ScriptRootSettings { Name = "r", Path = _dir.Path });

        Assert.Equal(["bad", "wrong"], root.Scripts.Select(s => s.Title));
        Assert.All(root.Scripts, s => Assert.True(s.Result.HasErrors && !s.IsUnmanaged));
        var lost = Assert.Single(Assert.Single(root.Folders).Scripts);
        Assert.Equal(Path.Combine(_dir.Path, "Старое", "lost.ps1"), lost.ScriptPath);
        Assert.Contains(lost.Result.Issues, i => i.Message.StartsWith("Файл скрипта не найден"));
    }

    [Fact]
    public void MissingRoot()
    {
        var root = ScriptCatalog.LoadRoot(new ScriptRootSettings { Name = "r", Path = Path.Combine(_dir.Path, "nope") });

        Assert.NotNull(root.Error);
    }

    [Fact]
    public void PresetFileSave()
    {
        var path = Path.Combine(_dir.Path, ".presets", "a.sql.yaml");
        PresetFile.Save(path, [new ScriptPreset("Неделя") { Values = new Dictionary<string, IReadOnlyList<string>> { ["from"] = ["today-7"] } }]);
        Assert.Equal("Неделя", Assert.Single(PresetFile.Load(path).Presets).Name);

        // Последний пресет удалён — файла нет.
        PresetFile.Save(path, []);
        Assert.False(File.Exists(path));
        Assert.Empty(PresetFile.Load(path).Presets);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var store = new SettingsStore(Path.Combine(_dir.Path, "cfg", "settings.json"));
        Assert.Empty(store.Load().Roots);

        store.Save(new AppSettings { Roots = [new() { Name = "Команда", Path = @"D:\scripts" }], PythonPath = "py" });
        var loaded = store.Load();

        Assert.Equal("Команда", Assert.Single(loaded.Roots).Name);
        Assert.Equal(@"D:\scripts", loaded.Roots[0].Path);
        Assert.Equal("py", loaded.PythonPath);
    }
}
