namespace Devwt.Core.Tests;

public sealed class HookCoreCommandTests
{
    [Fact]
    public void Update_command_preserves_running_applications_by_default_and_can_stop_them_explicitly()
    {
        var safe = Assert.IsType<UpdateCommand>(DevwtCommandParser.Parse(["update"]));
        var disruptive = Assert.IsType<UpdateCommand>(DevwtCommandParser.Parse(
            ["update", "--stop-running-applications"]));

        Assert.False(safe.StopRunningApplications);
        Assert.True(disruptive.StopRunningApplications);
        Assert.Throws<ArgumentException>(() => DevwtCommandParser.Parse(["update", "--unknown"]));
    }

    [Fact]
    public void Add_command_defaults_to_current_git_repo_and_accepts_unlimited_linked_repos()
    {
        var command = DevwtCommandParser.Parse(
            [
                "add",
                "--name",
                "sample",
                "--linked-repo",
                "shared",
                "--linked-repo-path",
                "../shared",
                "--linked-repo",
                "studio",
                "--linked-repo-path",
                "../shared-lib"
            ],
            @"C:\repos\sample");

        var add = Assert.IsType<AddRepositoryCommand>(command);
        Assert.Equal(@"C:\repos\sample", add.WorkingDirectory);
        Assert.Equal("sample", add.Name);
        Assert.Collection(
            add.LinkedRepositories,
            linked =>
            {
                Assert.Equal("shared", linked.Name);
                Assert.Equal("../shared", linked.Path);
            },
            linked =>
            {
                Assert.Equal("studio", linked.Name);
                Assert.Equal("../shared-lib", linked.Path);
            });
    }

    [Fact]
    public void Remove_pause_resume_and_hook_commands_are_parsed()
    {
        var removeCurrent = Assert.IsType<RemoveRepositoryCommand>(DevwtCommandParser.Parse(["remove"], @"C:\repos\sample\src"));
        Assert.Null(removeCurrent.RepositoryName);
        Assert.Equal(@"C:\repos\sample\src", removeCurrent.WorkingDirectory);

        var removeByName = Assert.IsType<RemoveRepositoryCommand>(DevwtCommandParser.Parse(["remove", "--repo", "sample"], @"C:\repos\sample"));
        Assert.Equal("sample", removeByName.RepositoryName);
        Assert.Equal(@"C:\repos\sample", removeByName.WorkingDirectory);

        Assert.IsType<PauseCommand>(DevwtCommandParser.Parse(["pause", "--worktree", @"..\sample-wt"], @"C:\repos\sample"));
        Assert.IsType<ResumeCommand>(DevwtCommandParser.Parse(["resume", "--repo", "sample"], @"C:\repos\sample"));

        var hook = Assert.IsType<WorktreeReadyHookCommand>(DevwtCommandParser.Parse(
            ["hook", "worktree-ready", "--repo-id", "repo-sample", "--path", "."],
            @"C:\repos\sample-wt"));

        Assert.Equal("repo-sample", hook.RepositoryId);
        Assert.Equal(@"C:\repos\sample-wt", hook.WorktreePath);
    }

    [Fact]
    public void Context_describe_upserts_current_or_explicit_worktree_and_can_clear()
    {
        var current = Assert.IsType<DescribeContextCommand>(DevwtCommandParser.Parse(
            ["context", "describe", "Review gateway routing"],
            @"C:\work\sample"));
        var explicitWorktree = Assert.IsType<DescribeContextCommand>(DevwtCommandParser.Parse(
            ["context", "describe", "--worktree", @"..\sample-review", "Review", "authentication", "changes"],
            @"C:\work\sample"));
        var clear = Assert.IsType<DescribeContextCommand>(DevwtCommandParser.Parse(
            ["context", "describe", "--clear"],
            @"C:\work\sample"));

        Assert.Equal(@"C:\work\sample", current.WorktreePath);
        Assert.Equal("Review gateway routing", current.Description);
        Assert.False(current.Clear);
        Assert.Equal(@"C:\work\sample-review", explicitWorktree.WorktreePath);
        Assert.Equal("Review authentication changes", explicitWorktree.Description);
        Assert.False(explicitWorktree.Clear);
        Assert.Null(clear.Description);
        Assert.True(clear.Clear);
    }

    [Fact]
    public void Port_commands_use_current_context_or_an_explicit_context()
    {
        var process = Assert.IsType<PortCommand>(DevwtCommandParser.Parse(
            ["port", "process", "--port", "44334"],
            @"C:\work\sample\src"));
        var check = Assert.IsType<PortCommand>(DevwtCommandParser.Parse(
            ["port", "check", "--port", "44334", "--context", "ctx-sample-review"],
            @"C:\work\sample"));

        Assert.Equal(PortCommandAction.Process, process.Action);
        Assert.Equal(44334, process.Port);
        Assert.Null(process.ContextId);
        Assert.Equal(@"C:\work\sample\src", process.WorkingDirectory);
        Assert.Equal(PortCommandAction.Check, check.Action);
        Assert.Equal(44334, check.Port);
        Assert.Equal("ctx-sample-review", check.ContextId);
    }

    [Fact]
    public void Proxy_target_is_context_and_port_with_auto_scheme_by_default()
    {
        var command = Assert.IsType<ProxyTargetCommand>(DevwtCommandParser.Parse(
            ["proxy", "target", "--context", "ctx-sample", "--port", "5025"],
            @"C:\repos\sample"));

        Assert.Equal("ctx-sample", command.ContextId);
        Assert.Equal(5025, command.Port);
        Assert.Equal("auto", command.Scheme);
    }

    [Fact]
    public void Proxy_context_and_port_specific_clear_commands_are_parsed()
    {
        var context = Assert.IsType<ProxyContextTargetCommand>(DevwtCommandParser.Parse(
            ["proxy", "context", "--context", "ctx-a"]));
        var clearPort = Assert.IsType<ProxyClearCommand>(DevwtCommandParser.Parse(
            ["proxy", "clear", "--port", "44334"]));
        var clearMode = Assert.IsType<ProxyClearCommand>(DevwtCommandParser.Parse(
            ["proxy", "clear"]));

        Assert.Equal("ctx-a", context.ContextId);
        Assert.Equal(44334, clearPort.Port);
        Assert.Null(clearMode.Port);
    }

    [Fact]
    public void Proxy_process_target_is_context_for_pid()
    {
        var command = Assert.IsType<ProxyProcessTargetCommand>(DevwtCommandParser.Parse(
            ["proxy", "process", "target", "--pid", "1234", "--context", "ctx-sample"],
            @"C:\repos\sample"));

        Assert.Equal(1234, command.ProcessId);
        Assert.Equal("ctx-sample", command.ContextId);
    }

    [Fact]
    public void Proxy_process_clear_is_pid_only()
    {
        var command = Assert.IsType<ProxyProcessClearCommand>(DevwtCommandParser.Parse(
            ["proxy", "process", "clear", "--pid", "1234"],
            @"C:\repos\sample"));

        Assert.Equal(1234, command.ProcessId);
    }

    [Fact]
    public void Proxy_child_stop_and_kill_target_backend_listener_by_port()
    {
        var stop = Assert.IsType<ProxyChildCommand>(DevwtCommandParser.Parse(
            ["proxy", "child", "stop", "--port", "44334", "--context", "ctx-sample"],
            @"C:\repos\sample"));
        var kill = Assert.IsType<ProxyChildCommand>(DevwtCommandParser.Parse(
            ["proxy", "child", "kill", "--port", "44334", "--protocol", "udp"],
            @"C:\repos\sample"));

        Assert.Equal(ProxyChildAction.Stop, stop.Action);
        Assert.Equal("ctx-sample", stop.ContextId);
        Assert.Equal(44334, stop.Port);
        Assert.Equal("tcp", stop.Protocol);
        Assert.Equal(ProxyChildAction.Kill, kill.Action);
        Assert.Null(kill.ContextId);
        Assert.Equal(44334, kill.Port);
        Assert.Equal("udp", kill.Protocol);
    }

    [Fact]
    public void Exec_command_launches_runtime_with_passthrough_semantics()
    {
        var command = Assert.IsType<ExecCommand>(DevwtCommandParser.Parse(
            ["exec", "--", "node", "server.js"],
            @"C:\repos\sample"));

        Assert.Equal(@"C:\repos\sample", command.WorkingDirectory);
        Assert.Equal("node", command.Program);
        Assert.Equal(["server.js"], command.Arguments);
    }

    [Fact]
    public void Run_command_can_launch_only_child_processes_through_devwt()
    {
        var command = Assert.IsType<RunCommand>(DevwtCommandParser.Parse(
            ["run", "--children-only", "--", @"C:\tools\ide.exe", "--restore"],
            @"C:\Users\developer"));

        Assert.Equal(@"C:\Users\developer", command.WorkingDirectory);
        Assert.Equal(@"C:\tools\ide.exe", command.Program);
        Assert.Equal(["--restore"], command.Arguments);
        Assert.True(command.ChildrenOnly);
    }

    [Fact]
    public void Shortcut_wrap_command_targets_taskbar_shortcuts_by_name()
    {
        var command = Assert.IsType<ShortcutCommand>(DevwtCommandParser.Parse(
            ["shortcut", "wrap", "--taskbar", "--name", "Rider", "--dry-run"],
            @"C:\repos\sample"));

        Assert.Equal(ShortcutAction.Wrap, command.Action);
        Assert.True(command.Taskbar);
        Assert.Equal("Rider", command.Name);
        Assert.True(command.DryRun);
        Assert.Null(command.ShortcutPath);
        Assert.Null(command.WorktreePath);
    }

    [Fact]
    public void Ide_watch_commands_are_parsed()
    {
        var add = Assert.IsType<IdeWatchCommand>(DevwtCommandParser.Parse(
            ["ide", "watch", "add", "--name", "Rider", "--path", @"..\Rider\bin\rider64.exe"],
            @"C:\Tools"));

        Assert.Equal(IdeWatchAction.Add, add.Action);
        Assert.Equal("Rider", add.Name);
        Assert.Equal(@"C:\Rider\bin\rider64.exe", add.ImagePath);
        Assert.False(add.All);

        var list = Assert.IsType<IdeWatchCommand>(DevwtCommandParser.Parse(
            ["ide", "watch", "list"],
            @"C:\Tools"));

        Assert.Equal(IdeWatchAction.List, list.Action);

        var remove = Assert.IsType<IdeWatchCommand>(DevwtCommandParser.Parse(
            ["ide", "watch", "remove", "--name", "Rider"],
            @"C:\Tools"));

        Assert.Equal(IdeWatchAction.Remove, remove.Action);
        Assert.Equal("Rider", remove.Name);
    }

    [Fact]
    public void Ide_watch_store_app_commands_are_parsed()
    {
        var appId = Assert.IsType<IdeWatchCommand>(DevwtCommandParser.Parse(
            ["ide", "watch", "add", "--name", "Codex", "--app-id", "OpenAI.Codex_2p2nqsd0c76g0!App"],
            @"C:\Tools"));

        Assert.Equal(IdeWatchAction.Add, appId.Action);
        Assert.Equal("Codex", appId.Name);
        Assert.Null(appId.ImagePath);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!App", appId.AppId);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", appId.PackageFamilyName);

        var packageFamily = Assert.IsType<IdeWatchCommand>(DevwtCommandParser.Parse(
            ["ide", "watch", "add", "--name", "Codex", "--package-family", "OpenAI.Codex_2p2nqsd0c76g0"],
            @"C:\Tools"));

        Assert.Equal(IdeWatchAction.Add, packageFamily.Action);
        Assert.Equal("Codex", packageFamily.Name);
        Assert.Null(packageFamily.ImagePath);
        Assert.Null(packageFamily.AppId);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", packageFamily.PackageFamilyName);

        var remove = Assert.IsType<IdeWatchCommand>(DevwtCommandParser.Parse(
            ["ide", "watch", "remove", "--app-id", "OpenAI.Codex_2p2nqsd0c76g0!App"],
            @"C:\Tools"));

        Assert.Equal(IdeWatchAction.Remove, remove.Action);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", remove.PackageFamilyName);
    }

    [Fact]
    public void Sandboxie_commands_are_not_part_of_hook_core_surface()
    {
        var command = Assert.IsType<HelpCommand>(DevwtCommandParser.Parse(["sandboxie", "status"], @"C:\repos\sample"));

        Assert.Equal(2, command.ExitCode);
        Assert.Contains("Unknown command: sandboxie", command.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sandboxie", DevwtCommandParser.HelpText, StringComparison.OrdinalIgnoreCase);
    }
}
