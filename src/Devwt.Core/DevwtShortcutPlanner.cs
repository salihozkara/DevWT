namespace Devwt.Core;

public sealed record DevwtShortcut(
    string Path,
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string IconLocation);

public sealed record DevwtShortcutWrapPlan(
    string ShortcutPath,
    string BackupPath,
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string IconLocation,
    bool AlreadyWrapped);

public static class DevwtShortcutPlanner
{
    public static DevwtShortcutWrapPlan CreateWrapPlan(
        DevwtShortcut shortcut,
        string devwtExecutablePath,
        string? worktreePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcut.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcut.TargetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(devwtExecutablePath);

        var alreadyWrapped = IsDevwtShortcut(shortcut, devwtExecutablePath);
        var arguments = alreadyWrapped
            ? shortcut.Arguments
            : BuildWrapperArguments(shortcut.TargetPath, shortcut.Arguments, worktreePath);

        return new DevwtShortcutWrapPlan(
            ShortcutPath: shortcut.Path,
            BackupPath: BuildBackupPath(shortcut.Path),
            TargetPath: alreadyWrapped ? shortcut.TargetPath : devwtExecutablePath,
            Arguments: arguments,
            WorkingDirectory: shortcut.WorkingDirectory,
            IconLocation: ResolveIconLocation(shortcut),
            AlreadyWrapped: alreadyWrapped);
    }

    public static string BuildBackupPath(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        var directory = Path.GetDirectoryName(shortcutPath) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(shortcutPath);
        return Path.Combine(directory, $"{fileName}.devwt.bak.lnk");
    }

    private static string BuildWrapperArguments(string targetPath, string originalArguments, string? worktreePath)
    {
        var parts = new List<string>
        {
            "run",
            "--children-only"
        };

        if (!string.IsNullOrWhiteSpace(worktreePath))
        {
            parts.Add("--worktree");
            parts.Add(QuoteArgument(worktreePath, force: true));
        }

        parts.Add("--");
        parts.Add(QuoteArgument(targetPath, force: true));

        if (!string.IsNullOrWhiteSpace(originalArguments))
        {
            parts.Add(originalArguments.Trim());
        }

        return string.Join(" ", parts);
    }

    private static bool IsDevwtShortcut(DevwtShortcut shortcut, string devwtExecutablePath) =>
        string.Equals(
            Path.GetFullPath(shortcut.TargetPath),
            Path.GetFullPath(devwtExecutablePath),
            StringComparison.OrdinalIgnoreCase)
        && shortcut.Arguments.Contains("run", StringComparison.OrdinalIgnoreCase)
        && shortcut.Arguments.Contains("--children-only", StringComparison.OrdinalIgnoreCase);

    private static string ResolveIconLocation(DevwtShortcut shortcut) =>
        string.IsNullOrWhiteSpace(shortcut.IconLocation)
            ? $"{shortcut.TargetPath},0"
            : shortcut.IconLocation;

    private static string QuoteArgument(string value, bool force = false)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!force && !value.Any(ch => char.IsWhiteSpace(ch) || ch == '"'))
        {
            return value;
        }

        var result = new System.Text.StringBuilder();
        result.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append(ch);
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(ch);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}
