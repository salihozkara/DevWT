using System.Security.Cryptography;
using System.Text;
using Devwt.Core;

namespace Devwt.Service;

public sealed class DevwtManager(
    DevwtStateStore store,
    IGitInspector gitInspector,
    IHookRuntimeConfigurator hookRuntime)
{
    public AddRepositoryResult AddRepository(AddRepositoryRequest request)
    {
        return DevwtStateLock.WithLock(() =>
        {
            var git = gitInspector.InspectRepository(request.WorkingDirectory);
            var repositories = store.LoadRepositories();
            var existingRepository = repositories.Repositories.FirstOrDefault(existing =>
                existing.GitCommonDir.Equals(git.GitCommonDir, StringComparison.OrdinalIgnoreCase)
                || existing.RootPath.Equals(git.RootPath, StringComparison.OrdinalIgnoreCase));
            var name = string.IsNullOrWhiteSpace(request.Name)
                ? existingRepository?.Name ?? Path.GetFileName(git.RootPath)
                : request.Name.Trim();
            var repositoryId = existingRepository?.Id ?? StableId("repo", name, git.GitCommonDir);
            var rootPath = existingRepository?.RootPath ?? git.RootPath;
            var linked = request.LinkedRepositories
                .Select(input => new LinkedRepository(
                    input.Name.Trim(),
                    input.Path.Trim(),
                    DevwtPath.Normalize(Path.Combine(rootPath, input.Path.Trim()))))
                .ToArray();
            var repository = new DevwtRepository(
                repositoryId,
                name,
                rootPath,
                git.GitCommonDir,
                linked.Length == 0 && existingRepository is not null
                    ? existingRepository.LinkedRepositories
                    : linked);

            var nextRepositories = repositories.Repositories
                .Where(existing => !existing.Id.Equals(repositoryId, StringComparison.OrdinalIgnoreCase)
                    && !existing.GitCommonDir.Equals(git.GitCommonDir, StringComparison.OrdinalIgnoreCase))
                .Append(repository)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            store.SaveRepositories(new DevwtRepositoryState(nextRepositories));

            var contexts = store.LoadContexts();
            var existingContextsByRoot = contexts.Contexts
                .GroupBy(context => DevwtPath.Normalize(context.WorktreeRootPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var repoContexts = git.Worktrees
                .Select((worktree, index) =>
                {
                    var context = CreateContext(repository, worktree, index);
                    return existingContextsByRoot.TryGetValue(DevwtPath.Normalize(worktree.RootPath), out var existing)
                        ? context with { Description = existing.Description }
                        : context;
                })
                .ToArray();
            var repoWorktreeRoots = repoContexts
                .Select(context => DevwtPath.Normalize(context.WorktreeRootPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var otherContexts = contexts.Contexts
                .Where(existing => !existing.RepositoryId.Equals(repositoryId, StringComparison.OrdinalIgnoreCase)
                    && !repoWorktreeRoots.Contains(DevwtPath.Normalize(existing.WorktreeRootPath)))
                .ToList();
            store.SaveContexts(new DevwtContextState(
                otherContexts
                    .Concat(repoContexts)
                    .OrderBy(context => context.RepositoryId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(context => context.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()));

            var hookPath = Path.Combine(gitInspector.EnsureHooksDirectory(request.WorkingDirectory, git), "post-checkout");
            PostCheckoutHookInstaller.Install(hookPath, "devwt", repositoryId);
            foreach (var context in repoContexts)
            {
                hookRuntime.Configure(repository, context);
            }

            return new AddRepositoryResult(repository, repoContexts);
        });
    }

    public DevwtContext WorktreeReady(string repositoryId, string worktreePath)
    {
        return DevwtStateLock.WithLock(() =>
        {
            var repositories = store.LoadRepositories();
            var repository = repositories.Repositories.First(repo =>
                repo.Id.Equals(repositoryId, StringComparison.OrdinalIgnoreCase));
            var git = gitInspector.InspectRepository(worktreePath);
            var worktree = git.Worktrees.FirstOrDefault(item =>
                DevwtPath.Normalize(item.RootPath).Equals(DevwtPath.Normalize(worktreePath), StringComparison.OrdinalIgnoreCase))
                ?? new GitWorktreeInfo(DevwtPath.Normalize(worktreePath), "detached");
            var contexts = store.LoadContexts();
            var existingEntry = contexts.Contexts
                .Select((context, index) => new { context, index })
                .FirstOrDefault(item => item.context.WorktreeRootPath.Equals(worktree.RootPath, StringComparison.OrdinalIgnoreCase));
            var context = CreateContext(repository, worktree, contexts.Contexts.Count) with
            {
                Description = existingEntry?.context.Description
            };
            var next = contexts.Contexts.ToList();
            if (existingEntry is not null)
            {
                next[existingEntry.index] = context;
            }
            else
            {
                next.Add(context);
            }

            store.SaveContexts(new DevwtContextState(next));
            hookRuntime.Configure(repository, context);
            return context;
        });
    }

    public DevwtContext SetDescription(string worktreePath, string? description)
    {
        var normalizedPath = DevwtPath.Normalize(worktreePath);
        var normalizedDescription = NormalizeDescription(description);
        return DevwtStateLock.WithLock(() =>
        {
            var contexts = store.LoadContexts();
            var existingEntry = contexts.Contexts
                .Select((context, index) => new { context, index })
                .Where(item => DevwtPath.IsUnderRoot(normalizedPath, item.context.WorktreeRootPath))
                .OrderByDescending(item => item.context.WorktreeRootPath.Length)
                .FirstOrDefault();

            DevwtContext context;
            var next = contexts.Contexts.ToList();
            if (existingEntry is not null)
            {
                context = existingEntry.context with { Description = normalizedDescription };
                next[existingEntry.index] = context;
            }
            else
            {
                var git = gitInspector.InspectRepository(normalizedPath);
                var repository = store.LoadRepositories().Repositories.FirstOrDefault(item =>
                    item.GitCommonDir.Equals(git.GitCommonDir, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"The worktree at {normalizedPath} belongs to a repository that is not registered in DevWT. Run `devwt add` first.");
                var worktree = git.Worktrees
                    .Where(item => DevwtPath.IsUnderRoot(normalizedPath, item.RootPath))
                    .OrderByDescending(item => item.RootPath.Length)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException($"Could not resolve a Git worktree for {normalizedPath}.");
                context = CreateContext(repository, worktree, contexts.Contexts.Count) with
                {
                    Description = normalizedDescription
                };
                next.Add(context);
                hookRuntime.Configure(repository, context);
            }

            store.SaveContexts(new DevwtContextState(next));
            return context;
        });
    }

    public RemoveRepositoryResult RemoveRepository(string? repositoryName) =>
        RemoveRepository(repositoryName, worktreePath: null);

    public RemoveRepositoryResult RemoveRepository(string? repositoryName, string? worktreePath)
    {
        return DevwtStateLock.WithLock(() =>
        {
            var repositories = store.LoadRepositories();
            var targets = ResolveRemoveTargets(repositories, repositoryName, worktreePath);
            var targetIds = targets.Select(repo => repo.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var contexts = store.LoadContexts();
            var removedContexts = contexts.Contexts
                .Where(context => targetIds.Contains(context.RepositoryId))
                .ToArray();
            var warnings = new List<string>();
            foreach (var context in removedContexts)
            {
                try
                {
                    hookRuntime.Remove(context);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    warnings.Add($"Hook runtime cleanup warning for {context.Name}: {ex.Message}");
                }
            }

            store.SaveRepositories(new DevwtRepositoryState(
                repositories.Repositories.Where(repo => !targetIds.Contains(repo.Id)).ToArray()));
            store.SaveContexts(new DevwtContextState(
                contexts.Contexts.Where(context => !targetIds.Contains(context.RepositoryId)).ToArray()));
            return new RemoveRepositoryResult(targets.Length, removedContexts.Length, warnings);
        });
    }

    private DevwtRepository[] ResolveRemoveTargets(
        DevwtRepositoryState repositories,
        string? repositoryName,
        string? worktreePath)
    {
        if (!string.IsNullOrWhiteSpace(repositoryName))
        {
            return repositories.Repositories
                .Where(repo => repo.Name.Equals(repositoryName, StringComparison.OrdinalIgnoreCase)
                    || repo.Id.Equals(repositoryName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (string.IsNullOrWhiteSpace(worktreePath))
        {
            throw new ArgumentException("remove requires --repo <name> or a current git repository directory.");
        }

        var git = gitInspector.InspectRepository(worktreePath);
        var target = repositories.Repositories.FirstOrDefault(repo =>
            repo.GitCommonDir.Equals(git.GitCommonDir, StringComparison.OrdinalIgnoreCase)
            || repo.RootPath.Equals(git.RootPath, StringComparison.OrdinalIgnoreCase));
        return target is null ? [] : [target];
    }

    public void SetPaused(string? repositoryName, string? worktreePath, bool paused)
    {
        DevwtStateLock.WithLock<object?>(() =>
        {
            var repositories = store.LoadRepositories();
            var repoIds = string.IsNullOrWhiteSpace(repositoryName)
                ? repositories.Repositories.Select(repo => repo.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : repositories.Repositories
                    .Where(repo => repo.Name.Equals(repositoryName, StringComparison.OrdinalIgnoreCase)
                        || repo.Id.Equals(repositoryName, StringComparison.OrdinalIgnoreCase))
                    .Select(repo => repo.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalizedWorktree = string.IsNullOrWhiteSpace(worktreePath) ? null : DevwtPath.Normalize(worktreePath);
            var contexts = store.LoadContexts().Contexts
                .Select(context =>
                {
                    var matchesRepo = repoIds.Contains(context.RepositoryId);
                    var matchesWorktree = normalizedWorktree is null
                        || context.WorktreeRootPath.Equals(normalizedWorktree, StringComparison.OrdinalIgnoreCase);
                    return matchesRepo && matchesWorktree
                        ? context with { Status = paused ? DevwtContextStatus.Paused : DevwtContextStatus.Active }
                        : context;
                })
                .ToArray();
            store.SaveContexts(new DevwtContextState(contexts));
            return null;
        });
    }

    private static DevwtContext CreateContext(DevwtRepository repository, GitWorktreeInfo worktree, int index)
    {
        var name = Path.GetFileName(worktree.RootPath);
        var contextId = StableId("ctx", repository.Name, worktree.RootPath);
        return new DevwtContext(
            contextId,
            repository.Id,
            name,
            worktree.RootPath,
            worktree.RefName,
            DevwtPortShift.LoopbackAddress,
            RuntimeNameFor(contextId),
            DevwtContextStatus.Active,
            DevwtPortShift.AssignedPortBaseFor(contextId));
    }

    private static string? NormalizeDescription(string? description)
    {
        if (description is null)
        {
            return null;
        }

        var normalized = description.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Context description cannot be empty. Use --clear to remove it.");
        }
        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Context description must be a single line without control characters.");
        }

        return normalized;
    }

    private static string StableId(string prefix, string name, string value)
    {
        var cleanName = new string(name
            .Where(char.IsLetterOrDigit)
            .Take(20)
            .ToArray());
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            cleanName = prefix;
        }

        return $"{prefix}-{cleanName.ToLowerInvariant()}-{Hash(value, 10)}";
    }

    private static string RuntimeNameFor(string contextId) =>
        "DevWT-" + Hash(contextId, 24);

    private static string Hash(string value, int length)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..length];
    }
}
