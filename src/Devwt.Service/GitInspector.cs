using Devwt.Core;

namespace Devwt.Service;

public sealed class GitInspector(ICommandRunner commandRunner) : IGitInspector
{
    public GitRepositoryInfo InspectRepository(string workingDirectory)
    {
        var cwd = DevwtPath.Normalize(workingDirectory);
        var root = RunGit(cwd, ["rev-parse", "--show-toplevel"]).Trim();
        var commonDir = RunGit(cwd, ["rev-parse", "--git-common-dir"]).Trim();
        var normalizedRoot = DevwtPath.Normalize(root);
        var normalizedCommon = Path.IsPathRooted(commonDir)
            ? DevwtPath.Normalize(commonDir)
            : DevwtPath.Normalize(Path.Combine(normalizedRoot, commonDir));
        var worktrees = GitWorktreeParser.ParsePorcelain(RunGit(cwd, ["worktree", "list", "--porcelain"]));

        return new GitRepositoryInfo(normalizedRoot, normalizedCommon, worktrees.Count == 0
            ? [new GitWorktreeInfo(normalizedRoot, CurrentBranchName(cwd))]
            : worktrees);
    }

    public string EnsureHooksDirectory(string workingDirectory, GitRepositoryInfo repository)
    {
        var cwd = DevwtPath.Normalize(workingDirectory);
        var existing = RunGitOptional(cwd, ["config", "--get", "core.hooksPath"]).Trim();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return DevwtPath.Normalize(Path.IsPathRooted(existing)
                ? existing
                : Path.Combine(repository.RootPath, existing));
        }

        var hooksDirectory = DevwtPath.Normalize(Path.Combine(repository.GitCommonDir, "hooks"));
        RunGit(cwd, ["config", "core.hooksPath", hooksDirectory]);
        return hooksDirectory;
    }

    private string CurrentBranchName(string cwd)
    {
        var branch = RunGit(cwd, ["branch", "--show-current"]).Trim();
        return string.IsNullOrWhiteSpace(branch) ? "detached" : branch;
    }

    private string RunGit(string cwd, IReadOnlyList<string> args)
    {
        var result = commandRunner.Run(["git", "-c", "safe.directory=*", "-C", cwd, .. args]);
        if (result.ExitCode != 0)
        {
            throw new IOException(string.Concat(result.Output, result.Error));
        }

        return result.Output;
    }

    private string RunGitOptional(string cwd, IReadOnlyList<string> args)
    {
        var result = commandRunner.Run(["git", "-c", "safe.directory=*", "-C", cwd, .. args]);
        return result.ExitCode == 0 ? result.Output : string.Empty;
    }
}
