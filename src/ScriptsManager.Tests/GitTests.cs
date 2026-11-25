using System.Diagnostics;
using ScriptsManager.Core.Catalog;
using ScriptsManager.Core.Git;
using ScriptsManager.Core.Manifests;

namespace ScriptsManager.Tests;

public sealed class GitTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        // git помечает объекты только для чтения — снимаем атрибут, иначе папку не удалить.
        foreach (var f in Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        _dir.Dispose();
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
    }

    private string Clone(string origin, string name)
    {
        Git(_dir.Path, "clone", "-q", origin, name);
        var path = Path.Combine(_dir.Path, name);
        Git(path, "config", "user.name", "Test");
        Git(path, "config", "user.email", "test@example.com");
        return path;
    }

    private string Origin()
    {
        var origin = Path.Combine(_dir.Path, "origin.git");
        Git(_dir.Path, "init", "-q", "--bare", "-b", "main", origin);
        var seed = Clone(origin, "seed");
        File.WriteAllText(Path.Combine(seed, "a.sql"), "select 1");
        Git(seed, "add", "-A");
        Git(seed, "commit", "-q", "-m", "init");
        Git(seed, "push", "-q", "-u", "origin", "main");
        return origin;
    }

    [Fact]
    public async Task StatusCommitPushFetchPull()
    {
        var origin = Origin();
        var a = Clone(origin, "a");
        var b = Clone(origin, "b");

        File.WriteAllText(Path.Combine(a, "a.sql"), "select 2");
        _dir.Write(@"a\Очереди\новый.yaml", "runner: tsql");
        _dir.Write(@"a\other.txt", "не публикуем");

        var repo = (await GitRepository.OpenAsync(Path.Combine(a, "Очереди")))!;
        Assert.Equal(Path.GetFullPath(a), repo.Root);

        var status = await repo.StatusAsync();
        Assert.Equal(("main", "origin/main", 0, 0), (status.Branch, status.Upstream, status.Ahead, status.Behind));
        Assert.Equal(GitFileState.Modified, status.Files[Path.Combine(a, "a.sql")]);
        Assert.Equal(GitFileState.Untracked, status.Files[Path.Combine(a, "Очереди", "новый.yaml")]);
        Assert.Equal(3, status.Changed);

        var log = await repo.CommitAndPushAsync([Path.Combine(a, "a.sql"), Path.Combine(a, "Очереди", "новый.yaml")], "Новый скрипт");
        Assert.Contains("Новый скрипт", log);

        status = await repo.StatusAsync();
        Assert.Equal((0, 0), (status.Ahead, status.Behind));
        Assert.Equal(GitFileState.Untracked, Assert.Single(status.Files).Value);

        var other = (await GitRepository.OpenAsync(b))!;
        await other.FetchAsync();
        Assert.Equal(1, (await other.StatusAsync()).Behind);
        await other.PullAsync();
        Assert.Equal(0, (await other.StatusAsync()).Behind);
        Assert.True(File.Exists(Path.Combine(b, "Очереди", "новый.yaml")));
    }

    [Fact]
    public async Task CommitWithoutRemote()
    {
        var dir = Path.Combine(_dir.Path, "local");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "user.email", "test@example.com");
        _dir.Write(@"local\s.sql", "select 1");

        var repo = (await GitRepository.OpenAsync(dir))!;
        Assert.False(await repo.HasRemoteAsync());
        Assert.Equal("main", (await repo.StatusAsync()).Branch);

        var log = await repo.CommitAndPushAsync([Path.Combine(dir, "s.sql")], "first");

        Assert.Contains("только закоммичены", log);
        Assert.Empty((await repo.StatusAsync()).Files);
    }

    [Fact]
    public async Task NotARepository()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsm-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Временная папка может оказаться внутри чужого репозитория только в экзотических случаях.
            Assert.Null(await GitRepository.OpenAsync(dir));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void ParseStatus()
    {
        var status = GitRepository.ParseStatus(@"C:\r",
            "## feature...origin/feature [ahead 2, behind 3]\0 M a.sql\0R  new.sql\0old.sql\0?? x/y.yaml\0UU c.sql\0 D d.sql\0A  e.sql\0"
            + "?? .output/deploy/log.txt\0?? sub/.output/a/b.txt\0");

        Assert.Equal(("feature", "origin/feature", 2, 3), (status.Branch, status.Upstream, status.Ahead, status.Behind));
        Assert.Equal(GitFileState.Modified, status.Files[@"C:\r\a.sql"]);
        Assert.Equal(GitFileState.Renamed, status.Files[@"C:\r\new.sql"]);
        Assert.Equal(GitFileState.Deleted, status.Files[@"C:\r\old.sql"]);
        Assert.Equal(GitFileState.Untracked, status.Files[@"C:\r\x\y.yaml"]);
        Assert.Equal(GitFileState.Conflict, status.Files[@"C:\r\c.sql"]);
        Assert.Equal(GitFileState.Deleted, status.Files[@"C:\r\d.sql"]);
        Assert.Equal(GitFileState.Added, status.Files[@"C:\r\e.sql"]);
        // Вывод скриптов (.output) в изменения не попадает.
        Assert.Equal(7, status.Changed);

        var fresh = GitRepository.ParseStatus(@"C:\r", "## No commits yet on main\0");
        Assert.Equal(("main", null, 0), (fresh.Branch, fresh.Upstream, fresh.Ahead));
        Assert.Equal("main", GitRepository.ParseStatus(@"C:\r", "## main\0").Branch);
    }

    [Fact]
    public async Task CommitPartOfPresets()
    {
        var dir = Path.Combine(_dir.Path, "local");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "user.email", "test@example.com");
        var presets = _dir.Write(@"local\.presets\s.sql.yaml", "- name: a\n- name: b\n");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", "init");

        // Уже подготовленное в индексе не должно попасть в коммит.
        _dir.Write(@"local\staged.txt", "x");
        Git(dir, "add", "staged.txt");
        File.WriteAllText(presets, "- name: a\n  values:\n    v: 2\n- name: c\n");

        var repo = (await GitRepository.OpenAsync(dir))!;
        var published = PresetFile.Parse((await repo.ReadCommittedAsync(presets))!).Presets;
        var current = PresetFile.Load(presets).Presets;
        var partial = PresetFile.Write(PresetChanges.Apply(published, current, ["c"]));
        await repo.CommitAndPushAsync([], "пресет c", new Dictionary<string, string> { [presets] = partial });

        Assert.Equal(["a", "b", "c"], PresetFile.Parse((await repo.ReadCommittedAsync(presets))!).Presets.Select(p => p.Name));
        Assert.Null(await repo.ReadCommittedAsync(Path.Combine(dir, "staged.txt")));
        var status = await repo.StatusAsync();
        Assert.Equal(GitFileState.Modified, status.Files[presets]);
        Assert.Equal(GitFileState.Added, status.Files[Path.Combine(dir, "staged.txt")]);
        Assert.Equal("- name: a\n  values:\n    v: 2\n- name: c\n", File.ReadAllText(presets));
    }

    [Fact]
    public async Task CommitRename()
    {
        var dir = Path.Combine(_dir.Path, "local");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "user.email", "test@example.com");
        _dir.Write(@"local\a.yaml", "runner: tsql");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", "init");
        Directory.CreateDirectory(Path.Combine(dir, ".manifests"));
        Git(dir, "mv", "a.yaml", ".manifests/a.sql.yaml");

        var repo = (await GitRepository.OpenAsync(dir))!;
        var status = await repo.StatusAsync();
        Assert.Equal(GitFileState.Deleted, status.Files[Path.Combine(dir, "a.yaml")]);
        await repo.CommitAndPushAsync(status.Files.Keys.ToList(), "перенос");

        Assert.Null(await repo.ReadCommittedAsync(Path.Combine(dir, "a.yaml")));
        Assert.Equal("runner: tsql", await repo.ReadCommittedAsync(Path.Combine(dir, ".manifests", "a.sql.yaml")));
        Assert.Empty((await repo.StatusAsync()).Files);
    }

    [Fact]
    public void CopyScript()
    {
        _dir.Write(@"team\x.txt");
        var script = _dir.Write(@"my\Очереди\s.sql", "select 1");
        var source = Path.Combine(_dir.Path, "my");
        var target = Path.Combine(_dir.Path, "team");
        var manifest = _dir.Write(@"my\.manifests\Очереди\s.sql.yaml", "runner: tsql");
        _dir.Write(@"my\.presets\Очереди\s.sql.yaml", "- name: p\n");
        _dir.Write(@"my\.manifests\Очереди\s.sql.md", "# s");
        var entry = new ScriptEntry(script, manifest, ScriptLayout.PresetsPath(source, script), ManifestLoader.Load(manifest, script));

        var (m, s, p) = ScriptCopier.Copy(entry, source, target, overwrite: false);

        Assert.Equal(Path.Combine(target, ".manifests", "Очереди", "s.sql.yaml"), m);
        Assert.Equal("select 1", File.ReadAllText(s));
        Assert.Equal("- name: p\n", File.ReadAllText(p));
        Assert.Equal("# s", File.ReadAllText(Path.Combine(target, ".manifests", "Очереди", "s.sql.md")));
        Assert.Throws<IOException>(() => ScriptCopier.Copy(entry, source, target, overwrite: false));

        // Описание убрали — при замене копии оно не остаётся.
        File.Delete(entry.DescriptionPath);
        ScriptCopier.Copy(entry, source, target, overwrite: true);
        Assert.False(File.Exists(Path.Combine(target, ".manifests", "Очереди", "s.sql.md")));
    }
}
