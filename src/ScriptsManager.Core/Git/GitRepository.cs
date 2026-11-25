using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ScriptsManager.Core.Catalog;

namespace ScriptsManager.Core.Git;

public enum GitFileState { Modified, Added, Untracked, Deleted, Renamed, Conflict }

/// <summary>Состояние рабочей копии: ветка, отставание/опережение от upstream и изменённые файлы (полные пути).</summary>
public sealed record GitStatus(string? Branch, string? Upstream, int Ahead, int Behind, IReadOnlyDictionary<string, GitFileState> Files)
{
    public int Changed => Files.Count;
}

public sealed class GitException(string message) : Exception(message);

/// <summary>
/// Git через git.exe — с настройками и учётными данными разработчика. Конфликты, ветки и прочее — вне приложения.
/// </summary>
public sealed partial class GitRepository
{
    private GitRepository(string root) => Root = root;

    /// <summary>Корень рабочей копии (полный путь).</summary>
    public string Root { get; }

    /// <summary>Репозиторий, в котором лежит папка; null — папка не в git или git не установлен.</summary>
    public static async Task<GitRepository?> OpenAsync(string folder, CancellationToken ct = default)
    {
        try
        {
            var top = (await RunAsync(folder, ct, "rev-parse", "--show-toplevel")).Trim();
            return top.Length > 0 ? new GitRepository(Path.GetFullPath(top)) : null;
        }
        catch (GitException)
        {
            return null;
        }
    }

    public async Task<GitStatus> StatusAsync(CancellationToken ct = default)
    {
        var output = await RunAsync(Root, ct, "status", "--porcelain=v1", "-z", "--branch", "--untracked-files=all");
        return ParseStatus(Root, output);
    }

    public async Task<bool> HasRemoteAsync(CancellationToken ct = default) =>
        (await RunAsync(Root, ct, "remote")).Trim().Length > 0;

    public Task FetchAsync(CancellationToken ct = default) => RunAsync(Root, ct, "fetch", "--quiet");

    /// <summary>Только fast-forward: при расхождении с сервером git откажет, разбираться — вне приложения.</summary>
    public Task<string> PullAsync(CancellationToken ct = default) => RunAsync(Root, ct, "pull", "--ff-only");

    /// <summary>Содержимое файла в последнем коммите; null — файла там нет (или коммитов ещё нет).</summary>
    public async Task<string?> ReadCommittedAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await RunAsync(Root, ct, ["show", "HEAD:" + Relative(path)], withStderr: false);
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>
    /// Коммитит указанные файлы (как в рабочей папке) и файлы <paramref name="partial"/> с заданным содержимым
    /// (часть изменений файла), остальные изменения не трогает; затем отправляет текущую ветку:
    /// без upstream — <c>push -u origin HEAD</c>, без remote — только коммит. Возвращает вывод git.
    /// </summary>
    /// <remarks>
    /// Коммит собирается во временном индексе (от HEAD), чтобы не задеть то, что уже подготовлено в основном;
    /// после коммита записи основного индекса для этих файлов обновляются до нового HEAD.
    /// </remarks>
    public async Task<string> CommitAndPushAsync(IReadOnlyCollection<string> paths, string message,
        IReadOnlyDictionary<string, string>? partial = null, CancellationToken ct = default)
    {
        partial ??= new Dictionary<string, string>();
        if (paths.Count == 0 && partial.Count == 0)
            throw new GitException("Не выбраны файлы для публикации");

        var relative = paths.Select(Relative).ToList();
        var log = new StringBuilder();
        var index = Path.Combine(Path.GetTempPath(), $"dsm-index-{Guid.NewGuid():N}");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            if (await HasHeadAsync(ct))
                await RunAsync(Root, ct, ["read-tree", "HEAD"], env);
            if (relative.Count > 0)
                await RunAsync(Root, ct, ["add", "-A", "--", .. relative], env);
            foreach (var (path, content) in partial)
            {
                var blob = (await RunAsync(Root, ct, ["hash-object", "-w", "--stdin"], stdin: content)).Trim();
                await RunAsync(Root, ct, ["update-index", "--add", "--cacheinfo", $"100644,{blob},{Relative(path)}"], env);
            }
            log.Append(await RunAsync(Root, ct, ["commit", "-m", message], env));
        }
        finally
        {
            File.Delete(index);
        }

        await RunAsync(Root, ct, ["reset", "-q", "--", .. relative.Concat(partial.Keys.Select(Relative))]);

        if (!await HasRemoteAsync(ct))
            return log.Append("Удалённого репозитория нет — изменения только закоммичены.").ToString();

        var status = await StatusAsync(ct);
        log.Append(status.Upstream is null
            ? await RunAsync(Root, ct, "push", "-u", "origin", "HEAD")
            : await RunAsync(Root, ct, "push"));
        return log.ToString();
    }

    /// <summary>Разбор <c>git status --porcelain=v1 -z --branch</c>.</summary>
    public static GitStatus ParseStatus(string root, string output)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var files = new Dictionary<string, GitFileState>(StringComparer.OrdinalIgnoreCase);

        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = parts[i];
            if (entry.Length < 3)
                continue;

            if (entry.StartsWith("## ", StringComparison.Ordinal))
            {
                var m = BranchRegex().Match(entry[3..]);
                branch = m.Groups["branch"].Value;
                upstream = m.Groups["upstream"].Success ? m.Groups["upstream"].Value : null;
                ahead = m.Groups["ahead"].Success ? int.Parse(m.Groups["ahead"].Value) : 0;
                behind = m.Groups["behind"].Success ? int.Parse(m.Groups["behind"].Value) : 0;
                if (branch.StartsWith("No commits yet on ", StringComparison.Ordinal))
                    branch = branch["No commits yet on ".Length..];
                continue;
            }

            var (x, y, path) = (entry[0], entry[1], entry[3..]);
            // Вывод скриптов (логи) — не изменения скриптов, публиковать нечего.
            if (x == '?' && ("/" + path).Contains("/" + ScriptLayout.OutputFolder + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            var state = (x, y) switch
            {
                ('?', '?') => GitFileState.Untracked,
                ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => GitFileState.Conflict,
                ('R', _) => GitFileState.Renamed,
                ('A', _) => GitFileState.Added,
                ('D', _) or (_, 'D') => GitFileState.Deleted,
                _ => GitFileState.Modified,
            };
            files[Path.GetFullPath(Path.Combine(root, path))] = state;

            // У переименования следом идёт старый путь: он тоже изменение (удалён), иначе при публикации
            // только нового пути старый файл остался бы в коммите.
            if (x is 'R' or 'C')
            {
                i++;
                if (x == 'R' && i < parts.Length && parts[i].Length > 0)
                    files.TryAdd(Path.GetFullPath(Path.Combine(root, parts[i])), GitFileState.Deleted);
            }
        }

        return new GitStatus(branch, upstream, ahead, behind, files);
    }

    private async Task<bool> HasHeadAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync(Root, ct, "rev-parse", "--verify", "--quiet", "HEAD");
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    private string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static Task<string> RunAsync(string dir, CancellationToken ct, params string[] args) => RunAsync(dir, ct, (IEnumerable<string>)args);

    /// <param name="env">Дополнительные переменные окружения (например, GIT_INDEX_FILE).</param>
    /// <param name="stdin">Текст на вход команды (UTF-8 без BOM).</param>
    /// <param name="withStderr">Добавлять ли stderr к результату.</param>
    private static async Task<string> RunAsync(string dir, CancellationToken ct, IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null, string? stdin = null, bool withStderr = true)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = stdin is null ? null : new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Пути с кириллицей — как есть, без \320\224…; сообщения git — по-английски не меняем.
        foreach (var a in new[] { "-c", "core.quotepath=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        // Запрос пароля в консоли повис бы: окна диспетчера учётных данных при этом работают.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (name, value) in env ?? new Dictionary<string, string>())
            psi.Environment[name] = value;

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new GitException("git не найден: установите Git for Windows");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
                process.StandardInput.Close();
            }
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
            {
                var error = (await stderr).Trim();
                throw new GitException(error.Length > 0 ? error : $"git {string.Join(' ', args)}: код {process.ExitCode}");
            }

            return await stdout + (withStderr ? await stderr : "");
        }
    }

    [GeneratedRegex(@"^(?<branch>.+?)(?:\.\.\.(?<upstream>\S+))?(?: \[(?:ahead (?<ahead>\d+))?(?:, )?(?:behind (?<behind>\d+))?(?:gone)?\])?$")]
    private static partial Regex BranchRegex();
}
