namespace Devwt.Core.Tests;

public sealed class HookCoreStateTests
{
    [Fact]
    public void Git_worktree_porcelain_parser_reads_worktree_roots_and_refs()
    {
        var worktrees = GitWorktreeParser.ParsePorcelain(
            """
            worktree C:/repos/volo
            HEAD 1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree C:/work/volo-feature
            HEAD 2222222222222222222222222222222222222222
            branch refs/heads/feature/demo

            """);

        Assert.Collection(
            worktrees,
            item =>
            {
                Assert.Equal(@"C:\repos\volo", item.RootPath);
                Assert.Equal("main", item.RefName);
            },
            item =>
            {
                Assert.Equal(@"C:\work\volo-feature", item.RootPath);
                Assert.Equal("feature/demo", item.RefName);
            });
    }

    [Fact]
    public void Hook_installer_calls_internal_worktree_ready_command_and_is_idempotent()
    {
        using var temp = new TempDirectory();
        var hookPath = Path.Combine(temp.Path, ".git", "hooks", "post-checkout");

        var first = PostCheckoutHookInstaller.Install(hookPath, "devwt", "repo-volo");
        var second = PostCheckoutHookInstaller.Install(hookPath, "devwt", "repo-volo");
        var hook = File.ReadAllText(hookPath);

        Assert.True(first.Modified);
        Assert.True(second.AlreadyInstalled);
        Assert.Contains("DEVWT BEGIN", hook);
        Assert.Contains("if [ \"${3:-}\" = \"1\" ]; then", hook);
        Assert.Contains("devwt hook worktree-ready --repo-id \"repo-volo\" --path \"$PWD\"", hook);
        Assert.Equal(1, CountOccurrences(hook, "DEVWT BEGIN"));
    }

    [Fact]
    public void Hook_installer_replaces_existing_devwt_block_on_upgrade()
    {
        using var temp = new TempDirectory();
        var hookPath = Path.Combine(temp.Path, ".git", "hooks", "post-checkout");
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(
            hookPath,
            """
            #!/bin/sh
            echo before
            # DEVWT BEGIN
            old devwt command
            # DEVWT END
            echo after
            """);

        var result = PostCheckoutHookInstaller.Install(hookPath, "devwt", "repo-volo");
        var hook = File.ReadAllText(hookPath);

        Assert.True(result.Modified);
        Assert.Contains("echo before", hook);
        Assert.Contains("echo after", hook);
        Assert.DoesNotContain("old devwt command", hook);
        Assert.Contains("devwt hook worktree-ready --repo-id \"repo-volo\" --path \"$PWD\"", hook);
        Assert.Equal(1, CountOccurrences(hook, "DEVWT BEGIN"));
    }

    [Fact]
    public void State_store_round_trips_repositories_contexts_and_routing()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository(
            Id: "repo-volo",
            Name: "volo",
            RootPath: @"C:\repos\volo",
            GitCommonDir: @"C:\repos\volo\.git",
            LinkedRepositories: [new LinkedRepository("abp", "../abp", @"C:\repos\abp")]);
        var context = new DevwtContext(
            Id: "ctx-volo-feature",
            RepositoryId: "repo-volo",
            Name: "volo-feature",
            WorktreeRootPath: @"C:\work\volo-feature",
            GitRef: "feature/demo",
            AssignedIp: "127.80.1.10",
            RuntimeName: "DevWT-runtime",
            Status: DevwtContextStatus.Active,
            Description: "PR 22558 review");

        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([context]));
        store.SaveRouting(new DevwtRoutingState(
            ExplicitLinkMaps: [new DevwtLinkMap("abp", @"C:\work\volo-feature", @"C:\work\abp-feature")],
            ActiveTarget: new DevwtActiveTarget("ctx-volo-feature", 5025, "http"),
            ProcessTargets: [new DevwtProcessTarget(1234, "ctx-volo-feature")],
            ApplicationTargets: [new DevwtApplicationTarget(@"C:\tools\chrome.exe", "ctx-volo-feature", 5025, "auto")]));

        var loadedRepository = Assert.Single(store.LoadRepositories().Repositories);
        Assert.Equal(repository.Id, loadedRepository.Id);
        Assert.Equal(repository.Name, loadedRepository.Name);
        Assert.Equal(repository.RootPath, loadedRepository.RootPath);
        Assert.Equal(repository.GitCommonDir, loadedRepository.GitCommonDir);
        Assert.Equal(repository.LinkedRepositories, loadedRepository.LinkedRepositories);
        var loadedContext = Assert.Single(store.LoadContexts().Contexts);
        Assert.Equal(context.Id, loadedContext.Id);
        Assert.Equal("PR 22558 review", loadedContext.Description);
        Assert.Equal("127.0.0.1", loadedContext.AssignedIp);
        Assert.Equal(DevwtPortShift.AssignedPortBaseFor(context.Id), loadedContext.AssignedPortBase);
        var loadedRouting = store.LoadRouting();
        Assert.Null(loadedRouting.ActiveTarget);
        Assert.Equal(
            new DevwtPortActiveTarget("ctx-volo-feature", 5025),
            Assert.Single(loadedRouting.PortActiveTargets));
        var processTarget = Assert.Single(loadedRouting.ProcessTargets);
        Assert.Equal(1234, processTarget.ProcessId);
        Assert.Equal("ctx-volo-feature", processTarget.ContextId);
        var applicationTarget = Assert.Single(loadedRouting.ApplicationTargets);
        Assert.Equal(@"C:\tools\chrome.exe", applicationTarget.ApplicationKey);
        Assert.Equal("ctx-volo-feature", applicationTarget.ContextId);
        Assert.Equal(5025, applicationTarget.Port);
    }

    [Fact]
    public void Routing_state_round_trips_global_and_independent_port_targets()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        store.SaveRouting(new DevwtRoutingState([], null)
        {
            ActiveTargetMode = DevwtActiveTargetMode.GlobalContext,
            GlobalActiveContextId = "ctx-global",
            PortActiveTargets =
            [
                new DevwtPortActiveTarget("ctx-a", 44334),
                new DevwtPortActiveTarget("ctx-b", 5001)
            ]
        });

        var loaded = store.LoadRouting();

        Assert.Equal(DevwtActiveTargetMode.GlobalContext, loaded.ActiveTargetMode);
        Assert.Equal("ctx-global", loaded.GlobalActiveContextId);
        Assert.Equal([5001, 44334], loaded.PortActiveTargets.Select(target => target.Port));
    }

    [Fact]
    public void Routing_state_round_trips_scoped_targets()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var state = new DevwtRoutingState([], null)
        {
            ApplicationContextTargets = [new(@"C:\tools\codex.exe", "ctx-a")],
            ProcessPortTargets = [new(1200, "ctx-b", 44334, "auto")],
            SessionContextTargets = [new("codex:thread-a", "ctx-a")],
            SessionPortTargets = [new("codex:thread-a", "ctx-b", 44334, "auto")],
            HttpsProxyEndpoints = [new("127.0.0.1", 44334, DevwtHttpsProxyMode.Raw)]
        };

        store.SaveRouting(state);
        var loaded = store.LoadRouting();

        Assert.Equal(state.ApplicationContextTargets, loaded.ApplicationContextTargets);
        Assert.Equal(state.ProcessPortTargets, loaded.ProcessPortTargets);
        Assert.Equal(state.SessionContextTargets, loaded.SessionContextTargets);
        Assert.Equal(state.SessionPortTargets, loaded.SessionPortTargets);
        Assert.Equal(state.HttpsProxyEndpoints, loaded.HttpsProxyEndpoints);
    }

    [Fact]
    public void Routing_state_normalizes_duplicate_scoped_targets_by_logical_key()
    {
        var normalized = DevwtRoutingState.Normalize(new DevwtRoutingState([], null)
        {
            ApplicationContextTargets =
            [
                new(@"C:\Tools\Codex.exe", "ctx-a"),
                new(@"c:\tools\codex.exe", "ctx-b")
            ],
            ProcessPortTargets =
            [
                new(1200, "ctx-a", 44334, "auto"),
                new(1200, "ctx-b", 44334, "https")
            ],
            SessionContextTargets =
            [
                new("codex:thread-a", "ctx-a"),
                new("CODEX:THREAD-A", "ctx-b")
            ],
            SessionPortTargets =
            [
                new("codex:thread-a", "ctx-a", 44334, "auto"),
                new("CODEX:THREAD-A", "ctx-b", 44334, "https")
            ],
            HttpsProxyEndpoints =
            [
                new("127.0.0.1", 44334, DevwtHttpsProxyMode.Tunnel),
                new("127.0.0.1", 44334, DevwtHttpsProxyMode.Inspect)
            ]
        });

        Assert.Equal("ctx-b", Assert.Single(normalized.ApplicationContextTargets).ContextId);
        Assert.Equal("ctx-b", Assert.Single(normalized.ProcessPortTargets).ContextId);
        Assert.Equal("ctx-b", Assert.Single(normalized.SessionContextTargets).ContextId);
        Assert.Equal("ctx-b", Assert.Single(normalized.SessionPortTargets).ContextId);
        Assert.Equal(DevwtHttpsProxyMode.Inspect, Assert.Single(normalized.HttpsProxyEndpoints).Mode);
    }

    [Fact]
    public void Routing_state_migrates_legacy_active_target_to_per_port_mode()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "routing.json"), """
            {
              "explicitLinkMaps": [],
              "activeTarget": { "contextId": "ctx-legacy", "port": 44334, "scheme": "https" },
              "processTargets": [{ "processId": 17, "contextId": "ctx-process" }]
            }
            """);

        var loaded = new DevwtStateStore(temp.Path).LoadRouting();

        Assert.Equal(DevwtActiveTargetMode.PerPort, loaded.ActiveTargetMode);
        Assert.Null(loaded.ActiveTarget);
        Assert.Equal(new DevwtPortActiveTarget("ctx-legacy", 44334), Assert.Single(loaded.PortActiveTargets));
        Assert.Equal(17, Assert.Single(loaded.ProcessTargets).ProcessId);
    }

    [Fact]
    public void Runtime_settings_round_trip_session_rules()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var rule = new DevwtSessionRule(
            Name: "Codex",
            Match: new DevwtSessionMatch(EnvironmentVariable: "CODEX_THREAD_ID"),
            Identity: new DevwtSessionIdentity(
                DevwtSessionIdentityKind.EnvironmentVariable,
                Value: "CODEX_THREAD_ID",
                Prefix: "codex:"));
        store.SaveRuntimeSettings(new DevwtRuntimeSettings(
            [],
            [rule],
            BrowserFallbackOnMissingPort: true,
            BrowserMissingPortPolicies: [
                new DevwtBrowserMissingPortPolicy(
                    "ctx-active",
                    44373,
                    DevwtBrowserMissingPortPolicyMode.Automatic)
            ]));

        var settings = store.LoadRuntimeSettings();
        var loaded = Assert.Single(settings.SessionRules);
        Assert.Equal("Codex", loaded.Name);
        Assert.Equal("CODEX_THREAD_ID", loaded.Match.EnvironmentVariable);
        Assert.Equal(DevwtSessionIdentityKind.EnvironmentVariable, loaded.Identity.Kind);
        Assert.Equal("codex:", loaded.Identity.Prefix);
        Assert.True(settings.BrowserFallbackOnMissingPort);
        Assert.Equal(
            DevwtBrowserMissingPortPolicyMode.Automatic,
            Assert.Single(settings.BrowserMissingPortPolicies).Mode);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
