namespace Devwt.Core;

public static class GitWorktreeParser
{
    public static IReadOnlyList<GitWorktreeInfo> ParsePorcelain(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var result = new List<GitWorktreeInfo>();
        string? root = null;
        string? reference = null;

        foreach (var rawLine in output.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                root = DevwtPath.Normalize(line["worktree ".Length..]);
                reference = null;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                reference = NormalizeRef(line["branch ".Length..]);
            }
            else if (line.Equals("detached", StringComparison.Ordinal))
            {
                reference = "detached";
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (root is null)
            {
                return;
            }

            result.Add(new GitWorktreeInfo(root, reference ?? "detached"));
            root = null;
            reference = null;
        }
    }

    private static string NormalizeRef(string value)
    {
        const string heads = "refs/heads/";
        return value.StartsWith(heads, StringComparison.Ordinal) ? value[heads.Length..] : value;
    }
}
