namespace Devwt.Core;

public sealed record ShellIntegrationResult(string ProfilePath, bool Modified);

public static class PowerShellShellIntegration
{
    private const string BeginMarker = "# DEVWT SHELL INTEGRATION BEGIN";
    private const string EndMarker = "# DEVWT SHELL INTEGRATION END";

    public static ShellIntegrationResult Install(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        var existing = File.Exists(profilePath) ? File.ReadAllText(profilePath) : "";
        var next = RemoveBlock(existing).TrimEnd();
        next = string.IsNullOrWhiteSpace(next)
            ? Block()
            : next + Environment.NewLine + Environment.NewLine + Block();
        if (string.Equals(existing, next, StringComparison.Ordinal))
        {
            return new ShellIntegrationResult(profilePath, Modified: false);
        }

        File.WriteAllText(profilePath, next);
        return new ShellIntegrationResult(profilePath, Modified: true);
    }

    public static ShellIntegrationResult Uninstall(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        if (!File.Exists(profilePath))
        {
            return new ShellIntegrationResult(profilePath, Modified: false);
        }

        var existing = File.ReadAllText(profilePath);
        var next = RemoveBlock(existing).TrimEnd() + Environment.NewLine;
        if (string.Equals(existing, next, StringComparison.Ordinal))
        {
            return new ShellIntegrationResult(profilePath, Modified: false);
        }

        File.WriteAllText(profilePath, next);
        return new ShellIntegrationResult(profilePath, Modified: true);
    }

    public static bool IsInstalled(string profilePath)
    {
        if (!File.Exists(profilePath))
        {
            return false;
        }

        var content = File.ReadAllText(profilePath);
        return content.Contains(BeginMarker, StringComparison.Ordinal)
            && content.Contains(EndMarker, StringComparison.Ordinal);
    }

    public static string DefaultWindowsPowerShellProfilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WindowsPowerShell",
            "Microsoft.PowerShell_profile.ps1");

    public static string DefaultPowerShellProfilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PowerShell",
            "Microsoft.PowerShell_profile.ps1");

    private static string Block()
    {
        return string.Join(
            Environment.NewLine,
            [
            BeginMarker,
            "# Hook this shell's future native children without command-name wrappers.",
            "if (Get-Command devwt -ErrorAction SilentlyContinue) {",
            "    try { $null = & devwt shell attach --pid $PID 2>$null } catch { }",
            "}",
            EndMarker,
            ""
            ]);
    }

    private static string RemoveBlock(string content)
    {
        var begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return content;
        }

        var end = content.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0)
        {
            return content;
        }

        end += EndMarker.Length;
        while (end < content.Length && (content[end] == '\r' || content[end] == '\n'))
        {
            end++;
        }

        return content.Remove(begin, end - begin);
    }
}
