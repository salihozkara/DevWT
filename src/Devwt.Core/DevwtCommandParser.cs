namespace Devwt.Core;

public abstract record DevwtCommand;

public sealed record AddRepositoryCommand(
    string WorkingDirectory,
    string? Name,
    IReadOnlyList<LinkedRepositoryInput> LinkedRepositories) : DevwtCommand;

public sealed record RemoveRepositoryCommand(string? RepositoryName, string WorkingDirectory) : DevwtCommand;

public sealed record PauseCommand(string? RepositoryName, string? WorktreePath) : DevwtCommand;

public sealed record ResumeCommand(string? RepositoryName, string? WorktreePath) : DevwtCommand;

public sealed record DescribeContextCommand(
    string WorktreePath,
    string? Description,
    bool Clear) : DevwtCommand;

public enum PortCommandAction
{
    Process,
    Check
}

public sealed record PortCommand(
    PortCommandAction Action,
    int Port,
    string? ContextId,
    string WorkingDirectory) : DevwtCommand;

public sealed record StatusCommand : DevwtCommand;

public sealed record UiCommand : DevwtCommand;

public sealed record RunCommand(
    string WorkingDirectory,
    string Program,
    IReadOnlyList<string> Arguments,
    bool ChildrenOnly = false) : DevwtCommand;

public sealed record ExecCommand(
    string WorkingDirectory,
    string Program,
    IReadOnlyList<string> Arguments,
    bool ChildrenOnly = false) : DevwtCommand;

public sealed record TerminalCommand(string WorkingDirectory, string Shell) : DevwtCommand;

public enum ShortcutAction
{
    List,
    Wrap,
    Restore
}

public sealed record ShortcutCommand(
    ShortcutAction Action,
    string? ShortcutPath,
    bool Taskbar,
    string? Name,
    bool All,
    string? WorktreePath,
    bool DryRun) : DevwtCommand;

public enum IdeWatchAction
{
    List,
    Add,
    Remove
}

public sealed record IdeWatchCommand(
    IdeWatchAction Action,
    string? Name,
    string? ImagePath,
    string? AppId,
    string? PackageFamilyName,
    bool All) : DevwtCommand;

public sealed record WorktreeReadyHookCommand(string RepositoryId, string WorktreePath) : DevwtCommand;

public sealed record LinkMapCommand(string LinkedRepositoryName, string SourceWorktreePath, string TargetWorktreePath) : DevwtCommand;

public sealed record ProxyTargetCommand(string ContextId, int Port, string Scheme) : DevwtCommand;

public sealed record ProxyContextTargetCommand(string ContextId) : DevwtCommand;

public sealed record ProxyClearCommand(int? Port = null) : DevwtCommand;

public sealed record ProxyProcessTargetCommand(int ProcessId, string ContextId) : DevwtCommand;

public sealed record ProxyProcessClearCommand(int ProcessId) : DevwtCommand;

public enum ProxyChildAction
{
    Stop,
    Kill
}

public sealed record ProxyChildCommand(
    ProxyChildAction Action,
    string? ContextId,
    int Port,
    string Protocol) : DevwtCommand;

public sealed record HelpCommand(string Message, int ExitCode = 0) : DevwtCommand;

public static class DevwtCommandParser
{
    public const string HelpText = """
        devwt commands:
          add [--name <repo>] [--linked-repo <name> --linked-repo-path <path>]...
          remove [--repo <name>]   # defaults to the current git repo
          pause [--repo <name>|--worktree <path>]
          resume [--repo <name>|--worktree <path>]
          context describe [--worktree <path>] <description>
          context describe [--worktree <path>] --clear
          port process --port <port> [--context <context-id>]
          port check --port <port> [--context <context-id>]
          link map --linked-repo <name> --source <worktree-path> --target <worktree-path>
          proxy target --context <context-id> --port <port>
          proxy context --context <context-id>
          proxy process target --pid <pid> --context <context-id>
          proxy process clear --pid <pid>
          proxy child stop --port <port> [--context <context-id>] [--protocol tcp|udp]
          proxy child kill --port <port> [--context <context-id>] [--protocol tcp|udp]
          proxy clear [--port <port>]
          run [--children-only] [--worktree <path>] -- <program> [args...]
          exec [--children-only] [--worktree <path>] -- <program> [args...] # pass through outside DevWT contexts
          terminal [--worktree <path>] [--shell powershell|cmd]
          shortcut list --taskbar
          shortcut restore (--path <shortcut.lnk>|--taskbar --name <text>|--taskbar --all) [--dry-run]
          ide watch list
          ide watch add --name <name> (--path <ide.exe>|--app-id <appId>|--package-family <pfn>)
          ide watch remove (--name <name>|--path <ide.exe>|--app-id <appId>|--package-family <pfn>|--all)
          shell install|uninstall|status                            # generic PowerShell child injection
          status
          ui
          hook worktree-ready --repo-id <id> --path <path>
        """;

    public static DevwtCommand Parse(IReadOnlyList<string> args, string? currentDirectory = null)
    {
        var cwd = DevwtPath.Normalize(currentDirectory ?? Environment.CurrentDirectory);
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            return new HelpCommand(HelpText);
        }

        return args[0].ToLowerInvariant() switch
        {
            "add" => ParseAdd(args.Skip(1).ToArray(), cwd),
            "remove" => ParseRemove(args.Skip(1).ToArray(), cwd),
            "pause" => ParsePauseResume<PauseCommand>(args.Skip(1).ToArray(), cwd, (repo, worktree) => new PauseCommand(repo, worktree)),
            "resume" => ParsePauseResume<ResumeCommand>(args.Skip(1).ToArray(), cwd, (repo, worktree) => new ResumeCommand(repo, worktree)),
            "context" => ParseContext(args.Skip(1).ToArray(), cwd),
            "port" => ParsePort(args.Skip(1).ToArray(), cwd),
            "status" => new StatusCommand(),
            "ui" => new UiCommand(),
            "run" => ParseRun(args.Skip(1).ToArray(), cwd),
            "exec" => ParseExec(args.Skip(1).ToArray(), cwd),
            "terminal" => ParseTerminal(args.Skip(1).ToArray(), cwd),
            "shortcut" => ParseShortcut(args.Skip(1).ToArray(), cwd),
            "ide" => ParseIde(args.Skip(1).ToArray(), cwd),
            "hook" => ParseHook(args.Skip(1).ToArray(), cwd),
            "link" => ParseLink(args.Skip(1).ToArray(), cwd),
            "proxy" => ParseProxy(args.Skip(1).ToArray()),
            var unknown => new HelpCommand($"Unknown command: {unknown}{Environment.NewLine}{HelpText}", 2)
        };
    }

    private static DevwtCommand ParseContext(IReadOnlyList<string> args, string cwd)
    {
        if (args.Count == 0 || !args[0].Equals("describe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("context requires subcommand: describe");
        }

        var worktreePath = cwd;
        var clear = false;
        var descriptionParts = new List<string>();
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--worktree":
                    worktreePath = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                case "--clear":
                    clear = true;
                    break;
                default:
                    if (option.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown context describe option: {option}");
                    }

                    descriptionParts.Add(option);
                    break;
            }
        }

        var description = descriptionParts.Count == 0
            ? null
            : string.Join(' ', descriptionParts).Trim();
        if (clear && !string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("context describe --clear does not accept a description");
        }
        if (!clear && string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("context describe requires a description or --clear");
        }

        return new DescribeContextCommand(worktreePath, description, clear);
    }

    private static DevwtCommand ParsePort(IReadOnlyList<string> args, string cwd)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("port requires subcommand: process or check");
        }

        var action = args[0].ToLowerInvariant() switch
        {
            "process" => PortCommandAction.Process,
            "check" => PortCommandAction.Check,
            _ => throw new ArgumentException("port requires subcommand: process or check")
        };
        int? port = null;
        string? contextId = null;
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--port":
                    port = ParsePort(RequiredValue(args, ref index, option));
                    break;
                case "--context":
                    contextId = RequiredValue(args, ref index, option);
                    break;
                default:
                    throw new ArgumentException($"Unknown port option: {option}");
            }
        }

        if (port is null)
        {
            throw new ArgumentException("port requires --port <port>");
        }

        return new PortCommand(action, port.Value, contextId, cwd);
    }

    private static AddRepositoryCommand ParseAdd(IReadOnlyList<string> args, string cwd)
    {
        string? name = null;
        var linked = new List<LinkedRepositoryInput>();
        string? pendingLinkedName = null;

        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--name":
                    name = RequiredValue(args, ref index, option);
                    break;
                case "--linked-repo":
                    pendingLinkedName = RequiredValue(args, ref index, option);
                    break;
                case "--linked-repo-path":
                    if (string.IsNullOrWhiteSpace(pendingLinkedName))
                    {
                        throw new ArgumentException("--linked-repo-path requires a preceding --linked-repo <name>");
                    }

                    linked.Add(new LinkedRepositoryInput(pendingLinkedName, RequiredValue(args, ref index, option)));
                    pendingLinkedName = null;
                    break;
                default:
                    throw new ArgumentException($"Unknown add option: {option}");
            }
        }

        if (!string.IsNullOrWhiteSpace(pendingLinkedName))
        {
            throw new ArgumentException("--linked-repo requires --linked-repo-path <path>");
        }

        return new AddRepositoryCommand(cwd, name, linked);
    }

    private static RemoveRepositoryCommand ParseRemove(IReadOnlyList<string> args, string cwd)
    {
        string? repo = null;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--repo":
                    repo = RequiredValue(args, ref index, option);
                    break;
                default:
                    throw new ArgumentException($"Unknown remove option: {option}");
            }
        }

        return new RemoveRepositoryCommand(repo, cwd);
    }

    private static T ParsePauseResume<T>(
        IReadOnlyList<string> args,
        string cwd,
        Func<string?, string?, T> factory)
        where T : DevwtCommand
    {
        string? repo = null;
        string? worktree = null;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--repo":
                    repo = RequiredValue(args, ref index, option);
                    break;
                case "--worktree":
                    worktree = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        return factory(repo, worktree);
    }

    private static DevwtCommand ParseHook(IReadOnlyList<string> args, string cwd)
    {
        if (args.Count == 0 || !args[0].Equals("worktree-ready", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("hook requires subcommand: worktree-ready");
        }

        string? repoId = null;
        string? path = null;
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--repo-id":
                    repoId = RequiredValue(args, ref index, option);
                    break;
                case "--path":
                    path = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                default:
                    throw new ArgumentException($"Unknown hook option: {option}");
            }
        }

        if (string.IsNullOrWhiteSpace(repoId) || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("hook worktree-ready requires --repo-id <id> --path <path>");
        }

        return new WorktreeReadyHookCommand(repoId, path);
    }

    private static DevwtCommand ParseLink(IReadOnlyList<string> args, string cwd)
    {
        if (args.Count == 0 || !args[0].Equals("map", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("link requires subcommand: map");
        }

        string? linkedRepo = null;
        string? source = null;
        string? target = null;
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--linked-repo":
                    linkedRepo = RequiredValue(args, ref index, option);
                    break;
                case "--source":
                    source = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                case "--target":
                    target = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                default:
                    throw new ArgumentException($"Unknown link map option: {option}");
            }
        }

        if (string.IsNullOrWhiteSpace(linkedRepo) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("link map requires --linked-repo <name> --source <path> --target <path>");
        }

        return new LinkMapCommand(linkedRepo, source, target);
    }

    private static DevwtCommand ParseProxy(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("proxy requires subcommand: target, context, process, child, or clear");
        }

        return args[0].ToLowerInvariant() switch
        {
            "target" => ParseProxyTarget(args.Skip(1).ToArray()),
            "context" => ParseProxyContextTarget(args.Skip(1).ToArray()),
            "process" => ParseProxyProcess(args.Skip(1).ToArray()),
            "child" => ParseProxyChild(args.Skip(1).ToArray()),
            "clear" => ParseProxyClear(args.Skip(1).ToArray()),
            _ => throw new ArgumentException("proxy requires subcommand: target, context, process, child, or clear")
        };
    }

    private static DevwtCommand ParseRun(IReadOnlyList<string> args, string cwd)
    {
        var workingDirectory = cwd;
        var childrenOnly = false;
        var separator = -1;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            if (option == "--")
            {
                separator = index;
                break;
            }

            switch (option)
            {
                case "--children-only":
                    childrenOnly = true;
                    break;
                case "--worktree":
                    workingDirectory = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                default:
                    throw new ArgumentException($"Unknown run option: {option}");
            }
        }

        if (separator < 0 || separator + 1 >= args.Count)
        {
            throw new ArgumentException("run requires -- <program> [args...]");
        }

        return new RunCommand(
            workingDirectory,
            args[separator + 1],
            args.Skip(separator + 2).ToArray(),
            childrenOnly);
    }

    private static DevwtCommand ParseExec(IReadOnlyList<string> args, string cwd)
    {
        var run = ParseRun(args, cwd);
        return run is RunCommand command
            ? new ExecCommand(command.WorkingDirectory, command.Program, command.Arguments, command.ChildrenOnly)
            : run;
    }

    private static DevwtCommand ParseShortcut(IReadOnlyList<string> args, string cwd)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("shortcut requires subcommand: list, wrap, restore");
        }

        var action = args[0].ToLowerInvariant() switch
        {
            "list" => ShortcutAction.List,
            "wrap" => ShortcutAction.Wrap,
            "restore" => ShortcutAction.Restore,
            var unknown => throw new ArgumentException($"Unknown shortcut subcommand: {unknown}")
        };

        string? shortcutPath = null;
        string? name = null;
        string? worktreePath = null;
        var taskbar = false;
        var all = false;
        var dryRun = false;

        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--path":
                    shortcutPath = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                case "--taskbar":
                    taskbar = true;
                    break;
                case "--name":
                    name = RequiredValue(args, ref index, option);
                    break;
                case "--all":
                    all = true;
                    break;
                case "--worktree":
                    worktreePath = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown shortcut option: {option}");
            }
        }

        if (action == ShortcutAction.List)
        {
            if (!taskbar)
            {
                throw new ArgumentException("shortcut list currently requires --taskbar");
            }

            return new ShortcutCommand(action, shortcutPath, taskbar, name, all, worktreePath, dryRun);
        }

        if (!string.IsNullOrWhiteSpace(worktreePath) && action == ShortcutAction.Restore)
        {
            throw new ArgumentException("shortcut restore does not accept --worktree");
        }

        if (!string.IsNullOrWhiteSpace(shortcutPath) && taskbar)
        {
            throw new ArgumentException("shortcut accepts either --path or --taskbar, not both");
        }

        if (!string.IsNullOrWhiteSpace(name) && all)
        {
            throw new ArgumentException("shortcut accepts either --name or --all, not both");
        }

        if (string.IsNullOrWhiteSpace(shortcutPath) && !taskbar)
        {
            throw new ArgumentException("shortcut requires --path <shortcut.lnk> or --taskbar");
        }

        if (taskbar && string.IsNullOrWhiteSpace(name) && !all)
        {
            throw new ArgumentException("shortcut --taskbar requires --name <text> or --all");
        }

        return new ShortcutCommand(action, shortcutPath, taskbar, name, all, worktreePath, dryRun);
    }

    private static DevwtCommand ParseIde(IReadOnlyList<string> args, string cwd)
    {
        if (args.Count < 2 || !args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("ide requires subcommand: watch list, watch add, watch remove");
        }

        var action = args[1].ToLowerInvariant() switch
        {
            "list" => IdeWatchAction.List,
            "add" => IdeWatchAction.Add,
            "remove" => IdeWatchAction.Remove,
            var unknown => throw new ArgumentException($"Unknown ide watch subcommand: {unknown}")
        };

        string? name = null;
        string? imagePath = null;
        string? appId = null;
        string? packageFamilyName = null;
        var explicitPackageFamily = false;
        var all = false;
        for (var index = 2; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--name":
                    name = RequiredValue(args, ref index, option);
                    break;
                case "--path":
                    imagePath = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                case "--app-id":
                    appId = RequiredValue(args, ref index, option);
                    packageFamilyName = PackageFamilyNameFromAppId(appId);
                    break;
                case "--package-family":
                    packageFamilyName = RequiredValue(args, ref index, option);
                    explicitPackageFamily = true;
                    break;
                case "--all":
                    all = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown ide watch option: {option}");
            }
        }

        var selectorCount = (string.IsNullOrWhiteSpace(imagePath) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(appId) ? 0 : 1)
            + (explicitPackageFamily ? 1 : 0);

        if (action == IdeWatchAction.Add && (string.IsNullOrWhiteSpace(name) || selectorCount != 1))
        {
            throw new ArgumentException("ide watch add requires --name <name> and exactly one of --path, --app-id, or --package-family");
        }

        if (action == IdeWatchAction.Remove)
        {
            var selectors = (string.IsNullOrWhiteSpace(name) ? 0 : 1)
                + selectorCount
                + (all ? 1 : 0);
            if (selectors != 1)
            {
                throw new ArgumentException("ide watch remove requires exactly one of --name, --path, --app-id, --package-family, or --all");
            }
        }

        return new IdeWatchCommand(action, name, imagePath, appId, packageFamilyName, all);
    }

    private static string PackageFamilyNameFromAppId(string appId)
    {
        var separator = appId.IndexOf('!', StringComparison.Ordinal);
        if (separator <= 0 || separator == appId.Length - 1)
        {
            throw new ArgumentException("App id must use the package-family!application-id format.");
        }

        return appId[..separator];
    }

    private static DevwtCommand ParseTerminal(IReadOnlyList<string> args, string cwd)
    {
        var workingDirectory = cwd;
        var shell = "powershell";
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--worktree":
                    workingDirectory = DevwtPath.Normalize(Path.Combine(cwd, RequiredValue(args, ref index, option)));
                    break;
                case "--shell":
                    shell = RequiredValue(args, ref index, option).ToLowerInvariant();
                    if (shell is not ("powershell" or "cmd"))
                    {
                        throw new ArgumentException("--shell must be powershell or cmd");
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown terminal option: {option}");
            }
        }

        return new TerminalCommand(workingDirectory, shell);
    }

    private static DevwtCommand ParseProxyTarget(IReadOnlyList<string> args)
    {
        string? contextId = null;
        int? port = null;
        var scheme = "auto";
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--context":
                    contextId = RequiredValue(args, ref index, option);
                    break;
                case "--port":
                    var value = RequiredValue(args, ref index, option);
                    if (!int.TryParse(value, out var parsed) || parsed is <= 0 or > 65535)
                    {
                        throw new ArgumentException("--port requires a TCP port between 1 and 65535");
                    }

                    port = parsed;
                    break;
                case "--scheme":
                    scheme = RequiredValue(args, ref index, option).ToLowerInvariant();
                    if (scheme is not ("auto" or "http" or "https"))
                    {
                        throw new ArgumentException("--scheme must be auto, http or https");
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown proxy target option: {option}");
            }
        }

        if (string.IsNullOrWhiteSpace(contextId) || port is null)
        {
            throw new ArgumentException("proxy target requires --context <context-id> --port <port>");
        }

        return new ProxyTargetCommand(contextId, port.Value, scheme);
    }

    private static DevwtCommand ParseProxyContextTarget(IReadOnlyList<string> args)
    {
        string? contextId = null;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            if (option == "--context")
            {
                contextId = RequiredValue(args, ref index, option);
                continue;
            }

            throw new ArgumentException($"Unknown proxy context option: {option}");
        }

        return string.IsNullOrWhiteSpace(contextId)
            ? throw new ArgumentException("proxy context requires --context <context-id>")
            : new ProxyContextTargetCommand(contextId);
    }

    private static DevwtCommand ParseProxyClear(IReadOnlyList<string> args)
    {
        int? port = null;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            if (option != "--port")
            {
                throw new ArgumentException($"Unknown proxy clear option: {option}");
            }

            var value = RequiredValue(args, ref index, option);
            if (!int.TryParse(value, out var parsed) || parsed is <= 0 or > 65535)
            {
                throw new ArgumentException("--port requires a port between 1 and 65535");
            }

            port = parsed;
        }

        return new ProxyClearCommand(port);
    }

    private static DevwtCommand ParseProxyProcess(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("proxy process requires subcommand: target or clear");
        }

        return args[0].ToLowerInvariant() switch
        {
            "target" => ParseProxyProcessTarget(args.Skip(1).ToArray()),
            "clear" => ParseProxyProcessClear(args.Skip(1).ToArray()),
            _ => throw new ArgumentException("proxy process requires subcommand: target or clear")
        };
    }

    private static DevwtCommand ParseProxyProcessTarget(IReadOnlyList<string> args)
    {
        int? processId = null;
        string? contextId = null;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--pid":
                    processId = ParseProcessId(RequiredValue(args, ref index, option));
                    break;
                case "--context":
                    contextId = RequiredValue(args, ref index, option);
                    break;
                default:
                    throw new ArgumentException($"Unknown proxy process target option: {option}");
            }
        }

        if (processId is null || string.IsNullOrWhiteSpace(contextId))
        {
            throw new ArgumentException("proxy process target requires --pid <pid> --context <context-id>");
        }

        return new ProxyProcessTargetCommand(processId.Value, contextId);
    }

    private static DevwtCommand ParseProxyProcessClear(IReadOnlyList<string> args)
    {
        int? processId = null;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--pid":
                    processId = ParseProcessId(RequiredValue(args, ref index, option));
                    break;
                default:
                    throw new ArgumentException($"Unknown proxy process clear option: {option}");
            }
        }

        if (processId is null)
        {
            throw new ArgumentException("proxy process clear requires --pid <pid>");
        }

        return new ProxyProcessClearCommand(processId.Value);
    }

    private static DevwtCommand ParseProxyChild(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("proxy child requires subcommand: stop or kill");
        }

        var action = args[0].ToLowerInvariant() switch
        {
            "stop" => ProxyChildAction.Stop,
            "kill" => ProxyChildAction.Kill,
            _ => throw new ArgumentException("proxy child requires subcommand: stop or kill")
        };

        string? contextId = null;
        int? port = null;
        var protocol = "tcp";
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--context":
                    contextId = RequiredValue(args, ref index, option);
                    break;
                case "--port":
                    port = ParsePort(RequiredValue(args, ref index, option));
                    break;
                case "--protocol":
                    protocol = RequiredValue(args, ref index, option).ToLowerInvariant();
                    if (protocol is not ("tcp" or "udp"))
                    {
                        throw new ArgumentException("--protocol must be tcp or udp");
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown proxy child option: {option}");
            }
        }

        if (port is null)
        {
            throw new ArgumentException("proxy child requires --port <port>");
        }

        return new ProxyChildCommand(action, contextId, port.Value, protocol);
    }

    private static int ParseProcessId(string value)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException("--pid requires a positive process id");
        }

        return parsed;
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, out var parsed) || parsed is <= 0 or > 65535)
        {
            throw new ArgumentException("--port requires a TCP or UDP port between 1 and 65535");
        }

        return parsed;
    }

    private static string RequiredValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"{option} requires a value");
        }

        return args[++index];
    }
}
