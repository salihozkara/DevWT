namespace Devwt.Core;

public static class DevwtPath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path.Trim())
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static bool IsUnderRoot(string candidatePath, string rootPath)
    {
        var candidate = Normalize(candidatePath);
        var root = Normalize(rootPath);

        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
