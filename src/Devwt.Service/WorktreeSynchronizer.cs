using Devwt.Core;

namespace Devwt.Service;

public sealed class DevwtWorktreeSynchronizer(
    DevwtStateStore store,
    DevwtManager manager,
    IGitInspector gitInspector,
    IWorktreeMaterializer materializer)
{
    public int SyncOnce()
    {
        var repositories = store.LoadRepositories();
        var knownWorktrees = store.LoadContexts().Contexts
            .Select(context => DevwtPath.Normalize(context.WorktreeRootPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = 0;

        foreach (var repository in repositories.Repositories)
        {
            GitRepositoryInfo git;
            try
            {
                git = gitInspector.InspectRepository(repository.RootPath);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var worktree in git.Worktrees)
            {
                var root = DevwtPath.Normalize(worktree.RootPath);
                if (knownWorktrees.Contains(root))
                {
                    continue;
                }

                manager.WorktreeReady(repository.Id, root);
                materializer.RepairMissingTrackedFiles(root);
                knownWorktrees.Add(root);
                registered++;
            }
        }

        return registered;
    }
}

public sealed class DevwtHookRuntimeReconciler(
    DevwtStateStore store,
    IHookRuntimeConfigurator hookRuntime)
{
    public int ReconcileOnce()
    {
        var repositories = store.LoadRepositories();
        var repositoryById = repositories.Repositories
            .ToDictionary(repository => repository.Id, StringComparer.OrdinalIgnoreCase);
        var configured = 0;

        foreach (var context in store.LoadContexts().Contexts)
        {
            if (context.Status != DevwtContextStatus.Active
                || !repositoryById.TryGetValue(context.RepositoryId, out var repository))
            {
                continue;
            }

            hookRuntime.Configure(repository, context);
            configured++;
        }

        return configured;
    }
}

public interface IWorktreeMaterializer
{
    bool RepairMissingTrackedFiles(string worktreePath);
}

public sealed class GitWorktreeMaterializer(ICommandRunner commandRunner) : IWorktreeMaterializer
{
    public bool RepairMissingTrackedFiles(string worktreePath)
    {
        var status = commandRunner.Run([
            "git",
            "-C",
            worktreePath,
            "status",
            "--porcelain",
            "--untracked-files=no"
        ]);
        if (status.ExitCode != 0)
        {
            return false;
        }

        var lines = status.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !lines.All(IsTrackedDeletion))
        {
            return false;
        }

        var reset = commandRunner.Run(["git", "-C", worktreePath, "reset", "--hard"]);
        if (reset.ExitCode != 0)
        {
            throw new IOException(string.Concat(reset.Output, reset.Error));
        }

        return true;
    }

    private static bool IsTrackedDeletion(string line) =>
        line.Length >= 2 && (line[0] == 'D' || line[1] == 'D') && !line.StartsWith("??", StringComparison.Ordinal);
}
