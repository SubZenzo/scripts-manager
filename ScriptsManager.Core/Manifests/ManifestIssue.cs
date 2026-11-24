namespace ScriptsManager.Core.Manifests;

public enum IssueSeverity { Warning, Error }

public sealed record ManifestIssue(IssueSeverity Severity, string Message, int? Line = null)
{
    public override string ToString() =>
        (Line is { } line ? $"строка {line}: " : "") + Message;
}

public sealed record ManifestLoadResult(ScriptManifest? Manifest, IReadOnlyList<ManifestIssue> Issues)
{
    /// <summary>Скрипт с ошибками в манифесте не запускается.</summary>
    public bool HasErrors => Manifest is null || Issues.Any(i => i.Severity == IssueSeverity.Error);
}
