namespace Devwt.Core;

public sealed record HookInstallResult(
    string HookPath,
    bool Created,
    bool Modified,
    bool AlreadyInstalled);

public static class PostCheckoutHookInstaller
{
    private const string BeginMarker = "# DEVWT BEGIN";
    private const string EndMarker = "# DEVWT END";

    public static HookInstallResult Install(string hookPath, string toolCommand)
    {
        return Install(hookPath, toolCommand, repositoryId: null);
    }

    public static HookInstallResult Install(string hookPath, string toolCommand, string? repositoryId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        var block = BuildBlock(toolCommand, repositoryId);

        if (!File.Exists(hookPath))
        {
            File.WriteAllText(hookPath, "#!/bin/sh\n\n" + block);
            return new HookInstallResult(hookPath, Created: true, Modified: true, AlreadyInstalled: false);
        }

        var existing = File.ReadAllText(hookPath);
        var begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin >= 0)
        {
            var end = existing.IndexOf(EndMarker, begin, StringComparison.Ordinal);
            if (end >= 0)
            {
                end += EndMarker.Length;
                if (end < existing.Length && existing[end] == '\r')
                {
                    end++;
                }

                if (end < existing.Length && existing[end] == '\n')
                {
                    end++;
                }

                var updated = existing[..begin] + block + existing[end..];
                if (string.Equals(existing, updated, StringComparison.Ordinal))
                {
                    return new HookInstallResult(hookPath, Created: false, Modified: false, AlreadyInstalled: true);
                }

                File.WriteAllText(hookPath, updated);
                return new HookInstallResult(hookPath, Created: false, Modified: true, AlreadyInstalled: false);
            }
        }

        var separator = existing.EndsWith('\n') ? "\n" : "\n\n";
        File.WriteAllText(hookPath, existing + separator + block);
        return new HookInstallResult(hookPath, Created: false, Modified: true, AlreadyInstalled: false);
    }

    public static string BuildBlock(string toolCommand)
    {
        return BuildBlock(toolCommand, repositoryId: null);
    }

    public static string BuildBlock(string toolCommand, string? repositoryId)
    {
        if (string.IsNullOrWhiteSpace(toolCommand))
        {
            throw new ArgumentException("Hook command cannot be empty.", nameof(toolCommand));
        }

        return string.Join(
            "\n",
            [
                BeginMarker,
                "if [ \"${3:-}\" = \"1\" ]; then",
                string.IsNullOrWhiteSpace(repositoryId)
                    ? $"  {toolCommand} add \"$PWD\" >/dev/null 2>&1 || true"
                    : $"  {toolCommand} hook worktree-ready --repo-id \"{EscapeShellDoubleQuoted(repositoryId)}\" --path \"$PWD\" >/dev/null 2>&1 || true",
                "fi",
                EndMarker,
                ""
            ]);
    }

    private static string EscapeShellDoubleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
