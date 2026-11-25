namespace ScriptsManager.Core.Runs;

public static class Executables
{
    /// <summary>
    /// Полный путь к программе, заданной именем (<c>python</c>, <c>pwsh</c>). PATH читается из реестра (системный и
    /// пользовательский), а не из окружения процесса: окружение наследуется при запуске и не видит, что PATH
    /// поменяли позже. Папка WindowsApps пропускается — там заглушка python.exe, которая открывает Microsoft Store.
    /// Не нашли — имя как есть (поищет сама система).
    /// </summary>
    public static string Find(string name)
    {
        if (name.IndexOfAny(['\\', '/']) >= 0)
            return name;

        var file = Path.HasExtension(name) ? name : name + ".exe";
        var path = string.Join(';',
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"));

        foreach (var entry in path.Split(';'))
        {
            var dir = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (dir.Length == 0 || dir.Contains(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var candidate = Path.Combine(dir, file);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Недопустимые символы в элементе PATH.
            }
        }

        return name;
    }
}
