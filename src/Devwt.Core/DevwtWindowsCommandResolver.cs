namespace Devwt.Core;

public sealed record DevwtResolvedCommand(string Program, IReadOnlyList<string> Arguments);

public static class DevwtWindowsCommandResolver
{
    private static readonly HashSet<string> DirectExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".com",
        ".exe"
    };

    private static readonly HashSet<string> BatchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat",
        ".cmd"
    };

    public static DevwtResolvedCommand Resolve(
        string program,
        IReadOnlyList<string> arguments,
        string? commandPath = null,
        string? commandPathExt = null,
        string? comSpec = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        if (!OperatingSystem.IsWindows())
        {
            return new DevwtResolvedCommand(program, arguments);
        }

        var resolved = ResolveProgramPath(program, commandPath, commandPathExt);
        if (resolved is null)
        {
            return new DevwtResolvedCommand(program, arguments);
        }

        var extension = Path.GetExtension(resolved);
        if (DirectExecutableExtensions.Contains(extension))
        {
            return new DevwtResolvedCommand(resolved, arguments);
        }

        if (BatchExtensions.Contains(extension))
        {
            var shell = !string.IsNullOrWhiteSpace(comSpec)
                ? comSpec
                : Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            return new DevwtResolvedCommand(
                shell,
                ["/d", "/s", "/c", "call", resolved, .. arguments]);
        }

        return new DevwtResolvedCommand(program, arguments);
    }

    private static string? ResolveProgramPath(string program, string? commandPath, string? commandPathExt)
    {
        var hasDirectory = program.Contains(Path.DirectorySeparatorChar)
            || program.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(program);
        if (hasDirectory)
        {
            return ResolveInDirectory(Path.GetDirectoryName(program), Path.GetFileName(program), commandPathExt);
        }

        foreach (var directory in SplitPath(commandPath ?? Environment.GetEnvironmentVariable("PATH")))
        {
            if (ResolveInDirectory(directory, program, commandPathExt) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static string? ResolveInDirectory(string? directory, string fileName, string? commandPathExt)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            var exact = Path.Combine(directory, fileName);
            return File.Exists(exact) && IsSupportedExtension(extension) ? exact : null;
        }

        foreach (var pathExt in SupportedPathExtensions(commandPathExt))
        {
            var candidate = Path.Combine(directory, fileName + pathExt);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitPath(string? value) =>
        (value ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> SupportedPathExtensions(string? commandPathExt)
    {
        var pathExt = string.IsNullOrWhiteSpace(commandPathExt)
            ? ".COM;.EXE;.BAT;.CMD"
            : commandPathExt;
        foreach (var item in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = item.StartsWith(".", StringComparison.Ordinal) ? item : "." + item;
            if (IsSupportedExtension(extension))
            {
                yield return extension;
            }
        }
    }

    private static bool IsSupportedExtension(string extension) =>
        DirectExecutableExtensions.Contains(extension) || BatchExtensions.Contains(extension);
}
