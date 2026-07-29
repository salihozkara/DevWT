using Devwt.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Yarp.ReverseProxy.Forwarder;

namespace Devwt.Service.Tests;

public sealed class HookCoreServiceTests
{
    [Fact]
    public void Gateway_route_table_resolves_https_proxy_mode_by_ip_and_port_with_auto_default()
    {
        var routing = new DevwtRoutingState([], null)
        {
            HttpsProxyEndpoints = [new("127.0.0.1", 44334, DevwtHttpsProxyMode.Inspect)]
        };
        var table = GatewayRouteTable.FromRoutes(
            [],
            DevwtRepositoryState.Empty,
            DevwtContextState.Empty,
            routing);

        Assert.Equal(DevwtHttpsProxyMode.Inspect, table.TcpHandlingModeFor("127.0.0.1", 44334));
        Assert.Equal(DevwtHttpsProxyMode.Auto, table.TcpHandlingModeFor("::1", 44334));
        Assert.Equal(DevwtHttpsProxyMode.Auto, table.TcpHandlingModeFor("127.0.0.1", 5001));
    }

    [Fact]
    public void Control_handler_sets_https_proxy_mode_for_one_ip_and_port()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var manager = new DevwtManager(
            store,
            new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])),
            new RecordingHookRuntimeConfigurator());
        var handler = new DevwtControlHandler(manager, store);

        var result = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetHttpsProxyMode,
            HttpsProxyEndpoint: new DevwtHttpsProxyEndpoint("127.0.0.1", 44334, DevwtHttpsProxyMode.Tunnel)));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            new DevwtHttpsProxyEndpoint("127.0.0.1", 44334, DevwtHttpsProxyMode.Tunnel),
            Assert.Single(store.LoadRouting().HttpsProxyEndpoints));
    }

    [Fact]
    public void Gateway_worker_alias_contains_backend_image_names_and_a_stable_endpoint_hash()
    {
        var first = DevwtGatewayWorkerNames.BuildAliasFileName(
            ["LowCodeDemoApp.HttpApi.Host.exe", "vite.exe"],
            "127.0.0.1:44334|LowCodeDemoApp.HttpApi.Host.exe|vite.exe");
        var second = DevwtGatewayWorkerNames.BuildAliasFileName(
            ["LowCodeDemoApp.HttpApi.Host.exe", "vite.exe"],
            "127.0.0.1:5001|LowCodeDemoApp.HttpApi.Host.exe|vite.exe");

        Assert.Contains("LowCodeDemoApp.HttpApi.Host", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vite", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DevwtGatewayWorkerNames.AliasMarker, first, StringComparison.Ordinal);
        Assert.EndsWith(".exe", first, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Gateway_worker_exit_stops_every_tcp_and_udp_listener_for_only_that_ip_and_port()
    {
        GatewayRoute[] routes =
        [
            new("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 52001, 101, GatewayRouteProtocol.Tcp),
            new("ctx-b", "repo-a", @"C:\work\b", 44334, "127.0.0.1", 52002, 202, GatewayRouteProtocol.Tcp),
            new("ctx-c", "repo-a", @"C:\work\c", 44334, "127.0.0.1", 52003, 303, GatewayRouteProtocol.Udp),
            new("ctx-d", "repo-a", @"C:\work\d", 5001, "127.0.0.1", 52004, 404, GatewayRouteProtocol.Tcp),
            new("ctx-e", "repo-a", @"C:\work\e", 44334, "127.0.0.1", 52005, 505, GatewayRouteProtocol.Tcp, "127.0.0.2")
        ];

        var processIds = DevwtGatewayWorkerExitPolicy.ListenerProcessIdsFor(
            routes,
            new DevwtGatewayWorkerEndpoint("127.0.0.1", 44334));

        Assert.Equal([101, 202, 303], processIds);
    }

    [Fact]
    public void Gateway_workers_read_the_supervisors_atomic_route_snapshot()
    {
        using var temp = new TempDirectory();
        var context = Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1") with
        {
            Description = "Review driver code"
        };
        var route = new GatewayRoute(
            context.Id,
            context.RepositoryId,
            context.WorktreeRootPath,
            44334,
            "127.0.0.1",
            52001,
            101);
        var table = GatewayRouteTable.FromRoutes(
            [route],
            new DevwtRepositoryState([
                new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", [])
            ]),
            new DevwtContextState([context]),
            DevwtRoutingState.Empty);
        var writer = new DevwtGatewayRouteSnapshotStore(temp.Path);
        var reader = new DevwtGatewayRouteSnapshotStore(temp.Path);

        writer.Save(table);
        var loaded = reader.BuildRouteTable();

        Assert.Equal(route, Assert.Single(loaded.Routes));
        Assert.Equal(route, loaded.ResolveNewest(44334));
        Assert.Equal("Review driver code", loaded.DescriptionForContext("ctx-a"));
    }

    [Fact]
    public void Control_handler_records_worker_connection_history_in_the_service_history()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var history = new DevwtConnectionHistory();
        var manager = new DevwtManager(
            store,
            new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])),
            new RecordingHookRuntimeConfigurator());
        var handler = new DevwtControlHandler(manager, store, connectionHistory: history);
        var entry = new DevwtConnectionHistoryEntry(
            DateTimeOffset.UtcNow,
            GatewayRouteProtocol.Tcp,
            "127.0.0.1",
            44334,
            "127.0.0.1",
            52301,
            "ctx-a",
            "repo-a",
            "process-context",
            42,
            @"C:\work\a\app.exe",
            @"c:\work\a\app.exe",
            "127.0.0.1:53000");

        var result = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.RecordGatewayConnection,
            ConnectionHistoryEntry: entry));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(entry, Assert.Single(history.Snapshot()));
    }

    [Fact]
    public void Linked_repo_routing_prefers_linked_target_before_self_context()
    {
        var callerRepo = new DevwtRepository(
            "repo-volo",
            "volo",
            @"C:\repos\volo",
            @"C:\repos\volo\.git",
            [new LinkedRepository("abp", "../abp", @"C:\repos\abp")]);
        var calleeRepo = new DevwtRepository("repo-abp", "abp", @"C:\repos\abp", @"C:\repos\abp\.git", []);
        var caller = Context("ctx-volo-feature", "repo-volo", @"C:\work\volo", "127.80.1.10");
        var self = new GatewayRoute("ctx-volo-feature", "repo-volo", @"C:\work\volo", 5025, "127.80.1.10", 5025, 10);
        var linked = new GatewayRoute("ctx-abp-feature", "repo-abp", @"C:\work\abp", 5025, "127.80.1.11", 5025, 11);
        var table = GatewayRouteTable.FromRoutes(
            [self, linked],
            new DevwtRepositoryState([callerRepo, calleeRepo]),
            new DevwtContextState([caller, Context("ctx-abp-feature", "repo-abp", @"C:\work\abp", "127.80.1.11")]),
            DevwtRoutingState.Empty);

        var route = table.Resolve(5025, callerContextId: "ctx-volo-feature", requestContextId: null, cookieContextId: null, includeActiveTarget: true);

        Assert.NotNull(route);
        Assert.Equal("ctx-abp-feature", route.ContextId);
    }

    [Fact]
    public void Browser_request_context_header_overrides_global_active_target_per_tab()
    {
        var first = new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5025, "127.80.1.10", 5025, 10);
        var second = new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 5025, "127.80.1.11", 5025, 11);
        var table = GatewayRouteTable.FromRoutes(
            [first, second],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.80.1.10"),
                Context("ctx-b", "repo-b", @"C:\work\b", "127.80.1.11")
            ]),
            new DevwtRoutingState([], new DevwtActiveTarget("ctx-a", 5025, "auto")));

        var route = table.Resolve(5025, callerContextId: null, requestContextId: "ctx-b", cookieContextId: null, includeActiveTarget: true);

        Assert.NotNull(route);
        Assert.Equal("ctx-b", route.ContextId);
    }

    [Fact]
    public void Browser_request_context_header_resolves_without_caller_process_lookup()
    {
        var first = new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5025, "127.80.1.10", 5025, 10);
        var second = new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 5025, "127.80.1.11", 5025, 11);
        var table = GatewayRouteTable.FromRoutes(
            [first, second],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.80.1.10"),
                Context("ctx-b", "repo-b", @"C:\work\b", "127.80.1.11")
            ]),
            new DevwtRoutingState([], new DevwtActiveTarget("ctx-a", 5025, "auto")));

        var route = table.ResolveWithoutCaller(5025, requestContextId: "ctx-b", cookieContextId: null, includeActiveTarget: true);

        Assert.NotNull(route);
        Assert.Equal("ctx-b", route.ContextId);
    }

    [Fact]
    public void Active_proxy_target_resolves_without_caller_process_lookup()
    {
        var first = new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5025, "127.80.1.10", 5025, 10);
        var second = new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 5025, "127.80.1.11", 5025, 11);
        var table = GatewayRouteTable.FromRoutes(
            [first, second],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.80.1.10"),
                Context("ctx-b", "repo-b", @"C:\work\b", "127.80.1.11")
            ]),
            new DevwtRoutingState([], new DevwtActiveTarget("ctx-b", 5025, "auto")));

        var route = table.ResolveWithoutCaller(5025, requestContextId: null, cookieContextId: null, includeActiveTarget: true);

        Assert.NotNull(route);
        Assert.Equal("ctx-b", route.ContextId);
    }

    [Fact]
    public void Gateway_global_context_target_resolves_same_context_across_ports()
    {
        var contexts = new DevwtContextState([
            Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", "repo-b", @"C:\work\b", "127.0.0.1")
        ]);
        var table = GatewayRouteTable.FromRoutes(
            [
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 54334, 10),
                new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 44334, "127.0.0.1", 55334, 11),
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5001, "127.0.0.1", 25001, 10),
                new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 5001, "127.0.0.1", 35001, 11)
            ],
            DevwtRepositoryState.Empty,
            contexts,
            new DevwtRoutingState([], null)
            {
                ActiveTargetMode = DevwtActiveTargetMode.GlobalContext,
                GlobalActiveContextId = "ctx-a"
            });

        Assert.Equal("ctx-a", table.ResolveGlobalActiveTarget(44334)!.ContextId);
        Assert.Equal("ctx-a", table.ResolveGlobalActiveTarget(5001)!.ContextId);
    }

    [Fact]
    public void Gateway_per_port_targets_are_independent_and_shared_by_tcp_udp()
    {
        var contexts = new DevwtContextState([
            Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", "repo-b", @"C:\work\b", "127.0.0.1")
        ]);
        var table = GatewayRouteTable.FromRoutes(
            [
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 54334, 10),
                new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 44334, "127.0.0.1", 55334, 11),
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 54334, 10, GatewayRouteProtocol.Udp),
                new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 44334, "127.0.0.1", 55334, 11, GatewayRouteProtocol.Udp),
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5001, "127.0.0.1", 25001, 10),
                new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 5001, "127.0.0.1", 35001, 11)
            ],
            DevwtRepositoryState.Empty,
            contexts,
            new DevwtRoutingState([], null)
            {
                ActiveTargetMode = DevwtActiveTargetMode.PerPort,
                PortActiveTargets =
                [
                    new DevwtPortActiveTarget("ctx-a", 44334),
                    new DevwtPortActiveTarget("ctx-b", 5001)
                ]
            });

        Assert.Equal("ctx-a", table.ResolveGlobalActiveTarget(44334, GatewayRouteProtocol.Tcp)!.ContextId);
        Assert.Equal("ctx-a", table.ResolveGlobalActiveTarget(44334, GatewayRouteProtocol.Udp)!.ContextId);
        Assert.Equal("ctx-b", table.ResolveGlobalActiveTarget(5001)!.ContextId);
    }

    [Fact]
    public void Gateway_route_table_can_apply_fresh_routing_state_without_listener_refresh()
    {
        var first = new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 54001, 10);
        var second = new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 44334, "127.0.0.1", 55001, 11);
        var table = GatewayRouteTable.FromRoutes(
            [first, second],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
                Context("ctx-b", "repo-b", @"C:\work\b", "127.0.0.1")
            ]),
            DevwtRoutingState.Empty);

        var route = table
            .WithRouting(new DevwtRoutingState([], new DevwtActiveTarget("ctx-a", 44334, "auto")))
            .ResolveGlobalActiveTarget(44334, listenIp: "127.0.0.1");

        Assert.NotNull(route);
        Assert.Equal("ctx-a", route.ContextId);
    }

    [Fact]
    public void Ambiguous_proxy_target_falls_back_to_newest_observed_route_when_no_process_context_exists()
    {
        var older = new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 54001, 10);
        var newest = new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 44334, "127.0.0.1", 55001, 11);
        var table = GatewayRouteTable.FromRoutes(
            [older, newest],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
                Context("ctx-b", "repo-b", @"C:\work\b", "127.0.0.1")
            ]),
            DevwtRoutingState.Empty);

        var route = table.ResolveWithoutCaller(44334, requestContextId: null, cookieContextId: null, includeActiveTarget: true);

        Assert.NotNull(route);
        Assert.Equal("ctx-b", route.ContextId);
    }

    [Fact]
    public void Process_context_matcher_propagates_context_from_parent_chain()
    {
        var contexts = new DevwtContextState([
            Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1")
        ]);
        var processes = new[]
        {
            new ProcessObservation(100, null, @"C:\tools\codex.exe", null, @"C:\work\a"),
            new ProcessObservation(200, 100, @"C:\tools\mcp.exe", null, null),
            new ProcessObservation(300, 200, @"C:\tools\chrome.exe", null, null)
        };

        var map = ProcessContextMatcher.ResolveProcessContexts(contexts, processes);

        Assert.Equal("ctx-a", map[100]);
        Assert.Equal("ctx-a", map[200]);
        Assert.Equal("ctx-a", map[300]);
    }

    [Fact]
    public void Process_context_matcher_prefers_process_worktree_over_parent_chain()
    {
        var contexts = new DevwtContextState([
            Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", "repo-b", @"C:\work\b", "127.0.0.1")
        ]);
        var processes = new[]
        {
            new ProcessObservation(100, null, @"C:\tools\launcher.exe", null, @"C:\work\a"),
            new ProcessObservation(200, 100, @"C:\work\b\src\App.Host\bin\Debug\net10.0\App.Host.exe", null, null)
        };

        var map = ProcessContextMatcher.ResolveProcessContexts(contexts, processes);

        Assert.Equal("ctx-a", map[100]);
        Assert.Equal("ctx-b", map[200]);
    }

    [Fact]
    public void Process_context_target_resolver_prefers_configured_parent_target()
    {
        var contexts = new DevwtContextState([
            Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", "repo-a", @"C:\work\b", "127.0.0.1")
        ]);
        var processes = new[]
        {
            new ProcessObservation(200, null, @"C:\tools\agent.exe", null, null),
            new ProcessObservation(300, 200, @"C:\tools\playwright.exe", null, @"C:\work\b")
        };
        var routing = new DevwtRoutingState(
            ExplicitLinkMaps: [],
            ActiveTarget: null,
            ProcessTargets: [new DevwtProcessTarget(200, "ctx-a")]);

        var contextId = ProcessContextTargetResolver.ResolveConfiguredTarget(300, contexts, processes, routing);

        Assert.Equal("ctx-a", contextId);
    }

    [Fact]
    public void Process_port_target_overrides_process_wide_target_through_parent_chain()
    {
        var contexts = new DevwtContextState([
            Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", "repo-a", @"C:\work\b", "127.0.0.1")
        ]);
        var processes = new[]
        {
            new ProcessObservation(100, null, @"C:\tools\codex.exe", null, null),
            new ProcessObservation(200, 100, @"C:\tools\node.exe", null, null)
        };
        var routing = new DevwtRoutingState([], null, ProcessTargets: [new DevwtProcessTarget(100, "ctx-a")])
        {
            ProcessPortTargets = [new DevwtProcessPortTarget(100, "ctx-b", 44334, "auto")]
        };

        Assert.Equal("ctx-b", ProcessContextTargetResolver.ResolveConfiguredTarget(200, 44334, contexts, processes, routing));
        Assert.Equal("ctx-a", ProcessContextTargetResolver.ResolveConfiguredTarget(200, 5001, contexts, processes, routing));
    }

    [Fact]
    public void Browser_scoped_proxy_target_wins_before_global_active_target()
    {
        var first = new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5025, "127.80.1.10", 5025, 10);
        var second = new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 5025, "127.80.1.11", 5025, 11);
        var table = GatewayRouteTable.FromRoutes(
            [first, second],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.80.1.10"),
                Context("ctx-b", "repo-b", @"C:\work\b", "127.80.1.11")
            ]),
            new DevwtRoutingState(
                [],
                new DevwtActiveTarget("ctx-a", 5025, "auto"),
                [new DevwtBrowserActiveTarget(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "ctx-b", 5025, "auto")]));

        var browserRoute = table.ResolveWithoutCaller(
            5025,
            requestContextId: null,
            cookieContextId: null,
            browserKey: @"c:\program files\google\chrome\application\CHROME.EXE",
            includeActiveTarget: true);
        var fallbackRoute = table.ResolveWithoutCaller(
            5025,
            requestContextId: null,
            cookieContextId: null,
            browserKey: @"C:\Program Files\Mozilla Firefox\firefox.exe",
            includeActiveTarget: true);

        Assert.NotNull(browserRoute);
        Assert.Equal("ctx-b", browserRoute.ContextId);
        Assert.NotNull(fallbackRoute);
        Assert.Equal("ctx-a", fallbackRoute.ContextId);
    }

    [Fact]
    public void Application_proxy_target_wins_before_newest_route()
    {
        var first = new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5025, "127.0.0.1", 54001, 10);
        var second = new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 5025, "127.0.0.1", 55001, 11);
        var table = GatewayRouteTable.FromRoutes(
            [first, second],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
                Context("ctx-b", "repo-b", @"C:\work\b", "127.0.0.1")
            ]),
            new DevwtRoutingState(
                [],
                null,
                ApplicationTargets: [new DevwtApplicationTarget(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "ctx-a", 5025, "auto")]));

        var route = table.ResolveApplicationTarget(
            5025,
            @"c:\program files\google\chrome\application\CHROME.EXE");

        Assert.NotNull(route);
        Assert.Equal("ctx-a", route.ContextId);
    }

    [Fact]
    public void Session_port_target_overrides_session_wide_target()
    {
        var table = GatewayRouteTable.FromRoutes(
            [
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 24434, 10),
                new GatewayRoute("ctx-b", "repo-a", @"C:\work\b", 44334, "127.0.0.1", 34434, 20)
            ],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
                Context("ctx-b", "repo-a", @"C:\work\b", "127.0.0.1")
            ]),
            new DevwtRoutingState([], null)
            {
                SessionContextTargets = [new DevwtSessionContextTarget("codex:a", "ctx-a")],
                SessionPortTargets = [new DevwtSessionPortTarget("codex:a", "ctx-b", 44334, "auto")]
            });

        Assert.Equal("ctx-b", table.ResolveSessionTarget(44334, "CODEX:A")!.ContextId);
    }

    [Fact]
    public void Application_target_uses_port_override_then_image_wide_target()
    {
        var routing = new DevwtRoutingState(
            [],
            null,
            ApplicationTargets: [new DevwtApplicationTarget(@"C:\tools\codex.exe", "ctx-b", 44334, "auto")])
        {
            ApplicationContextTargets = [new DevwtApplicationContextTarget(@"C:\tools\codex.exe", "ctx-a")]
        };
        var table = GatewayRouteTable.FromRoutes(
            [
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 24434, 10),
                new GatewayRoute("ctx-b", "repo-a", @"C:\work\b", 44334, "127.0.0.1", 34434, 20),
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5001, "127.0.0.1", 25001, 10),
                new GatewayRoute("ctx-b", "repo-a", @"C:\work\b", 5001, "127.0.0.1", 35001, 20)
            ],
            DevwtRepositoryState.Empty,
            new DevwtContextState([
                Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
                Context("ctx-b", "repo-a", @"C:\work\b", "127.0.0.1")
            ]),
            routing);

        Assert.Equal("ctx-b", table.ResolveApplicationTarget(44334, @"c:\TOOLS\codex.exe")!.ContextId);
        Assert.Equal("ctx-a", table.ResolveApplicationTarget(5001, @"c:\TOOLS\codex.exe")!.ContextId);
    }

    [Fact]
    public void Control_handler_sets_and_clears_browser_scoped_active_targets_without_replacing_global_target()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.80.1.10"),
            Context("ctx-b", repository.Id, @"C:\work\b", "127.80.1.11")
        ]));
        store.SaveRouting(new DevwtRoutingState([], null)
        {
            ActiveTargetMode = DevwtActiveTargetMode.GlobalContext,
            GlobalActiveContextId = "ctx-a",
            PortActiveTargets = [new DevwtPortActiveTarget("ctx-a", 5025)]
        });
        var handler = new DevwtControlHandler(new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()), store);

        var set = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetActiveTarget,
            ActiveTarget: new DevwtActiveTarget("ctx-b", 5025, "auto"),
            ActiveTargetBrowserKey: @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
        var afterSet = store.LoadRouting();

        Assert.Equal(0, set.ExitCode);
        Assert.Equal(DevwtActiveTargetMode.GlobalContext, afterSet.ActiveTargetMode);
        Assert.Equal("ctx-a", afterSet.GlobalActiveContextId);
        Assert.Equal("ctx-a", Assert.Single(afterSet.PortActiveTargets).ContextId);
        var browserTarget = Assert.Single(afterSet.BrowserActiveTargets);
        Assert.Equal("ctx-b", browserTarget.ContextId);
        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", browserTarget.BrowserKey);

        var clear = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetActiveTarget,
            ClearActiveTarget: true,
            ActiveTargetBrowserKey: @"c:\program files\google\chrome\application\CHROME.EXE"));

        Assert.Equal(0, clear.ExitCode);
        var afterClear = store.LoadRouting();
        Assert.Equal(DevwtActiveTargetMode.GlobalContext, afterClear.ActiveTargetMode);
        Assert.Equal("ctx-a", afterClear.GlobalActiveContextId);
        Assert.Equal("ctx-a", Assert.Single(afterClear.PortActiveTargets).ContextId);
        Assert.Empty(afterClear.BrowserActiveTargets);
    }

    [Fact]
    public void Control_handler_updates_and_clears_one_port_without_affecting_other_ports()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1")
        ]));
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store);

        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetActiveTarget,
            ActiveTarget: new DevwtActiveTarget("ctx-a", 44334, "auto")));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetActiveTarget,
            ActiveTarget: new DevwtActiveTarget("ctx-b", 5001, "auto")));
        var clear = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetActiveTarget,
            ClearActiveTarget: true,
            Port: 44334));

        Assert.Equal(0, clear.ExitCode);
        var routing = store.LoadRouting();
        Assert.Equal(DevwtActiveTargetMode.PerPort, routing.ActiveTargetMode);
        Assert.Equal(new DevwtPortActiveTarget("ctx-b", 5001), Assert.Single(routing.PortActiveTargets));
    }

    [Fact]
    public void Control_handler_switches_to_global_context_without_discarding_port_targets()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1")
        ]));
        store.SaveRouting(new DevwtRoutingState([], null)
        {
            PortActiveTargets = [new DevwtPortActiveTarget("ctx-a", 44334)]
        });
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store);

        var result = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetActiveTarget,
            ActiveTargetMode: DevwtActiveTargetMode.GlobalContext,
            GlobalActiveContextId: "ctx-b"));

        Assert.Equal(0, result.ExitCode);
        var routing = store.LoadRouting();
        Assert.Equal(DevwtActiveTargetMode.GlobalContext, routing.ActiveTargetMode);
        Assert.Equal("ctx-b", routing.GlobalActiveContextId);
        Assert.Equal(new DevwtPortActiveTarget("ctx-a", 44334), Assert.Single(routing.PortActiveTargets));
    }

    [Fact]
    public void Control_handler_sets_and_clears_process_targets()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1")
        ]));
        store.SaveRouting(new DevwtRoutingState([], new DevwtActiveTarget("ctx-b", 5025, "auto")));
        var handler = new DevwtControlHandler(new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()), store);

        var set = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetProcessTarget,
            ProcessTarget: new DevwtProcessTarget(1234, "ctx-a")));

        Assert.Equal(0, set.ExitCode);
        var afterSet = store.LoadRouting();
        Assert.Equal("ctx-b", Assert.Single(afterSet.PortActiveTargets).ContextId);
        var processTarget = Assert.Single(afterSet.ProcessTargets);
        Assert.Equal(1234, processTarget.ProcessId);
        Assert.Equal("ctx-a", processTarget.ContextId);

        var clear = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetProcessTarget,
            ProcessId: 1234,
            ClearProcessTarget: true));

        Assert.Equal(0, clear.ExitCode);
        Assert.Empty(store.LoadRouting().ProcessTargets);
    }

    [Fact]
    public void Control_handler_sets_and_clears_application_targets()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1")
        ]));
        var handler = new DevwtControlHandler(new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()), store);

        var set = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetApplicationTarget,
            ApplicationTarget: new DevwtApplicationTarget(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "ctx-a", 44334, "auto")));

        Assert.Equal(0, set.ExitCode);
        var target = Assert.Single(store.LoadRouting().ApplicationTargets);
        Assert.Equal("ctx-a", target.ContextId);
        Assert.Equal(44334, target.Port);

        var clear = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetApplicationTarget,
            ClearApplicationTarget: true,
            ApplicationTargetKey: @"c:\program files\google\chrome\application\CHROME.EXE",
            Port: 44334));

        Assert.Equal(0, clear.ExitCode);
        Assert.Empty(store.LoadRouting().ApplicationTargets);
    }

    [Fact]
    public void Control_handler_sets_and_clears_scoped_targets_independently()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1")
        ]));
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store);

        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetProcessTarget,
            ProcessPortTarget: new DevwtProcessPortTarget(1200, "ctx-a", 44334, "auto")));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetProcessTarget,
            ProcessPortTarget: new DevwtProcessPortTarget(1200, "ctx-b", 5001, "auto")));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetApplicationTarget,
            ApplicationContextTarget: new DevwtApplicationContextTarget(@"C:\tools\codex.exe", "ctx-a")));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetApplicationTarget,
            ApplicationContextTarget: new DevwtApplicationContextTarget(@"C:\tools\rider.exe", "ctx-b")));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetSessionTarget,
            SessionContextTarget: new DevwtSessionContextTarget("codex:a", "ctx-a")));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetSessionTarget,
            SessionContextTarget: new DevwtSessionContextTarget("codex:b", "ctx-b")));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetSessionTarget,
            SessionPortTarget: new DevwtSessionPortTarget("codex:a", "ctx-a", 44334, "auto")));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetSessionTarget,
            SessionPortTarget: new DevwtSessionPortTarget("codex:a", "ctx-b", 5001, "auto")));

        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetProcessTarget,
            ClearProcessPortTarget: true,
            ProcessId: 1200,
            Port: 44334));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetApplicationTarget,
            ClearApplicationContextTarget: true,
            ApplicationTargetKey: @"C:\TOOLS\CODEX.EXE"));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetSessionTarget,
            ClearSessionContextTarget: true,
            SessionId: "CODEX:A"));
        handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetSessionTarget,
            ClearSessionPortTarget: true,
            SessionId: "CODEX:A",
            Port: 44334));

        var routing = store.LoadRouting();
        Assert.Equal(5001, Assert.Single(routing.ProcessPortTargets).Port);
        Assert.Equal(@"C:\tools\rider.exe", Assert.Single(routing.ApplicationContextTargets).ApplicationKey);
        Assert.Equal("codex:b", Assert.Single(routing.SessionContextTargets).SessionId);
        Assert.Equal(5001, Assert.Single(routing.SessionPortTargets).Port);
    }

    [Fact]
    public void Control_handler_rejects_invalid_scoped_target_without_mutating_state()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1")
        ]));
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store);

        var results = new[]
        {
            handler.Handle(new DevwtControlRequest(
                DevwtControlOperation.SetSessionTarget,
                SessionPortTarget: new DevwtSessionPortTarget("", "ctx-a", 44334, "auto"))),
            handler.Handle(new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessPortTarget: new DevwtProcessPortTarget(0, "ctx-a", 44334, "auto"))),
            handler.Handle(new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessPortTarget: new DevwtProcessPortTarget(10, "ctx-a", 70000, "auto"))),
            handler.Handle(new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessPortTarget: new DevwtProcessPortTarget(10, "ctx-a", 44334, "ftp"))),
            handler.Handle(new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessPortTarget: new DevwtProcessPortTarget(10, "missing", 44334, "auto")))
        };

        Assert.All(results, result => Assert.Equal(2, result.ExitCode));
        Assert.Empty(store.LoadRouting().ProcessPortTargets);
        Assert.Empty(store.LoadRouting().SessionPortTargets);
    }

    [Fact]
    public void Control_handler_serializes_concurrent_scoped_updates()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1")
        ]));
        var handlers = Enumerable.Range(0, 2)
            .Select(_ => new DevwtControlHandler(
                new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
                store))
            .ToArray();
        var results = new System.Collections.Concurrent.ConcurrentBag<DevwtCommandResult>();

        Parallel.For(1, 33, processId =>
        {
            results.Add(handlers[processId % handlers.Length].Handle(new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessPortTarget: new DevwtProcessPortTarget(processId, "ctx-a", 44334, "auto"))));
        });

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal(32, store.LoadRouting().ProcessPortTargets.Count);
    }

    [Fact]
    public void Web_ui_action_mapper_builds_scoped_target_requests()
    {
        var processWide = DevwtWebUiActionMapper.Map(new DevwtWebUiAction(
            "set-process-target",
            ContextId: "ctx-process",
            ProcessId: 101));
        var processPort = DevwtWebUiActionMapper.Map(new DevwtWebUiAction(
            "set-process-port-target",
            ContextId: "ctx-process-port",
            Port: 44334,
            Scheme: "https",
            ProcessId: 101));
        var imageWide = DevwtWebUiActionMapper.Map(new DevwtWebUiAction(
            "set-image-context-target",
            ContextId: "ctx-image",
            ApplicationKey: "codex.exe"));
        var sessionWide = DevwtWebUiActionMapper.Map(new DevwtWebUiAction(
            "set-session-context-target",
            ContextId: "ctx-session",
            SessionId: "codex:thread-a"));
        var sessionPort = DevwtWebUiActionMapper.Map(new DevwtWebUiAction(
            "set-session-port-target",
            ContextId: "ctx-session-port",
            Port: 5001,
            SessionId: "codex:thread-a"));

        Assert.Equal(new DevwtProcessTarget(101, "ctx-process"), processWide.ProcessTarget);
        Assert.Equal(
            new DevwtProcessPortTarget(101, "ctx-process-port", 44334, "https"),
            processPort.ProcessPortTarget);
        Assert.Equal(
            new DevwtApplicationContextTarget("codex.exe", "ctx-image"),
            imageWide.ApplicationContextTarget);
        Assert.Equal(
            new DevwtSessionContextTarget("codex:thread-a", "ctx-session"),
            sessionWide.SessionContextTarget);
        Assert.Equal(
            new DevwtSessionPortTarget("codex:thread-a", "ctx-session-port", 5001, "auto"),
            sessionPort.SessionPortTarget);
    }

    [Fact]
    public void Web_ui_action_mapper_builds_management_requests()
    {
        var addRepository = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "add-repository",
            RepositoryName: "volo",
            WorktreePath: @"D:\GitHub\volo",
            LinkedRepositories: [new LinkedRepositoryInput("abp", @"D:\GitHub\abp")]));
        var pauseRepository = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "pause-repository",
            RepositoryName: "repo-volo"));
        var resumeRepository = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "resume-repository",
            RepositoryName: "repo-volo"));
        var portCheck = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "check-port",
            WorktreePath: @"D:\GitHub\volo",
            ContextId: "ctx-volo",
            Port: 44334));
        var ideWatch = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "add-ide-watch",
            IdeWatchName: "Rider",
            IdeWatchSelectorKind: "path",
            IdeWatchSelectorValue: @"C:\Tools\rider64.exe"));
        var forceKill = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "kill-proxy-child",
            ContextId: "ctx-volo",
            Port: 44334,
            Protocol: "tcp"));
        var browserFallback = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "set-browser-fallback-on-missing-port",
            BrowserFallbackOnMissingPort: true));
        var worktreeFallback = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "set-browser-missing-port-policy",
            ContextId: "ctx-volo",
            Port: 44373,
            BrowserMissingPortPolicyMode: DevwtBrowserMissingPortPolicyMode.Automatic));
        var clearWorktreeFallback = DevwtWebUiActionMapper.MapManagement(new DevwtWebUiAction(
            "clear-browser-missing-port-policy",
            ContextId: "ctx-volo",
            Port: 44373));

        Assert.Equal(DevwtControlOperation.AddRepository, addRepository.Operation);
        Assert.Equal(@"D:\GitHub\volo", addRepository.AddRepository?.WorkingDirectory);
        Assert.Equal("volo", addRepository.AddRepository?.Name);
        Assert.Equal(
            new LinkedRepositoryInput("abp", @"D:\GitHub\abp"),
            Assert.Single(addRepository.AddRepository!.LinkedRepositories));
        Assert.Equal(DevwtControlOperation.Pause, pauseRepository.Operation);
        Assert.Equal("repo-volo", pauseRepository.RepositoryName);
        Assert.Equal(DevwtControlOperation.Resume, resumeRepository.Operation);
        Assert.Equal("repo-volo", resumeRepository.RepositoryName);
        Assert.Equal(
            new DevwtPortQuery(44334, @"D:\GitHub\volo", "ctx-volo"),
            portCheck.PortQuery);
        Assert.Equal(
            new DevwtIdeWatch("Rider", ImagePath: @"C:\Tools\rider64.exe"),
            ideWatch.IdeWatch);
        Assert.Equal(
            new DevwtProxyChildTarget("ctx-volo", 44334, GatewayRouteProtocol.Tcp, Force: true),
            forceKill.ProxyChildTarget);
        Assert.Equal(DevwtControlOperation.SetBrowserFallbackOnMissingPort, browserFallback.Operation);
        Assert.True(browserFallback.BrowserFallbackOnMissingPort);
        Assert.Equal(
            new DevwtBrowserMissingPortPolicy(
                "ctx-volo",
                44373,
                DevwtBrowserMissingPortPolicyMode.Automatic),
            worktreeFallback.BrowserMissingPortPolicy);
        Assert.True(clearWorktreeFallback.ClearBrowserMissingPortPolicy);
    }

    [Theory]
    [InlineData("clear-process-target", DevwtControlOperation.SetProcessTarget)]
    [InlineData("clear-process-port-target", DevwtControlOperation.SetProcessTarget)]
    [InlineData("clear-image-context-target", DevwtControlOperation.SetApplicationTarget)]
    [InlineData("clear-application-target", DevwtControlOperation.SetApplicationTarget)]
    [InlineData("clear-session-context-target", DevwtControlOperation.SetSessionTarget)]
    [InlineData("clear-session-port-target", DevwtControlOperation.SetSessionTarget)]
    public void Web_ui_action_mapper_maps_scoped_clear_actions(
        string actionName,
        DevwtControlOperation expectedOperation)
    {
        var action = new DevwtWebUiAction(
            actionName,
            Port: 44334,
            ProcessId: 101,
            SessionId: "codex:thread-a",
            ApplicationKey: "codex.exe");

        var request = DevwtWebUiActionMapper.Map(action);

        Assert.Equal(expectedOperation, request.Operation);
        Assert.True(
            request.ClearProcessTarget
            || request.ClearProcessPortTarget
            || request.ClearApplicationContextTarget
            || request.ClearApplicationTarget
            || request.ClearSessionContextTarget
            || request.ClearSessionPortTarget);
    }

    [Fact]
    public void Control_handler_sets_and_removes_session_rules()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store);
        var rule = new DevwtSessionRule(
            Name: "Codex",
            Match: new DevwtSessionMatch(ProcessName: "codex-code-mode-host.exe"),
            Identity: new DevwtSessionIdentity(
                DevwtSessionIdentityKind.EnvironmentVariable,
                Value: "CODEX_THREAD_ID",
                Prefix: "codex:"));

        var set = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetSessionRule,
            SessionRule: rule));

        Assert.Equal(0, set.ExitCode);
        Assert.Equal("Codex", Assert.Single(store.LoadRuntimeSettings().SessionRules).Name);

        var remove = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.RemoveSessionRule,
            SessionRuleName: "Codex"));

        Assert.Equal(0, remove.ExitCode);
        Assert.Empty(store.LoadRuntimeSettings().SessionRules);
    }

    [Fact]
    public void Control_handler_sets_browser_missing_port_fallback()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var handler = new DevwtControlHandler(
            new DevwtManager(
                store,
                new FakeGitInspector(new GitRepositoryInfo(@"C:\work\volo", @"C:\work\volo\.git", [])),
                new RecordingHookRuntimeConfigurator()),
            store);

        var enable = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetBrowserFallbackOnMissingPort,
            BrowserFallbackOnMissingPort: true));
        Assert.Equal(0, enable.ExitCode);
        Assert.True(store.LoadRuntimeSettings().BrowserFallbackOnMissingPort);

        var disable = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetBrowserFallbackOnMissingPort,
            BrowserFallbackOnMissingPort: false));
        Assert.Equal(0, disable.ExitCode);
        Assert.False(store.LoadRuntimeSettings().BrowserFallbackOnMissingPort);
    }

    [Fact]
    public void Control_handler_sets_worktree_scoped_browser_missing_port_policies()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        store.SaveRepositories(new DevwtRepositoryState([
            new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", [])
        ]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-active", "repo-a", @"C:\work\active", "127.0.0.1"),
            Context("ctx-provider", "repo-a", @"C:\work\provider", "127.0.0.1"),
            Context("ctx-other", "repo-other", @"C:\work\other", "127.0.0.1")
        ]));
        var handler = new DevwtControlHandler(
            new DevwtManager(
                store,
                new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])),
                new RecordingHookRuntimeConfigurator()),
            store);

        var automatic = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetBrowserMissingPortPolicy,
            BrowserMissingPortPolicy: new DevwtBrowserMissingPortPolicy(
                "ctx-active",
                44373,
                DevwtBrowserMissingPortPolicyMode.Automatic)));
        Assert.Equal(0, automatic.ExitCode);
        Assert.Equal(
            DevwtBrowserMissingPortPolicyMode.Automatic,
            Assert.Single(store.LoadRuntimeSettings().BrowserMissingPortPolicies).Mode);

        var redirect = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetBrowserMissingPortPolicy,
            BrowserMissingPortPolicy: new DevwtBrowserMissingPortPolicy(
                "ctx-active",
                44373,
                DevwtBrowserMissingPortPolicyMode.Redirect,
                "ctx-provider")));
        Assert.Equal(0, redirect.ExitCode);
        Assert.Equal(
            "ctx-provider",
            Assert.Single(store.LoadRuntimeSettings().BrowserMissingPortPolicies).TargetContextId);

        var crossRepository = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetBrowserMissingPortPolicy,
            BrowserMissingPortPolicy: new DevwtBrowserMissingPortPolicy(
                "ctx-active",
                44373,
                DevwtBrowserMissingPortPolicyMode.Redirect,
                "ctx-other")));
        Assert.Equal(2, crossRepository.ExitCode);

        var clear = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetBrowserMissingPortPolicy,
            BrowserMissingPortPolicy: new DevwtBrowserMissingPortPolicy(
                "ctx-active",
                44373,
                DevwtBrowserMissingPortPolicyMode.Disabled),
            ClearBrowserMissingPortPolicy: true));
        Assert.Equal(0, clear.ExitCode);
        Assert.Empty(store.LoadRuntimeSettings().BrowserMissingPortPolicies);
    }

    [Fact]
    public void Control_handler_accepts_environment_only_session_rules()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store);
        var rule = new DevwtSessionRule(
            Name: "Codex",
            Match: new DevwtSessionMatch(EnvironmentVariable: "CODEX_THREAD_ID"),
            Identity: new DevwtSessionIdentity(
                DevwtSessionIdentityKind.EnvironmentVariable,
                Value: "CODEX_THREAD_ID",
                Prefix: "codex:"));

        var result = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.SetSessionRule,
            SessionRule: rule));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Codex", Assert.Single(store.LoadRuntimeSettings().SessionRules).Name);
    }

    [Fact]
    public void Control_handler_stops_proxy_child_listener_process_without_killing_gateway()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 }
        ]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            "ctx-a\t44334\t127.0.0.1\t55297\t42"
        ]);
        var processController = new RecordingProcessController();
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store,
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([new ListenerObservation(42, "127.0.0.1", 55297)])),
            processController);

        var stop = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.StopProxyChild,
            ProxyChildTarget: new DevwtProxyChildTarget("ctx-a", 44334, GatewayRouteProtocol.Tcp, Force: false)));
        var kill = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.StopProxyChild,
            ProxyChildTarget: new DevwtProxyChildTarget("ctx-a", 44334, GatewayRouteProtocol.Tcp, Force: true)));

        Assert.Equal(0, stop.ExitCode);
        Assert.Equal(0, kill.ExitCode);
        Assert.Equal([(42, false), (42, true)], processController.Requests);
    }

    [Fact]
    public void Control_handler_requires_context_when_proxy_child_port_is_ambiguous()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1") with { AssignedPortBase = 25000 }
        ]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            "ctx-a\t44334\t127.0.0.1\t55297\t42",
            "ctx-b\t44334\t127.0.0.1\t55298\t43"
        ]);
        var processController = new RecordingProcessController();
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store,
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(42, "127.0.0.1", 55297),
                new ListenerObservation(43, "127.0.0.1", 55298)
            ])),
            processController);

        var result = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.StopProxyChild,
            ProxyChildTarget: new DevwtProxyChildTarget(null, 44334, GatewayRouteProtocol.Tcp, Force: true)));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Ambiguous proxy child target", result.Output, StringComparison.Ordinal);
        Assert.Empty(processController.Requests);
    }

    [Fact]
    public void Port_queries_find_live_backend_processes_in_auto_or_explicit_context()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { Description = "Driver review" },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1")
        ]));
        var processId = Environment.ProcessId;
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t44334\t127.0.0.1\t55297\t{processId}",
            $"ctx-a\t44334\t127.0.0.1\t55298\t{processId}\tudp",
            $"ctx-a\t44334\t127.0.0.1\t55300\t{processId}",
            $"ctx-b\t44334\t127.0.0.1\t55299\t{processId}"
        ]);
        var handler = new DevwtControlHandler(
            new DevwtManager(store, new FakeGitInspector(new GitRepositoryInfo(@"C:\work\a", @"C:\work\a\.git", [])), new RecordingHookRuntimeConfigurator()),
            store,
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(processId, "127.0.0.1", 55297),
                new ListenerObservation(processId, "127.0.0.1", 55298, GatewayRouteProtocol.Udp),
                new ListenerObservation(processId, "127.0.0.1", 55299)
            ])));

        var process = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.FindPortProcesses,
            PortQuery: new DevwtPortQuery(44334, @"C:\work\a\src", ContextId: null)));
        var check = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.CheckPort,
            PortQuery: new DevwtPortQuery(44334, @"C:\unrelated", "ctx-b")));
        var absent = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.CheckPort,
            PortQuery: new DevwtPortQuery(5001, @"C:\work\a", ContextId: null)));
        var unregistered = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.CheckPort,
            PortQuery: new DevwtPortQuery(44334, @"C:\unrelated", ContextId: null)));

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("\"Driver review\" (ctx-a)", process.Output, StringComparison.Ordinal);
        Assert.Contains($"PID {processId}", process.Output, StringComparison.Ordinal);
        Assert.Contains("tcp 127.0.0.1:44334 -> 127.0.0.1:55297", process.Output, StringComparison.Ordinal);
        Assert.Contains("udp 127.0.0.1:44334 -> 127.0.0.1:55298", process.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("55299", process.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("55300", process.Output, StringComparison.Ordinal);
        Assert.Equal(0, check.ExitCode);
        Assert.Contains("(ctx-b)", check.Output, StringComparison.Ordinal);
        Assert.Equal(1, absent.ExitCode);
        Assert.Contains("no application is listening", absent.Output, StringComparison.Ordinal);
        Assert.Equal(2, unregistered.ExitCode);
        Assert.Contains("not inside a registered DevWT context", unregistered.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_key_resolver_uses_tcp_owner_process_image()
    {
        var browserKey = DevwtBrowserKeyResolver.ResolveBrowserKey(
            new FixedActiveTcpConnectionSource(42),
            new FixedProcessObservationSource([
                new ProcessObservation(42, null, @"C:\Program Files\Google\Chrome\Application\chrome.exe", null, null)
            ]),
            new IPEndPoint(IPAddress.Loopback, 51000),
            new IPEndPoint(IPAddress.Loopback, 17776));

        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", browserKey);
    }

    [Fact]
    public void Gateway_detects_tls_client_hello_before_routing_https()
    {
        Assert.True(DevwtGatewayProtocol.IsTlsClientHello([0x16, 0x03, 0x03, 0x00, 0x7a]));
        Assert.False(DevwtGatewayProtocol.IsTlsClientHello(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n")));
    }

    [Fact]
    public void Gateway_distinguishes_http1_http2_and_raw_tcp_prefixes()
    {
        var http2Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

        Assert.True(DevwtGatewayHttpHeaders.LooksLikeHttpRequest("G"u8));
        Assert.True(DevwtGatewayHttpHeaders.IsHttp1Request("GET / HTTP/1.1\r\n"u8));
        Assert.True(DevwtGatewayProtocol.CouldBeHttp2ConnectionPreface(http2Preface.AsSpan(0, 10)));
        Assert.True(DevwtGatewayProtocol.IsHttp2ConnectionPreface(http2Preface));
        Assert.False(DevwtGatewayHttpHeaders.LooksLikeHttpRequest("SSH-2.0"u8));
        Assert.False(DevwtGatewayProtocol.CouldBeHttp2ConnectionPreface("SSH-2.0"u8));
    }

    [Fact]
    public void Gateway_forwarder_request_configs_have_no_activity_timeout()
    {
        var proxyHostType = DevwtYarpProxyHostType();

        foreach (var fieldName in new[]
                 {
                     "HttpRequestConfig",
                     "HttpsRequestConfig",
                     "Http2PriorKnowledgeRequestConfig"
                 })
        {
            var field = Assert.IsAssignableFrom<FieldInfo>(
                proxyHostType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static));
            var config = Assert.IsType<ForwarderRequestConfig>(field.GetValue(null));

            Assert.Equal(Timeout.InfiniteTimeSpan, config.ActivityTimeout);
        }
    }

    [Fact]
    public void Gateway_backend_http_handler_has_no_connect_timeout()
    {
        var proxyHostType = DevwtYarpProxyHostType();
        var clientKeyType = Assert.IsAssignableFrom<Type>(
            proxyHostType.GetNestedType("BackendClientKey", BindingFlags.NonPublic));
        var clientKey = Activator.CreateInstance(
            clientKeyType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [IPAddress.Loopback, 44334, false],
            culture: null);
        var proxyHost = RuntimeHelpers.GetUninitializedObject(proxyHostType);
        var createHttpClient = Assert.IsAssignableFrom<MethodInfo>(
            proxyHostType.GetMethod("CreateHttpClient", BindingFlags.Instance | BindingFlags.NonPublic));
        using var client = Assert.IsType<HttpMessageInvoker>(
            createHttpClient.Invoke(proxyHost, [clientKey]));
        var handlerField = Assert.Single(
            typeof(HttpMessageInvoker)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(HttpMessageHandler).IsAssignableFrom(field.FieldType));
        var handler = Assert.IsType<SocketsHttpHandler>(handlerField.GetValue(client));

        Assert.Equal(Timeout.InfiniteTimeSpan, handler.ConnectTimeout);
    }

    [Fact]
    public async Task Gateway_inspects_cleartext_http2_prior_knowledge_with_yarp()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var backendPort = FreeTcpPort();
        while (backendPort == port)
        {
            backendPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1")]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t127.0.0.1\t{port}\t127.0.0.1\t{backendPort}\t10\ttcp"
        ]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var backend = await StartHttp2BackendAsync(backendPort, timeout.Token);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", backendPort)
            ])),
            new NullActiveTcpConnectionSource(),
            new EmptyProcessObservationSource(),
            store);
        using var gatewayCancellation = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayCancellation.Token);
        await WaitForPortAsync(port);

        try
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var response = await client.SendAsync(request, timeout.Token);

            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("h2c", await response.Content.ReadAsStringAsync(timeout.Token));
        }
        finally
        {
            gatewayCancellation.Cancel();
            await gatewayTask;
        }
    }

    [Fact]
    public async Task Gateway_raw_mode_does_not_treat_http_looking_tcp_payload_as_http()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var backendPort = FreeTcpPort();
        while (backendPort == port)
        {
            backendPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1")]));
        store.SaveRouting(new DevwtRoutingState([], null)
        {
            HttpsProxyEndpoints = [new("127.0.0.1", port, DevwtHttpsProxyMode.Raw)]
        });
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t127.0.0.1\t{port}\t127.0.0.1\t{backendPort}\t10\ttcp"
        ]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backendListener = new TcpListener(IPAddress.Loopback, backendPort);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", backendPort)
            ])),
            new NullActiveTcpConnectionSource(),
            new EmptyProcessObservationSource(),
            store);
        using var gatewayCancellation = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayCancellation.Token);
        await WaitForPortAsync(port);
        await Task.Delay(100, timeout.Token);
        backendListener.Start();
        var backendTask = AcceptHttpRequestOnceAsync(backendListener, "raw", timeout.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\nHost: localhost\r\nX-DevWT-Context: must-remain\r\n\r\n"), timeout.Token);
            Assert.Equal("raw", await ReadHttpResponseAsync(stream, returnRawResponse: false, timeout.Token));
        }
        finally
        {
            gatewayCancellation.Cancel();
            backendListener.Stop();
            await gatewayTask;
        }

        Assert.Contains("X-DevWT-Context: must-remain", await backendTask, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gateway_inspects_http2_header_routes_the_selected_tab_context_and_reencrypts_upstream()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var firstTargetPort = FreeTcpPort();
        var secondTargetPort = FreeTcpPort();
        while (firstTargetPort == port)
        {
            firstTargetPort = FreeTcpPort();
        }

        while (secondTargetPort == port || secondTargetPort == firstTargetPort)
        {
            secondTargetPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1"),
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1")
        ]));
        store.SaveRouting(new DevwtRoutingState([], null)
        {
            PortActiveTargets = [new DevwtPortActiveTarget("ctx-b", port)],
            HttpsProxyEndpoints = [new DevwtHttpsProxyEndpoint("127.0.0.1", port, DevwtHttpsProxyMode.Inspect)]
        });
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t127.0.0.1\t{port}\t127.0.0.1\t{firstTargetPort}\t10\ttcp",
            $"ctx-b\t127.0.0.1\t{port}\t127.0.0.1\t{secondTargetPort}\t20\ttcp"
        ]);

        var certificateStore = new DevwtGatewayCertificateStore(temp.Path);
        using var certificate = certificateStore.GetOrCreateServerCertificate();
        using var firstTargetListener = new TcpListener(IPAddress.Loopback, firstTargetPort);
        using var secondTargetListener = new TcpListener(IPAddress.Loopback, secondTargetPort);
        firstTargetListener.Start();
        secondTargetListener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstTargetTask = AcceptTlsHttpOnceAsync(firstTargetListener, certificate, "ctx-a", timeout.Token);
        var secondTargetTask = AcceptTlsHttpOnceAsync(secondTargetListener, certificate, "ctx-b", timeout.Token);
        var history = new DevwtConnectionHistory();

        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", firstTargetPort),
                new ListenerObservation(20, "127.0.0.1", secondTargetPort)
            ])),
            new NullActiveTcpConnectionSource(),
            new EmptyProcessObservationSource(),
            store,
            connectionHistory: history,
            certificateStore: certificateStore);
        using var gatewayCancellation = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayCancellation.Token);
        await WaitForPortAsync(port);

        (Version Version, string Context, string Body) firstResponse;
        (Version Version, string Context, string Body) secondResponse;
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                MaxConnectionsPerServer = 1,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
            using var client = new HttpClient(handler);
            async Task<(Version Version, string Context, string Body)> SendAsync(string contextId)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/")
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request.Headers.Add("X-DevWT-Context", contextId);
                request.Headers.Add("X-DevWT-Tab", contextId == "ctx-a" ? "7" : "8");
                request.Headers.Add("X-DevWT-Token", "token");
                request.Headers.Add("X-DevWT-Allow-Fallback", "1");
                request.Headers.Add("Cookie", $"application-cookie=keep; devwt-context={contextId}");
                using var response = await client.SendAsync(request, timeout.Token);
                return (
                    response.Version,
                    Assert.Single(response.Headers.GetValues("X-DevWT-Context")),
                    await response.Content.ReadAsStringAsync(timeout.Token));
            }

            var responses = await Task.WhenAll(SendAsync("ctx-a"), SendAsync("ctx-b"));
            firstResponse = responses.Single(response => response.Context == "ctx-a");
            secondResponse = responses.Single(response => response.Context == "ctx-b");
        }
        finally
        {
            gatewayCancellation.Cancel();
            await gatewayTask;
            timeout.Cancel();
            try
            {
                await Task.WhenAll(firstTargetTask, secondTargetTask);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or AuthenticationException)
            {
            }
        }

        var firstForwardedRequest = await firstTargetTask;
        var secondForwardedRequest = await secondTargetTask;
        Assert.Equal(HttpVersion.Version20, firstResponse.Version);
        Assert.Equal(HttpVersion.Version20, secondResponse.Version);
        Assert.StartsWith("GET / HTTP/1.1", firstForwardedRequest, StringComparison.Ordinal);
        Assert.StartsWith("GET / HTTP/1.1", secondForwardedRequest, StringComparison.Ordinal);
        Assert.Contains($"Host: 127.0.0.1:{port}", firstForwardedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Host: 127.0.0.1:{port}", secondForwardedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-DevWT-", firstForwardedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-DevWT-", secondForwardedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("application-cookie=keep", firstForwardedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("application-cookie=keep", secondForwardedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devwt-context", firstForwardedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devwt-context", secondForwardedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("ctx-a", "ctx-a"), (firstResponse.Context, firstResponse.Body));
        Assert.Equal(("ctx-b", "ctx-b"), (secondResponse.Context, secondResponse.Body));
        var entries = history.Snapshot();
        Assert.Equal(["ctx-a", "ctx-b"], entries.Select(entry => entry.ContextId).Order());
        Assert.Single(entries.Select(entry => entry.ClientEndPoint).Distinct());
    }

    [Fact]
    public async Task Gateway_terminates_client_tls_and_forwards_to_plain_http_backend()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var backendPort = FreeTcpPort();
        while (backendPort == port)
        {
            backendPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1")
        ]));
        store.SaveRouting(new DevwtRoutingState([], null)
        {
            PortActiveTargets = [new DevwtPortActiveTarget("ctx-a", port)],
            HttpsProxyEndpoints = [new DevwtHttpsProxyEndpoint("127.0.0.1", port, DevwtHttpsProxyMode.Inspect)]
        });
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t127.0.0.1\t{port}\t127.0.0.1\t{backendPort}\t10\ttcp"
        ]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var backend = await StartHttpBackendAsync(backendPort, timeout.Token);
        var certificateStore = new DevwtGatewayCertificateStore(temp.Path);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", backendPort)
            ])),
            new NullActiveTcpConnectionSource(),
            new EmptyProcessObservationSource(),
            store,
            certificateStore: certificateStore);
        using var gatewayCancellation = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayCancellation.Token);
        await WaitForPortAsync(port);

        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var response = await client.SendAsync(request, timeout.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("plain-http", await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal("ctx-a", Assert.Single(response.Headers.GetValues("X-DevWT-Context")));
        }
        finally
        {
            gatewayCancellation.Cancel();
            await gatewayTask;
        }
    }

    [Fact]
    public async Task Gateway_yarp_proxies_websocket_upgrade_and_strips_devwt_headers()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var backendPort = FreeTcpPort();
        while (backendPort == port)
        {
            backendPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1")
        ]));
        store.SaveRouting(new DevwtRoutingState([], null)
        {
            PortActiveTargets = [new DevwtPortActiveTarget("ctx-a", port)]
        });
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t127.0.0.1\t{port}\t127.0.0.1\t{backendPort}\t10\ttcp"
        ]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backendListener = new TcpListener(IPAddress.Loopback, backendPort);
        backendListener.Start();
        var backendTask = AcceptWebSocketEchoOnceAsync(backendListener, timeout.Token);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", backendPort)
            ])),
            new NullActiveTcpConnectionSource(),
            new EmptyProcessObservationSource(),
            store);
        using var gatewayCancellation = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayCancellation.Token);
        await WaitForPortAsync(port);

        string echoed;
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-DevWT-Context", "ctx-a");
            socket.Options.SetRequestHeader("X-DevWT-Tab", "9");
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/socket"), timeout.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[32];
            var received = await socket.ReceiveAsync(buffer, timeout.Token);
            echoed = Encoding.UTF8.GetString(buffer, 0, received.Count);
            socket.Abort();
        }
        finally
        {
            gatewayCancellation.Cancel();
            await gatewayTask;
        }

        var backendRequest = await backendTask;
        Assert.Equal("hello", echoed);
        Assert.Contains("Upgrade: websocket", backendRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-DevWT-", backendRequest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gateway_forwards_tls_client_hello_without_terminating_tls_when_route_is_resolved()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var backendPort = FreeTcpPort();
        while (backendPort == port)
        {
            backendPort = FreeTcpPort();
        }

        var targetAddress = IPAddress.Loopback;
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var targetListener = new TcpListener(targetAddress, backendPort);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task targetTask = Task.CompletedTask;

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([Context("ctx-a", repository.Id, @"C:\work\a", DevwtPortShift.LoopbackAddress) with { AssignedPortBase = 24000 }]));
        store.SaveRouting(new DevwtRoutingState([], null)
        {
            HttpsProxyEndpoints = [new DevwtHttpsProxyEndpoint("127.0.0.1", port, DevwtHttpsProxyMode.Tunnel)]
        });
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{backendPort}\t10"
        ]);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([new ListenerObservation(10, "127.0.0.1", backendPort)])),
            new NullActiveTcpConnectionSource(),
            new EmptyProcessObservationSource(),
            store,
            certificateStore: new DevwtGatewayCertificateStore(temp.Path));
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(250, testCts.Token);
        targetListener.Start();
        targetTask = Task.Run(async () =>
        {
            try
            {
                using var accepted = await targetListener.AcceptTcpClientAsync(testCts.Token);
                var stream = accepted.GetStream();
                var buffer = new byte[5];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset), testCts.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                received.SetResult(buffer[..offset]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("target-response"), testCts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                received.TrySetException(ex);
            }
        });

        byte[] response;
        var hello = new byte[] { 0x16, 0x03, 0x03, 0x00, 0x01 };
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var clientStream = client.GetStream();
            await clientStream.WriteAsync(hello, testCts.Token);
            var responseBuffer = new byte[32];
            var read = await clientStream.ReadAsync(responseBuffer, testCts.Token);
            response = responseBuffer[..read];
        }
        finally
        {
            cts.Cancel();
            targetListener.Stop();
            testCts.Cancel();
            await gatewayTask;
            try
            {
                await targetTask;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        Assert.Equal(hello, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("target-response", Encoding.ASCII.GetString(response));
    }

    [Fact]
    public async Task Gateway_process_target_routes_child_process_to_selected_context_before_newest_route()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var targetPort = FreeTcpPort();
        var unusedNewestPort = FreeTcpPort();
        while (targetPort == port)
        {
            targetPort = FreeTcpPort();
        }

        while (unusedNewestPort == port || unusedNewestPort == targetPort)
        {
            unusedNewestPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1") with { AssignedPortBase = 25000 }
        ]));
        store.SaveRouting(new DevwtRoutingState(
            ExplicitLinkMaps: [],
            ActiveTarget: null,
            ProcessTargets: [new DevwtProcessTarget(200, "ctx-a")]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{targetPort}\t10",
            $"ctx-b\t{port}\t127.0.0.1\t{unusedNewestPort}\t11"
        ]);

        var targetListener = new TcpListener(IPAddress.Loopback, targetPort);
        var snapshotBuilder = new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
            new ListenerObservation(10, "127.0.0.1", targetPort),
            new ListenerObservation(11, "127.0.0.1", unusedNewestPort)
        ]));
        var routeTable = snapshotBuilder.BuildRouteTable();
        Assert.Equal(["ctx-a", "ctx-b"], routeTable.Routes.Select(route => route.ContextId).ToArray());
        Assert.Equal("ctx-a", routeTable.ResolveCallerContext(port, "ctx-a", cookieContextId: null)!.ContextId);
        IReadOnlyList<ProcessObservation> processes = [
            new ProcessObservation(10, null, @"C:\tools\server-a.exe", null, null),
            new ProcessObservation(11, null, @"C:\tools\server-b.exe", null, null),
            new ProcessObservation(200, null, @"C:\tools\agent.exe", null, null),
            new ProcessObservation(300, 200, @"C:\tools\playwright.exe", null, null)
        ];
        var processSource = new RecordingProcessObservationSource(processes);
        Assert.Equal("ctx-a", ProcessContextTargetResolver.ResolveConfiguredTarget(
            300,
            store.LoadContexts(),
            processes,
            store.LoadRouting()));

        var connectionSource = new RecordingActiveTcpConnectionSource(300);
        var gateway = new DevwtGatewayServer(
            snapshotBuilder,
            connectionSource,
            processSource,
            store);
        using var cts = new CancellationTokenSource();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(600, testCts.Token);

        targetListener.Start();
        var targetAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetTask = Task.Run(async () =>
        {
            using var accepted = await targetListener.AcceptTcpClientAsync(testCts.Token);
            targetAccepted.SetResult();
            var stream = accepted.GetStream();
            var requestBuffer = new byte[128];
            using var request = new MemoryStream();
            while (!DevwtGatewayHttpHeaders.HasCompleteHeaderBlock(request.GetBuffer().AsSpan(0, (int)request.Length)))
            {
                var received = await stream.ReadAsync(requestBuffer.AsMemory(0, requestBuffer.Length), testCts.Token);
                if (received == 0)
                {
                    throw new IOException("Gateway connected to the target but closed before proxying request bytes.");
                }

                request.Write(requestBuffer, 0, received);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nctx-a"), testCts.Token);
            await Task.Delay(100, testCts.Token);
        }, testCts.Token);

        string response;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, testCts.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"), testCts.Token);
            response = await ReadHttpResponseAsync(stream, returnRawResponse: false, testCts.Token);
        }
        finally
        {
            cts.Cancel();
            targetListener.Stop();
            testCts.Cancel();
            await gatewayTask;
            try
            {
                await targetTask;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        Assert.True(connectionSource.Calls > 0);
        Assert.Equal(1, processSource.Calls);
        Assert.True(targetAccepted.Task.IsCompletedSuccessfully);
        Assert.Equal("ctx-a", response);
    }

    [Fact]
    public async Task Gateway_session_rule_routes_browser_process_to_context_started_by_same_application_session()
    {
        using var temp = new TempDirectory();
        var history = new DevwtConnectionHistory();
        var port = FreeTcpPort();
        var decoyPort = FreeTcpPort();
        var firstTargetPort = FreeTcpPort();
        var secondTargetPort = FreeTcpPort();
        var decoyTargetPort = FreeTcpPort();
        while (decoyPort == port)
        {
            decoyPort = FreeTcpPort();
        }

        while (firstTargetPort == port || firstTargetPort == decoyPort)
        {
            firstTargetPort = FreeTcpPort();
        }

        while (secondTargetPort == port || secondTargetPort == decoyPort || secondTargetPort == firstTargetPort)
        {
            secondTargetPort = FreeTcpPort();
        }

        while (decoyTargetPort == port
               || decoyTargetPort == decoyPort
               || decoyTargetPort == firstTargetPort
               || decoyTargetPort == secondTargetPort)
        {
            decoyTargetPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1") with { AssignedPortBase = 25000 }
        ]));
        store.SaveRuntimeSettings(new DevwtRuntimeSettings([], [
            new DevwtSessionRule(
                Name: "ABP Studio",
                Match: new DevwtSessionMatch(ProcessName: "Volo.Abp.Studio.UI.Host"),
                Identity: new DevwtSessionIdentity(
                    DevwtSessionIdentityKind.RootProcess,
                    Prefix: "abp-studio:"))
        ]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-b\t{decoyPort}\t127.0.0.1\t{decoyTargetPort}\t201",
            $"ctx-a\t{port}\t127.0.0.1\t{firstTargetPort}\t200",
            $"ctx-b\t{port}\t127.0.0.1\t{secondTargetPort}\t400"
        ]);

        var processSource = new FixedProcessObservationSource([
            new ProcessObservation(100, null, @"C:\Users\salih\AppData\Local\abp-studio\current\Volo.Abp.Studio.UI.Host.exe", null, null, StartTime: "2026-07-13T10:00:00Z"),
            new ProcessObservation(200, 100, @"D:\GitHub\volo\LowCodeDemoApp.AuthServer.exe", null, null, StartTime: "2026-07-13T10:00:10Z"),
            new ProcessObservation(201, 100, @"D:\GitHub\volo\LowCodeDemoApp.HttpApi.Host.exe", null, null, StartTime: "2026-07-13T10:00:15Z"),
            new ProcessObservation(300, 100, @"C:\Program Files\Google\Chrome\Application\chrome.exe", null, null, StartTime: "2026-07-13T10:00:20Z"),
            new ProcessObservation(400, null, @"D:\GitHub\other\LowCodeDemoApp.AuthServer.exe", null, null, StartTime: "2026-07-13T10:00:30Z")
        ]);
        Assert.Equal("ctx-a", ProcessSessionResolver.ResolveSessionContext(
            300,
            [
                new GatewayRoute("ctx-a", repository.Id, @"C:\work\a", port, "127.0.0.1", firstTargetPort, 200),
                new GatewayRoute("ctx-b", repository.Id, @"C:\work\b", port, "127.0.0.1", secondTargetPort, 400)
            ],
            processSource.Read(),
            store.LoadRuntimeSettings()));

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstListener = new TcpListener(IPAddress.Loopback, firstTargetPort);
        var secondListener = new TcpListener(IPAddress.Loopback, secondTargetPort);
        firstListener.Start();
        secondListener.Start();
        var firstBackend = AcceptHttpOnceAsync(firstListener, "ctx-a", testCts.Token);
        var secondBackend = AcceptHttpOnceAsync(secondListener, "ctx-b", testCts.Token);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(201, "127.0.0.1", decoyTargetPort),
                new ListenerObservation(200, "127.0.0.1", firstTargetPort),
                new ListenerObservation(400, "127.0.0.1", secondTargetPort)
            ])),
            new FixedActiveTcpConnectionSource(300),
            processSource,
            store,
            connectionHistory: history);
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(600, testCts.Token);

        try
        {
            var response = await RequestGatewayAsync(port, testCts.Token);
            Assert.Equal("ctx-a", response);
            await firstBackend.WaitAsync(testCts.Token);
            Assert.False(secondBackend.IsCompletedSuccessfully);
        }
        finally
        {
            cts.Cancel();
            firstListener.Stop();
            secondListener.Stop();
            testCts.Cancel();
            await gatewayTask;
            try
            {
                await secondBackend;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        var entry = Assert.Single(history.Snapshot());
        Assert.Equal("session-context", entry.RouteReason);
        Assert.Equal("abp-studio:100:2026-07-13T10:00:00Z", entry.SessionId);
    }

    [Fact]
    public async Task Gateway_retries_process_identity_after_initial_observation_miss()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var firstTargetPort = FreeTcpPort();
        var secondTargetPort = FreeTcpPort();
        while (firstTargetPort == port)
        {
            firstTargetPort = FreeTcpPort();
        }
        while (secondTargetPort == port || secondTargetPort == firstTargetPort)
        {
            secondTargetPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var history = new DevwtConnectionHistory();
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1") with { AssignedPortBase = 25000 }
        ]));
        store.SaveRuntimeSettings(new DevwtRuntimeSettings([], [
            new DevwtSessionRule(
                "Codex",
                new DevwtSessionMatch(ProcessName: "codex-root.exe"),
                new DevwtSessionIdentity(DevwtSessionIdentityKind.EnvironmentVariable, "CODEX_THREAD_ID", "codex:"))
        ]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{firstTargetPort}\t200",
            $"ctx-b\t{port}\t127.0.0.1\t{secondTargetPort}\t400"
        ]);
        var sessionA = new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "thread-a" };
        var sessionB = new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "thread-b" };
        var processSource = new DelayedProcessObservationSource([
            new ProcessObservation(100, null, @"C:\tools\codex-root.exe", null, null, sessionA),
            new ProcessObservation(200, 100, @"C:\tools\server-a.exe", null, null),
            new ProcessObservation(300, 100, @"C:\tools\client.exe", null, null),
            new ProcessObservation(350, null, @"C:\tools\codex-root.exe", null, null, sessionB),
            new ProcessObservation(400, 350, @"C:\tools\server-b.exe", null, null)
        ]);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstListener = new TcpListener(IPAddress.Loopback, firstTargetPort);
        var secondListener = new TcpListener(IPAddress.Loopback, secondTargetPort);
        firstListener.Start();
        secondListener.Start();
        var firstBackend = AcceptHttpOnceAsync(firstListener, "ctx-a", testCts.Token);
        var secondBackend = AcceptHttpOnceAsync(secondListener, "ctx-b", testCts.Token);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(200, "127.0.0.1", firstTargetPort),
                new ListenerObservation(400, "127.0.0.1", secondTargetPort)
            ])),
            new FixedActiveTcpConnectionSource(300),
            processSource,
            store,
            connectionHistory: history);
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(600, testCts.Token);

        try
        {
            Assert.Equal("ctx-b", await RequestGatewayAsync(port, testCts.Token));
            await secondBackend.WaitAsync(testCts.Token);
            Assert.Equal("ctx-a", await RequestGatewayAsync(port, testCts.Token));
            await firstBackend.WaitAsync(testCts.Token);
        }
        finally
        {
            cts.Cancel();
            firstListener.Stop();
            secondListener.Stop();
            testCts.Cancel();
            await gatewayTask;
        }

        var entries = history.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal("session-context", entries[0].RouteReason);
        Assert.Equal("codex:thread-a", entries[0].SessionId);
        Assert.Equal("newest", entries[1].RouteReason);
        Assert.Null(entries[1].SessionId);
    }

    [Fact]
    public void Process_session_resolver_uses_application_scoped_environment_variable_identity()
    {
        var settings = new DevwtRuntimeSettings([], [
            new DevwtSessionRule(
                Name: "Codex",
                Match: new DevwtSessionMatch(ProcessName: "codex.exe"),
                Identity: new DevwtSessionIdentity(
                    DevwtSessionIdentityKind.EnvironmentVariable,
                    Value: "CODEX_THREAD_ID",
                    Prefix: "codex:"))
        ]);
        var processes = new[]
        {
            new ProcessObservation(
                10,
                null,
                @"C:\tools\codex.exe",
                null,
                null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CODEX_THREAD_ID"] = "019ee8f4"
                })
        };

        var sessionId = ProcessSessionResolver.ResolveSessionId(10, processes, settings);

        Assert.Equal("codex:019ee8f4", sessionId);
    }

    [Fact]
    public void Process_session_resolver_preserves_environment_only_identity_for_codex_started_helpers()
    {
        var settings = new DevwtRuntimeSettings([], [
            new DevwtSessionRule(
                Name: "Codex",
                Match: new DevwtSessionMatch(EnvironmentVariable: "CODEX_THREAD_ID"),
                Identity: new DevwtSessionIdentity(
                    DevwtSessionIdentityKind.EnvironmentVariable,
                    Value: "CODEX_THREAD_ID",
                    Prefix: "codex:"))
        ]);
        var processes = new[]
        {
            new ProcessObservation(
                10,
                null,
                @"C:\Users\salih\AppData\Local\Programs\Ollama\ollama.exe",
                null,
                null,
                new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "detached-task" },
                "2026-07-15T20:00:00Z")
        };

        var sessionId = ProcessSessionResolver.ResolveSessionId(10, processes, settings);

        Assert.Equal("codex:detached-task", sessionId);
    }

    [Fact]
    public void Process_session_resolver_rejects_a_reused_parent_pid_started_after_the_child()
    {
        var settings = new DevwtRuntimeSettings([], [
            new DevwtSessionRule(
                Name: "Codex",
                Match: new DevwtSessionMatch(ProcessName: "codex-code-mode-host.exe"),
                Identity: new DevwtSessionIdentity(
                    DevwtSessionIdentityKind.EnvironmentVariable,
                    Value: "CODEX_THREAD_ID",
                    Prefix: "codex:"))
        ]);
        var processes = new[]
        {
            new ProcessObservation(
                10,
                20,
                @"C:\Users\salih\AppData\Local\Programs\Ollama\ollama.exe",
                null,
                null,
                StartTime: "2026-07-15T20:00:00Z"),
            new ProcessObservation(
                20,
                null,
                @"C:\Program Files\WindowsApps\OpenAI.Codex\codex-code-mode-host.exe",
                null,
                null,
                new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "new-task" },
                "2026-07-15T21:00:00Z")
        };

        var sessionId = ProcessSessionResolver.ResolveSessionId(10, processes, settings);

        Assert.Null(sessionId);
    }

    [Fact]
    public void Process_session_context_uses_the_supplied_client_identity_snapshot()
    {
        var settings = new DevwtRuntimeSettings([], [
            new DevwtSessionRule(
                "Codex",
                new DevwtSessionMatch(ProcessName: "codex-code-mode-host.exe"),
                new DevwtSessionIdentity(DevwtSessionIdentityKind.EnvironmentVariable, "CODEX_THREAD_ID", "codex:"))
        ]);
        var listenerEnvironment = new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "thread-a" };
        var processes = new[]
        {
            new ProcessObservation(100, null, @"C:\tools\codex-code-mode-host.exe", null, null, listenerEnvironment),
            new ProcessObservation(200, 100, @"C:\tools\server.exe", null, null)
        };
        var routes = new[]
        {
            new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 24001, 200)
        };

        var contextId = ProcessSessionResolver.ResolveSessionContext(
            "codex:thread-a",
            routes,
            processes,
            settings);

        Assert.Equal("ctx-a", contextId);
    }

    [Fact]
    public void Process_routing_cache_evicts_expired_and_least_recent_entries()
    {
        var cache = new ProcessRoutingCache(
            capacity: 2,
            identityLifetime: TimeSpan.FromSeconds(5),
            lastContextLifetime: TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        cache.SetIdentity(10, new CachedProcessIdentity("a.exe", "a.exe", "session-a"), now);
        cache.SetIdentity(20, new CachedProcessIdentity("b.exe", "b.exe", "session-b"), now.AddSeconds(1));
        cache.SetIdentity(30, new CachedProcessIdentity("c.exe", "c.exe", "session-c"), now.AddSeconds(2));

        Assert.Null(cache.TryGetIdentity(10, now.AddSeconds(2)));
        Assert.NotNull(cache.TryGetIdentity(20, now.AddSeconds(2)));
        Assert.Null(cache.TryGetIdentity(20, now.AddSeconds(7)));
    }

    [Fact]
    public void Process_routing_cache_bounds_identity_and_last_context_at_default_capacity()
    {
        var cache = new ProcessRoutingCache();
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        for (var processId = 1; processId <= 513; processId++)
        {
            var observedAt = now.AddMilliseconds(processId);
            cache.SetIdentity(processId, new CachedProcessIdentity($"{processId}.exe", $"{processId}.exe", null), observedAt);
            cache.SetLastContext(processId, $"ctx-{processId}", observedAt);
        }

        Assert.Null(cache.TryGetIdentity(1, now.AddSeconds(1)));
        Assert.Null(cache.TryGetLastContext(1, now.AddSeconds(1)));
        Assert.NotNull(cache.TryGetIdentity(513, now.AddSeconds(1)));
        Assert.Equal("ctx-513", cache.TryGetLastContext(513, now.AddSeconds(1)));
        Assert.Null(cache.TryGetLastContext(513, now.AddMinutes(6)));
    }

    [Fact]
    public void Process_snapshot_cache_reuses_and_atomically_replaces_snapshots_for_new_required_processes()
    {
        var cache = new ProcessSnapshotCache();
        IReadOnlyList<ProcessObservation> source = [
            new ProcessObservation(10, null, @"C:\tools\client.exe", null, null)
        ];
        var calls = 0;
        IReadOnlyList<ProcessObservation> Load()
        {
            calls++;
            return source;
        }

        var first = cache.GetOrRefresh(new HashSet<int> { 10 }, "rules-a", Load);
        var reused = cache.GetOrRefresh(new HashSet<int> { 10 }, "rules-a", Load);
        source = [
            new ProcessObservation(10, null, @"C:\tools\client.exe", null, null),
            new ProcessObservation(20, null, @"C:\tools\listener.exe", null, null)
        ];
        var replaced = cache.GetOrRefresh(new HashSet<int> { 10, 20 }, "rules-a", Load);

        Assert.Equal(2, calls);
        Assert.Same(first, reused);
        Assert.NotSame(first, replaced);
        Assert.Equal([10, 20], replaced.Select(process => process.ProcessId));
    }

    [Fact]
    public void Process_snapshot_cache_replaces_snapshot_when_session_rules_change()
    {
        var cache = new ProcessSnapshotCache();
        var calls = 0;
        IReadOnlyList<ProcessObservation> Load()
        {
            calls++;
            return [new ProcessObservation(10, null, @"C:\tools\client.exe", null, null)];
        }

        cache.GetOrRefresh(new HashSet<int> { 10 }, "rules-a", Load);
        cache.GetOrRefresh(new HashSet<int> { 10 }, "rules-b", Load);
        cache.GetOrRefresh(new HashSet<int> { 10 }, "rules-b", Load);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Process_snapshot_cache_replaces_snapshot_when_a_required_pid_is_reused()
    {
        var cache = new ProcessSnapshotCache();
        var firstStart = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        var replacementStart = firstStart.AddMinutes(1);
        var sourceStart = firstStart;
        var calls = 0;
        IReadOnlyList<ProcessObservation> Load()
        {
            calls++;
            return [
                new ProcessObservation(
                    10,
                    null,
                    @"C:\tools\client.exe",
                    null,
                    null,
                    StartTime: sourceStart.ToString("O"))
            ];
        }

        cache.GetOrRefresh(
            new HashSet<int> { 10 },
            "rules-a",
            Load,
            new Dictionary<int, DateTimeOffset> { [10] = firstStart });
        sourceStart = replacementStart;
        cache.GetOrRefresh(
            new HashSet<int> { 10 },
            "rules-a",
            Load,
            new Dictionary<int, DateTimeOffset> { [10] = replacementStart });

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Connection_history_remains_bounded_at_default_capacity()
    {
        var history = new DevwtConnectionHistory();
        var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
        for (var index = 0; index < 250; index++)
        {
            history.Add(new DevwtConnectionHistoryEntry(
                now.AddSeconds(index),
                GatewayRouteProtocol.Tcp,
                "127.0.0.1",
                44334,
                "127.0.0.1",
                24000 + index,
                "ctx-a",
                "context-a",
                "newest",
                index,
                "app.exe",
                "app.exe",
                $"127.0.0.1:{50000 + index}"));
        }

        var snapshot = history.Snapshot();
        Assert.Equal(200, snapshot.Count);
        Assert.Equal(249, snapshot[0].ProcessId);
        Assert.Equal(50, snapshot[^1].ProcessId);
    }

    [Fact]
    public async Task Gateway_active_target_overrides_last_process_context_immediately_after_ui_change()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var firstTargetPort = FreeTcpPort();
        var secondTargetPort = FreeTcpPort();
        while (firstTargetPort == port)
        {
            firstTargetPort = FreeTcpPort();
        }

        while (secondTargetPort == port || secondTargetPort == firstTargetPort)
        {
            secondTargetPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1") with { AssignedPortBase = 25000 }
        ]));
        store.SaveRouting(DevwtRoutingState.Empty);
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{firstTargetPort}\t10",
            $"ctx-b\t{port}\t127.0.0.1\t{secondTargetPort}\t11"
        ]);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstListener = new TcpListener(IPAddress.Loopback, firstTargetPort);
        var secondListener = new TcpListener(IPAddress.Loopback, secondTargetPort);
        firstListener.Start();
        secondListener.Start();
        var firstBackend = AcceptHttpOnceAsync(firstListener, "ctx-a", testCts.Token);
        var secondBackend = AcceptHttpOnceAsync(secondListener, "ctx-b", testCts.Token);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", firstTargetPort),
                new ListenerObservation(11, "127.0.0.1", secondTargetPort)
            ])),
            new FixedActiveTcpConnectionSource(300),
            new EmptyProcessObservationSource(),
            store);
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(600, testCts.Token);

        try
        {
            var initialResponse = await RequestGatewayAsync(port, testCts.Token);
            Assert.Equal("ctx-b", initialResponse);

            store.SaveRouting(new DevwtRoutingState([], new DevwtActiveTarget("ctx-a", port, "auto")));

            var activeResponse = await RequestGatewayAsync(port, testCts.Token);
            Assert.Equal("ctx-a", activeResponse);
            await firstBackend.WaitAsync(testCts.Token);
            await secondBackend.WaitAsync(testCts.Token);
        }
        finally
        {
            cts.Cancel();
            firstListener.Stop();
            secondListener.Stop();
            testCts.Cancel();
            await gatewayTask;
        }
    }

    [Fact]
    public async Task Gateway_application_target_routes_calling_process_and_records_history()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var firstTargetPort = FreeTcpPort();
        var secondTargetPort = FreeTcpPort();
        while (firstTargetPort == port)
        {
            firstTargetPort = FreeTcpPort();
        }

        while (secondTargetPort == port || secondTargetPort == firstTargetPort)
        {
            secondTargetPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var history = new DevwtConnectionHistory();
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1") with { AssignedPortBase = 25000 }
        ]));
        store.SaveRouting(new DevwtRoutingState(
            [],
            null,
            ApplicationTargets: [new DevwtApplicationTarget(chrome, "ctx-a", port, "auto")]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{firstTargetPort}\t10",
            $"ctx-b\t{port}\t127.0.0.1\t{secondTargetPort}\t11"
        ]);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstListener = new TcpListener(IPAddress.Loopback, firstTargetPort);
        var secondListener = new TcpListener(IPAddress.Loopback, secondTargetPort);
        firstListener.Start();
        secondListener.Start();
        var firstBackend = AcceptHttpOnceAsync(firstListener, "ctx-a", testCts.Token);
        var secondBackend = AcceptHttpOnceAsync(secondListener, "ctx-b", testCts.Token);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", firstTargetPort),
                new ListenerObservation(11, "127.0.0.1", secondTargetPort)
            ])),
            new FixedActiveTcpConnectionSource(300),
            new FixedProcessObservationSource([
                new ProcessObservation(300, null, chrome, null, null)
            ]),
            store,
            connectionHistory: history);
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(600, testCts.Token);

        try
        {
            var response = await RequestGatewayAsync(port, testCts.Token);
            Assert.Equal("ctx-a", response);
            await firstBackend.WaitAsync(testCts.Token);
            Assert.False(secondBackend.IsCompletedSuccessfully);
        }
        finally
        {
            cts.Cancel();
            firstListener.Stop();
            secondListener.Stop();
            testCts.Cancel();
            await gatewayTask;
            try
            {
                await secondBackend;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        var entry = Assert.Single(history.Snapshot());
        Assert.Equal(300, entry.ProcessId);
        Assert.Equal(chrome, entry.ProcessImagePath);
        Assert.Equal(chrome, entry.ApplicationKey);
        Assert.Equal("ctx-a", entry.ContextId);
        Assert.Equal("app-default", entry.RouteReason);
        Assert.Equal(port, entry.Port);
    }

    [Fact]
    public async Task Gateway_self_process_listener_wins_before_application_default()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var firstTargetPort = FreeTcpPort();
        var secondTargetPort = FreeTcpPort();
        while (firstTargetPort == port)
        {
            firstTargetPort = FreeTcpPort();
        }

        while (secondTargetPort == port || secondTargetPort == firstTargetPort)
        {
            secondTargetPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var history = new DevwtConnectionHistory();
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        var appPath = @"C:\work\b\src\App.Host\bin\Debug\net10.0\App.Host.exe";
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1") with { AssignedPortBase = 25000 }
        ]));
        store.SaveRouting(new DevwtRoutingState(
            [],
            null,
            ApplicationTargets: [new DevwtApplicationTarget(appPath, "ctx-a", port, "auto")]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{firstTargetPort}\t10",
            $"ctx-b\t{port}\t127.0.0.1\t{secondTargetPort}\t11"
        ]);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstListener = new TcpListener(IPAddress.Loopback, firstTargetPort);
        var secondListener = new TcpListener(IPAddress.Loopback, secondTargetPort);
        firstListener.Start();
        secondListener.Start();
        var firstBackend = AcceptHttpOnceAsync(firstListener, "ctx-a", testCts.Token);
        var secondBackend = AcceptHttpOnceAsync(secondListener, "ctx-b", testCts.Token);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", firstTargetPort),
                new ListenerObservation(11, "127.0.0.1", secondTargetPort)
            ])),
            new FixedActiveTcpConnectionSource(11),
            new FixedProcessObservationSource([
                new ProcessObservation(11, null, appPath, null, null)
            ]),
            store,
            connectionHistory: history);
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(600, testCts.Token);

        try
        {
            var response = await RequestGatewayAsync(port, testCts.Token);
            Assert.Equal("ctx-b", response);
            await secondBackend.WaitAsync(testCts.Token);
            Assert.False(firstBackend.IsCompletedSuccessfully);
        }
        finally
        {
            cts.Cancel();
            firstListener.Stop();
            secondListener.Stop();
            testCts.Cancel();
            await gatewayTask;
            try
            {
                await firstBackend;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        var entry = Assert.Single(history.Snapshot());
        Assert.Equal("ctx-b", entry.ContextId);
        Assert.Equal("self-process", entry.RouteReason);
    }

    [Theory]
    [InlineData("::1", "ctx-v4", false, false, null, null, HttpStatusCode.OK, "ctx-v4", "request-header")]
    [InlineData("127.0.0.1", "ctx-v6", false, false, null, null, HttpStatusCode.OK, "ctx-v6", "request-header")]
    [InlineData("::1", "ctx-missing", true, false, null, null, HttpStatusCode.BadGateway, "", "")]
    [InlineData("::1", "ctx-missing", false, true, null, null, HttpStatusCode.BadGateway, "", "")]
    [InlineData("::1", "ctx-missing", true, true, null, null, HttpStatusCode.OK, "ctx-v6", "browser-fallback-single-target")]
    [InlineData("::1", "ctx-active", false, true, "Automatic", null, HttpStatusCode.OK, "ctx-v6", "browser-worktree-fallback-single-target")]
    [InlineData("::1", "ctx-active", true, true, "Disabled", null, HttpStatusCode.BadGateway, "", "")]
    [InlineData("::1", "ctx-active", false, true, "Redirect", "ctx-v4", HttpStatusCode.OK, "ctx-v4", "browser-worktree-redirect")]
    public async Task Gateway_explicit_context_fallback_requires_global_setting_and_request_opt_in(
        string clientIp,
        string requestedContextId,
        bool fallbackSetting,
        bool fallbackHeader,
        string? policyMode,
        string? targetContextId,
        HttpStatusCode expectedStatus,
        string expectedBody,
        string expectedReason)
    {
        using var temp = new TempDirectory();
        var port = FreeDualStackTcpPort();
        using var ipv4BackendListener = new TcpListener(IPAddress.Loopback, 0);
        using var ipv6BackendListener = new TcpListener(IPAddress.IPv6Loopback, 0);
        ipv4BackendListener.Start();
        ipv6BackendListener.Start();
        var ipv4BackendPort = ((IPEndPoint)ipv4BackendListener.LocalEndpoint).Port;
        var ipv6BackendPort = ((IPEndPoint)ipv6BackendListener.LocalEndpoint).Port;

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-v4", repository.Id, @"C:\work\v4", "127.0.0.1"),
            Context("ctx-v6", repository.Id, @"C:\work\v6", "127.0.0.1"),
            Context("ctx-active", repository.Id, @"C:\work\active", "127.0.0.1")
        ]));
        IReadOnlyList<DevwtBrowserMissingPortPolicy> policies = policyMode is null
            ? Array.Empty<DevwtBrowserMissingPortPolicy>()
            : [
                new DevwtBrowserMissingPortPolicy(
                    requestedContextId,
                    port,
                    Enum.Parse<DevwtBrowserMissingPortPolicyMode>(policyMode),
                    targetContextId)
            ];
        store.SaveRuntimeSettings(new DevwtRuntimeSettings(
            BrowserFallbackOnMissingPort: fallbackSetting,
            BrowserMissingPortPolicies: policies));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-v4\t127.0.0.1\t{port}\t127.0.0.1\t{ipv4BackendPort}\t10\ttcp",
            $"ctx-v6\t::1\t{port}\t::1\t{ipv6BackendPort}\t20\ttcp"
        ]);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ipv4Backend = AcceptHttpOnceAsync(ipv4BackendListener, "ctx-v4", testCts.Token);
        var ipv6Backend = AcceptHttpOnceAsync(ipv6BackendListener, "ctx-v6", testCts.Token);
        var history = new DevwtConnectionHistory();
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", ipv4BackendPort),
                new ListenerObservation(20, "::1", ipv6BackendPort)
            ])),
            new NullActiveTcpConnectionSource(),
            new EmptyProcessObservationSource(),
            store,
            connectionHistory: history);
        using var gatewayCancellation = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayCancellation.Token);
        var clientAddress = IPAddress.Parse(clientIp);
        await WaitForPortAsync(clientAddress, port);

        HttpStatusCode actualStatus;
        string actualBody;
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false
            };
            using var client = new HttpClient(handler);
            var uriHost = clientAddress.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{clientAddress}]"
                : clientAddress.ToString();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{uriHost}:{port}/");
            request.Headers.Host = $"localhost:{port}";
            request.Headers.Add("X-DevWT-Context", requestedContextId);
            if (fallbackHeader)
            {
                request.Headers.Add("X-DevWT-Allow-Fallback", "1");
            }
            using var response = await client.SendAsync(request, testCts.Token);
            actualStatus = response.StatusCode;
            actualBody = await response.Content.ReadAsStringAsync(testCts.Token);
        }
        finally
        {
            gatewayCancellation.Cancel();
            ipv4BackendListener.Stop();
            ipv6BackendListener.Stop();
            testCts.Cancel();
            await gatewayTask;
            foreach (var backend in new[] { ipv4Backend, ipv6Backend })
            {
                try
                {
                    await backend;
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }
            }
        }

        Assert.Equal(expectedStatus, actualStatus);
        Assert.Equal(expectedBody, actualBody);
        if (expectedStatus == HttpStatusCode.OK)
        {
            var entry = Assert.Single(history.Snapshot());
            Assert.Equal(expectedBody, entry.ContextId);
            Assert.Equal(expectedReason, entry.RouteReason);
        }
        else
        {
            Assert.Empty(history.Snapshot());
        }
    }

    [Fact]
    public async Task Gateway_adds_context_response_header_for_http_responses()
    {
        using var temp = new TempDirectory();
        var port = FreeTcpPort();
        var backendPort = FreeTcpPort();
        while (backendPort == port)
        {
            backendPort = FreeTcpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var history = new DevwtConnectionHistory();
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with
        {
            AssignedPortBase = 24000,
            Description = "Review driver code"
        }]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{backendPort}\t10"
        ]);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, backendPort);
        listener.Start();
        var backend = AcceptHttpResponseOnceAsync(
            listener,
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok",
            testCts.Token);
        var connectionSource = new RecordingActiveTcpConnectionSource(300);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([new ListenerObservation(10, "127.0.0.1", backendPort)])),
            connectionSource,
            new FixedProcessObservationSource([
                new ProcessObservation(300, null, chrome, null, null)
            ]),
            store,
            connectionHistory: history);
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(600, testCts.Token);

        string response;
        try
        {
            response = await RequestGatewayRawAsync(port, testCts.Token);
            await backend.WaitAsync(testCts.Token);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            testCts.Cancel();
            await gatewayTask;
        }

        Assert.Contains("X-DevWT-Context: ctx-a\r\n", response, StringComparison.Ordinal);
        Assert.Contains("X-DevWT-Route-Reason: single-target\r\n", response, StringComparison.Ordinal);
        Assert.Contains("X-DevWT-Description: Review driver code\r\n", response, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\nok", response, StringComparison.Ordinal);
        Assert.Equal(1, connectionSource.Calls);
        var entry = Assert.Single(history.Snapshot());
        Assert.Equal(300, entry.ProcessId);
        Assert.Equal(chrome, entry.ProcessImagePath);
        Assert.Equal(chrome, entry.ApplicationKey);
        Assert.Equal("single-target", entry.RouteReason);
    }

    [Fact]
    public async Task Gateway_proxies_udp_datagrams_to_shifted_backend_port()
    {
        using var temp = new TempDirectory();
        var port = FreeUdpPort();
        var backendPort = FreeUdpPort();
        while (backendPort == port)
        {
            backendPort = FreeUdpPort();
        }

        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 }]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{backendPort}\t10\tudp"
        ]);

        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        var backendTask = Task.Run(async () =>
        {
            var received = await backend.ReceiveAsync();
            await backend.SendAsync(Encoding.ASCII.GetBytes("udp-ok"), "udp-ok".Length, received.RemoteEndPoint);
        });
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(10, "127.0.0.1", backendPort, GatewayRouteProtocol.Udp)
            ])),
            new NullActiveTcpConnectionSource(),
            new ThrowingProcessObservationSource(),
            store,
            udpEndpointSource: new ThrowingActiveUdpEndpointSource());
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(500);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            await client.SendAsync(Encoding.ASCII.GetBytes("hello"), "hello".Length, new IPEndPoint(IPAddress.Loopback, port));
            var response = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("udp-ok", Encoding.ASCII.GetString(response.Buffer));
            await backendTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cts.Cancel();
            await gatewayTask;
        }
    }

    [Fact]
    public async Task Gateway_udp_history_records_natural_session_identity()
    {
        using var temp = new TempDirectory();
        var port = FreeUdpPort();
        var firstBackendPort = FreeUdpPort();
        var secondBackendPort = FreeUdpPort();
        var store = new DevwtStateStore(temp.Path);
        var history = new DevwtConnectionHistory();
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            Context("ctx-a", repository.Id, @"C:\work\a", "127.0.0.1") with { AssignedPortBase = 24000 },
            Context("ctx-b", repository.Id, @"C:\work\b", "127.0.0.1") with { AssignedPortBase = 25000 }
        ]));
        store.SaveRuntimeSettings(new DevwtRuntimeSettings([], [
            new DevwtSessionRule(
                "Codex",
                new DevwtSessionMatch(ProcessName: "codex-root.exe"),
                new DevwtSessionIdentity(DevwtSessionIdentityKind.EnvironmentVariable, "CODEX_THREAD_ID", "codex:"))
        ]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            $"ctx-a\t{port}\t127.0.0.1\t{firstBackendPort}\t200\tudp",
            $"ctx-b\t{port}\t127.0.0.1\t{secondBackendPort}\t400\tudp"
        ]);
        var sessionA = new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "thread-a" };
        var sessionB = new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "thread-b" };
        var processSource = new FixedProcessObservationSource([
            new ProcessObservation(100, null, @"C:\tools\codex-root.exe", null, null, sessionA),
            new ProcessObservation(200, 100, @"C:\tools\server-a.exe", null, null),
            new ProcessObservation(300, 100, @"C:\tools\client.exe", null, null),
            new ProcessObservation(350, null, @"C:\tools\codex-root.exe", null, null, sessionB),
            new ProcessObservation(400, 350, @"C:\tools\server-b.exe", null, null)
        ]);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var firstBackend = new UdpClient(new IPEndPoint(IPAddress.Loopback, firstBackendPort));
        using var secondBackend = new UdpClient(new IPEndPoint(IPAddress.Loopback, secondBackendPort));
        var firstBackendTask = Task.Run(async () =>
        {
            var received = await firstBackend.ReceiveAsync(testCts.Token);
            await firstBackend.SendAsync(Encoding.ASCII.GetBytes("ctx-a"), received.RemoteEndPoint, testCts.Token);
        }, testCts.Token);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([
                new ListenerObservation(200, "127.0.0.1", firstBackendPort, GatewayRouteProtocol.Udp),
                new ListenerObservation(400, "127.0.0.1", secondBackendPort, GatewayRouteProtocol.Udp)
            ])),
            new NullActiveTcpConnectionSource(),
            processSource,
            store,
            connectionHistory: history,
            udpEndpointSource: new FixedActiveUdpEndpointSource(300));
        using var cts = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(cts.Token);
        await Task.Delay(500, testCts.Token);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            await client.SendAsync(Encoding.ASCII.GetBytes("hello"), new IPEndPoint(IPAddress.Loopback, port), testCts.Token);
            var response = await client.ReceiveAsync(testCts.Token);
            Assert.Equal("ctx-a", Encoding.ASCII.GetString(response.Buffer));
            await firstBackendTask.WaitAsync(testCts.Token);
        }
        finally
        {
            cts.Cancel();
            await gatewayTask;
        }

        var entry = Assert.Single(history.Snapshot());
        Assert.Equal("session-context", entry.RouteReason);
        Assert.Equal("codex:thread-a", entry.SessionId);
    }

    [Fact]
    public async Task Gateway_survives_transient_state_file_lock_during_refresh()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-a", "repo-a", @"C:\work\a", @"C:\work\a\.git", []);
        var context = Context("ctx-a", repository.Id, @"C:\work\a", "127.80.1.10");
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([context]));
        var contextsPath = Path.Combine(temp.Path, "contexts.json");
        await using var locked = new FileStream(contextsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var gateway = new DevwtGatewayServer(
            new DevwtRouteSnapshotBuilder(store, new FixedListenerSource([])),
            new NullActiveTcpConnectionSource(),
            new EmptyProcessObservationSource(),
            store);
        using var cts = new CancellationTokenSource();

        var task = gateway.RunAsync(cts.Token);
        await Task.Delay(400);
        cts.Cancel();
        await task;

        Assert.False(task.IsFaulted);
    }

    [Fact]
    public void Gateway_no_longer_exposes_in_page_chooser_control_paths()
    {
        var html = DevwtWebUiAssets.RenderShell();

        Assert.DoesNotContain("__devwt", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Choose a target for this tab", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gateway_route_table_uses_original_port_and_shifted_backend_port_for_web_ui()
    {
        var route = new GatewayRoute("ctx-a", "repo-a", @"C:\work\repo-a", 44334, "127.0.0.1", 55297, 42);
        var table = GatewayRouteTable.FromRoutes(
            [route],
            DevwtRepositoryState.Empty,
            DevwtContextState.Empty,
            DevwtRoutingState.Empty);

        var exposed = Assert.Single(table.Routes);

        Assert.Equal(44334, exposed.Port);
        Assert.Equal("127.0.0.1", exposed.TargetIp);
        Assert.Equal(55297, exposed.TargetPort);
    }

    [Fact]
    public void Gateway_route_table_exposes_runtime_routes_for_web_ui()
    {
        var route = new GatewayRoute("ctx-volo-feature", "repo-volo", @"C:\work\volo", 5025, "127.80.1.10", 5025, 10);
        var table = GatewayRouteTable.FromRoutes(
            [route],
            DevwtRepositoryState.Empty,
            DevwtContextState.Empty,
            DevwtRoutingState.Empty);

        Assert.Same(route, Assert.Single(table.Routes));
    }

    [Fact]
    public void Gateway_route_table_treats_shared_bindings_as_one_logical_target()
    {
        var first = new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 55297, 10);
        var second = first with { ListenerProcessId = 20 };
        var table = GatewayRouteTable.FromRoutes(
            [first, second],
            DevwtRepositoryState.Empty,
            DevwtContextState.Empty,
            DevwtRoutingState.Empty);

        var target = table.ResolveSingleTarget(44334, GatewayRouteProtocol.Tcp, "127.0.0.1");

        Assert.Same(first, target);
        Assert.Equal([10, 20], table.Routes.Select(route => route.ListenerProcessId));
    }

    [Fact]
    public void Gateway_route_table_does_not_merge_distinct_context_targets()
    {
        var table = GatewayRouteTable.FromRoutes(
            [
                new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 55297, 10),
                new GatewayRoute("ctx-b", "repo-b", @"C:\work\b", 44334, "127.0.0.1", 55297, 20)
            ],
            DevwtRepositoryState.Empty,
            DevwtContextState.Empty,
            DevwtRoutingState.Empty);

        Assert.Null(table.ResolveSingleTarget(44334, GatewayRouteProtocol.Tcp, "127.0.0.1"));
    }

    [Fact]
    public void Route_snapshot_uses_port_binding_map_to_expose_original_localhost_port()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-volo", "volo", @"C:\repos\volo", @"C:\repos\volo\.git", []);
        var context = new DevwtContext("ctx-volo", repository.Id, "volo", @"C:\repos\volo", "main", "127.0.0.1", "DevWT-runtime", DevwtContextStatus.Active, AssignedPortBase: 24000);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([context]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            "ctx-volo\t44334\t127.0.0.1\t55297\t42"
        ]);
        var snapshot = new DevwtRouteSnapshotBuilder(
            store,
            new FixedListenerSource([new ListenerObservation(42, "127.0.0.1", 55297)]));

        var route = Assert.Single(snapshot.BuildRouteTable().Routes);

        Assert.Equal(context.Id, route.ContextId);
        Assert.Equal(44334, route.Port);
        Assert.Equal("127.0.0.1", route.TargetIp);
        Assert.Equal(55297, route.TargetPort);
    }

    [Fact]
    public void Route_snapshot_preserves_original_bind_address_for_gateway_listener()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-volo", "volo", @"C:\repos\volo", @"C:\repos\volo\.git", []);
        var context = new DevwtContext("ctx-volo", repository.Id, "volo", @"C:\repos\volo", "main", "127.0.0.1", "DevWT-runtime", DevwtContextStatus.Active, AssignedPortBase: 24000);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([context]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            "ctx-volo\t192.168.1.10\t44334\t192.168.1.10\t55297\t42\ttcp"
        ]);
        var snapshot = new DevwtRouteSnapshotBuilder(
            store,
            new FixedListenerSource([new ListenerObservation(42, "192.168.1.10", 55297)]));

        var route = Assert.Single(snapshot.BuildRouteTable().Routes);

        Assert.Equal("192.168.1.10", route.ListenIp);
        Assert.Equal(44334, route.Port);
        Assert.Equal("192.168.1.10", route.TargetIp);
        Assert.Equal(55297, route.TargetPort);
    }

    [Fact]
    public void Route_snapshot_preserves_ipv6_loopback_binding()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-volo", "volo", @"C:\repos\volo", @"C:\repos\volo\.git", []);
        var context = new DevwtContext("ctx-volo", repository.Id, "volo", @"C:\repos\volo", "main", "127.0.0.1", "DevWT-runtime", DevwtContextStatus.Active, AssignedPortBase: 24000);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([context]));
        File.WriteAllLines(HookPortBindingMap.ResolvePath(temp.Path), [
            "ctx-volo\t::1\t44334\t::1\t55297\t42\ttcp"
        ]);
        var snapshot = new DevwtRouteSnapshotBuilder(
            store,
            new FixedListenerSource([new ListenerObservation(42, "::1", 55297)]));

        var route = Assert.Single(snapshot.BuildRouteTable().Routes);

        Assert.Equal("::1", route.ListenIp);
        Assert.Equal(44334, route.Port);
        Assert.Equal("::1", route.TargetIp);
        Assert.Equal(55297, route.TargetPort);
    }

    [Fact]
    public void Route_snapshot_ignores_localhost_listener_without_hook_port_binding()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-volo", "volo", @"C:\repos\volo", @"C:\repos\volo\.git", []);
        var context = new DevwtContext("ctx-volo", repository.Id, "volo", @"C:\repos\volo", "main", "127.0.0.1", "DevWT-runtime", DevwtContextStatus.Active, AssignedPortBase: 24000);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([context]));
        var snapshot = new DevwtRouteSnapshotBuilder(
            store,
            new FixedListenerSource([new ListenerObservation(42, "127.0.0.1", 55297)]));

        var routes = snapshot.BuildRouteTable().Routes;

        Assert.Empty(routes);
    }

    [Fact]
    public void Synchronizer_registers_new_worktrees_and_repairs_deleted_tracked_files()
    {
        using var temp = new TempDirectory();
        var repoRoot = Path.Combine(temp.Path, "repo");
        var worktreeRoot = Path.Combine(temp.Path, "repo-feature");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(worktreeRoot);
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository(
            "repo-volo",
            "volo",
            repoRoot,
            Path.Combine(repoRoot, ".git"),
            []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            new DevwtContext(
                "ctx-main",
                repository.Id,
                "repo",
                repoRoot,
                "main",
                "127.80.1.10",
                "DevWT-main",
                DevwtContextStatus.Active)
        ]));
        var git = new FakeGitInspector(new GitRepositoryInfo(
            repoRoot,
            Path.Combine(repoRoot, ".git"),
            [
                new GitWorktreeInfo(repoRoot, "main"),
                new GitWorktreeInfo(worktreeRoot, "feature/demo")
            ]));
        var hookRuntime = new RecordingHookRuntimeConfigurator();
        var materializer = new RecordingMaterializer();
        var synchronizer = new DevwtWorktreeSynchronizer(
            store,
            new DevwtManager(store, git, hookRuntime),
            git,
            materializer);

        var registered = synchronizer.SyncOnce();

        Assert.Equal(1, registered);
        Assert.Contains(store.LoadContexts().Contexts, context =>
            context.WorktreeRootPath.Equals(worktreeRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(worktreeRoot, materializer.Repaired);
    }

    [Fact]
    public void Hook_runtime_reconciler_reconfigures_existing_active_contexts()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-volo", "volo", @"C:\repos\volo", @"C:\repos\volo\.git", []);
        var active = new DevwtContext("ctx-volo", repository.Id, "volo", @"C:\repos\volo", "main", "127.80.1.10", "DevWT-runtime", DevwtContextStatus.Active);
        var paused = new DevwtContext("ctx-paused", repository.Id, "volo-paused", @"C:\repos\volo-paused", "feature", "127.80.1.11", "DevWT-runtime-paused", DevwtContextStatus.Paused);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([active, paused]));
        var hookRuntime = new RecordingHookRuntimeConfigurator();
        var reconciler = new DevwtHookRuntimeReconciler(store, hookRuntime);

        var count = reconciler.ReconcileOnce();

        Assert.Equal(1, count);
        Assert.Equal(DevwtPortShift.Normalize(active), Assert.Single(hookRuntime.Configured));
    }

    [Fact]
    public void Repository_pause_and_resume_update_all_and_only_its_contexts()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        store.SaveRepositories(new DevwtRepositoryState([
            new DevwtRepository("repo-volo", "volo", @"C:\repos\volo", @"C:\repos\volo\.git", []),
            new DevwtRepository("repo-abp", "abp", @"C:\repos\abp", @"C:\repos\abp\.git", [])
        ]));
        store.SaveContexts(new DevwtContextState([
            new DevwtContext("ctx-volo-main", "repo-volo", "volo", @"C:\repos\volo", "main", "127.0.0.1", "DevWT-volo-main", DevwtContextStatus.Active),
            new DevwtContext("ctx-volo-feature", "repo-volo", "volo-feature", @"C:\work\volo-feature", "feature", "127.0.0.1", "DevWT-volo-feature", DevwtContextStatus.Active),
            new DevwtContext("ctx-abp", "repo-abp", "abp", @"C:\repos\abp", "main", "127.0.0.1", "DevWT-abp", DevwtContextStatus.Active)
        ]));
        var manager = new DevwtManager(
            store,
            new FakeGitInspector(new GitRepositoryInfo(@"C:\repos\volo", @"C:\repos\volo\.git", [])),
            new RecordingHookRuntimeConfigurator());

        manager.SetPaused("repo-volo", worktreePath: null, paused: true);

        Assert.All(
            store.LoadContexts().Contexts.Where(context => context.RepositoryId == "repo-volo"),
            context => Assert.Equal(DevwtContextStatus.Paused, context.Status));
        Assert.Equal(
            DevwtContextStatus.Active,
            Assert.Single(store.LoadContexts().Contexts, context => context.RepositoryId == "repo-abp").Status);

        manager.SetPaused("volo", worktreePath: null, paused: false);

        Assert.All(
            store.LoadContexts().Contexts.Where(context => context.RepositoryId == "repo-volo"),
            context => Assert.Equal(DevwtContextStatus.Active, context.Status));
    }

    [Fact]
    public void Manager_add_repo_installs_hook_registers_existing_worktrees_and_configures_hook_runtime()
    {
        using var temp = new TempDirectory();
        var repoRoot = Path.Combine(temp.Path, "volo");
        var worktreeRoot = Path.Combine(temp.Path, "volo-feature");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(worktreeRoot);
        var git = new FakeGitInspector(
            new GitRepositoryInfo(
                RootPath: repoRoot,
                GitCommonDir: Path.Combine(repoRoot, ".git"),
                Worktrees:
                [
                    new GitWorktreeInfo(repoRoot, "main"),
                    new GitWorktreeInfo(worktreeRoot, "feature/demo")
                ]));
        var hookRuntime = new RecordingHookRuntimeConfigurator();
        var manager = new DevwtManager(new DevwtStateStore(temp.Path), git, hookRuntime);

        var result = manager.AddRepository(new AddRepositoryRequest(
            WorkingDirectory: repoRoot,
            Name: "volo",
            LinkedRepositories: []));

        Assert.Equal("volo", result.Repository.Name);
        Assert.Equal(2, result.Contexts.Count);
        Assert.All(result.Contexts, context =>
        {
            Assert.Equal("127.0.0.1", context.AssignedIp);
            Assert.InRange(context.AssignedPortBase, 1, 65535);
        });
        Assert.Equal(2, hookRuntime.Configured.Count);
        Assert.True(git.HooksDirectoryEnsured);
        Assert.True(File.Exists(Path.Combine(repoRoot, ".git", "hooks", "post-checkout")));
    }

    [Fact]
    public void Manager_add_repo_is_idempotent_for_existing_git_common_dir_from_worktree()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repoRoot = Path.Combine(temp.Path, "tab-a");
        var worktreeRoot = Path.Combine(temp.Path, "tab-a-test");
        var gitCommonDir = Path.Combine(temp.Path, ".git");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(worktreeRoot);
        Directory.CreateDirectory(gitCommonDir);
        var existing = new DevwtRepository("repo-tab", "tab-a", repoRoot, gitCommonDir, []);
        store.SaveRepositories(new DevwtRepositoryState([
            existing,
            new DevwtRepository("repo-duplicate", "tab-a-test", worktreeRoot, gitCommonDir, [])
        ]));
        store.SaveContexts(new DevwtContextState([
            new DevwtContext("ctx-tab-a", existing.Id, "tab-a", repoRoot, "main", "127.80.1.10", "DevWT-a", DevwtContextStatus.Active),
            new DevwtContext("ctx-tab-a-test", existing.Id, "tab-a-test", worktreeRoot, "feature", "127.80.1.11", "DevWT-b", DevwtContextStatus.Active),
            new DevwtContext("ctx-duplicate", "repo-duplicate", "tab-a-test", worktreeRoot, "feature", "127.80.9.99", "DevWT-duplicate", DevwtContextStatus.Active)
        ]));
        var git = new FakeGitInspector(
            new GitRepositoryInfo(
                RootPath: worktreeRoot,
                GitCommonDir: gitCommonDir,
                Worktrees:
                [
                    new GitWorktreeInfo(repoRoot, "main"),
                    new GitWorktreeInfo(worktreeRoot, "feature")
                ]));
        var manager = new DevwtManager(store, git, new RecordingHookRuntimeConfigurator());

        var result = manager.AddRepository(new AddRepositoryRequest(
            WorkingDirectory: worktreeRoot,
            Name: null,
            LinkedRepositories: []));

        var repository = Assert.Single(store.LoadRepositories().Repositories);
        Assert.Equal(existing.Id, repository.Id);
        Assert.Equal("tab-a", repository.Name);
        Assert.Equal(repoRoot, repository.RootPath);
        Assert.Equal(2, store.LoadContexts().Contexts.Count);
        Assert.All(store.LoadContexts().Contexts, context => Assert.Equal(existing.Id, context.RepositoryId));
        Assert.Equal(["tab-a", "tab-a-test"], store.LoadContexts().Contexts.Select(context => context.Name).Order().ToArray());
        Assert.Equal(existing.Id, result.Repository.Id);
        Assert.Equal(2, result.Contexts.Count);
    }

    [Fact]
    public void Context_description_upserts_by_nested_worktree_path_and_survives_repository_refresh()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repoRoot = Path.Combine(temp.Path, "volo");
        var nestedPath = Path.Combine(repoRoot, "src", "app");
        Directory.CreateDirectory(nestedPath);
        var git = new FakeGitInspector(
            new GitRepositoryInfo(
                RootPath: repoRoot,
                GitCommonDir: Path.Combine(repoRoot, ".git"),
                Worktrees: [new GitWorktreeInfo(repoRoot, "feature/review")]));
        var manager = new DevwtManager(store, git, new RecordingHookRuntimeConfigurator());
        manager.AddRepository(new AddRepositoryRequest(repoRoot, "volo", []));
        var handler = new DevwtControlHandler(manager, store);

        var first = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.DescribeContext,
            WorktreePath: nestedPath,
            ContextDescription: "Review driver code"));
        var second = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.DescribeContext,
            WorktreePath: repoRoot,
            ContextDescription: "PR 22558 review"));
        manager.AddRepository(new AddRepositoryRequest(repoRoot, "volo", []));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("PR 22558 review", Assert.Single(store.LoadContexts().Contexts).Description);
        Assert.Equal(
            "PR 22558 review",
            Assert.Single(new DevwtWebUiStatusProvider(store).Build().Contexts).Description);

        var clear = handler.Handle(new DevwtControlRequest(
            DevwtControlOperation.DescribeContext,
            WorktreePath: repoRoot,
            ClearContextDescription: true));

        Assert.Equal(0, clear.ExitCode);
        Assert.Null(Assert.Single(store.LoadContexts().Contexts).Description);
    }

    [Fact]
    public void Context_description_creates_a_missing_context_for_a_registered_repository()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repoRoot = Path.Combine(temp.Path, "volo");
        var gitCommonDir = Path.Combine(repoRoot, ".git");
        Directory.CreateDirectory(repoRoot);
        store.SaveRepositories(new DevwtRepositoryState([
            new DevwtRepository("repo-volo", "volo", repoRoot, gitCommonDir, [])
        ]));
        var git = new FakeGitInspector(
            new GitRepositoryInfo(
                RootPath: repoRoot,
                GitCommonDir: gitCommonDir,
                Worktrees: [new GitWorktreeInfo(repoRoot, "feature/review")]));
        var manager = new DevwtManager(store, git, new RecordingHookRuntimeConfigurator());

        var context = manager.SetDescription(repoRoot, "Review driver code");

        Assert.Equal("Review driver code", context.Description);
        Assert.Equal("Review driver code", Assert.Single(store.LoadContexts().Contexts).Description);
        Assert.Equal(repoRoot, context.WorktreeRootPath);
    }

    [Fact]
    public void Manager_remove_drops_state_even_when_hook_runtime_cleanup_warns()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var repository = new DevwtRepository("repo-volo", "volo", @"C:\repos\volo", @"C:\repos\volo\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([repository]));
        store.SaveContexts(new DevwtContextState([
            new DevwtContext("ctx-volo", repository.Id, "volo", @"C:\repos\volo", "main", "127.80.1.10", "DevWT-runtime", DevwtContextStatus.Active)
        ]));
        var manager = new DevwtManager(
            store,
            new FakeGitInspector(new GitRepositoryInfo(repository.RootPath, repository.GitCommonDir, [])),
            new FailingRemoveHookRuntimeConfigurator());

        var result = manager.RemoveRepository("volo");

        Assert.Empty(store.LoadRepositories().Repositories);
        Assert.Empty(store.LoadContexts().Contexts);
        Assert.Contains("Hook runtime cleanup warning", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Manager_remove_without_repo_name_drops_only_current_repository()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);
        var volo = new DevwtRepository("repo-volo", "volo", @"C:\repos\volo", @"C:\repos\volo\.git", []);
        var abp = new DevwtRepository("repo-abp", "abp", @"C:\repos\abp", @"C:\repos\abp\.git", []);
        store.SaveRepositories(new DevwtRepositoryState([volo, abp]));
        store.SaveContexts(new DevwtContextState([
            new DevwtContext("ctx-volo", volo.Id, "volo", @"C:\repos\volo", "main", "127.80.1.10", "DevWT-volo", DevwtContextStatus.Active),
            new DevwtContext("ctx-abp", abp.Id, "abp", @"C:\repos\abp", "main", "127.80.1.11", "DevWT-abp", DevwtContextStatus.Active)
        ]));
        var manager = new DevwtManager(
            store,
            new FakeGitInspector(new GitRepositoryInfo(volo.RootPath, volo.GitCommonDir, [])),
            new RecordingHookRuntimeConfigurator());

        var result = manager.RemoveRepository(repositoryName: null, worktreePath: @"C:\repos\volo\src");

        Assert.Equal(1, result.RemovedRepositories);
        Assert.Equal(1, result.RemovedContexts);
        Assert.Equal(abp.Id, Assert.Single(store.LoadRepositories().Repositories).Id);
        Assert.Equal("ctx-abp", Assert.Single(store.LoadContexts().Contexts).Id);
    }

    [Fact]
    public void Git_inspector_marks_selected_repositories_safe_for_service_owned_git_calls()
    {
        var runner = new RecordingCommandRunner(
            new CommandResult(0, "C:/repos/volo\n"),
            new CommandResult(0, ".git\n"),
            new CommandResult(0, "worktree C:/repos/volo\nHEAD 123\nbranch refs/heads/main\n"),
            new CommandResult(0, "main\n"));
        var inspector = new GitInspector(runner);

        inspector.InspectRepository(@"C:\repos\volo");

        Assert.All(runner.Commands, command =>
        {
            Assert.Contains("-c", command);
            Assert.Contains("safe.directory=*", command);
        });
    }

    [Fact]
    public void Control_pipe_security_allows_non_admin_authenticated_users()
    {
        var security = DevwtNamedPipeControlServer.CreatePipeSecurity();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        Assert.Contains(rules.Cast<System.Security.AccessControl.AuthorizationRule>(), rule =>
            rule.IdentityReference == authenticatedUsers);
    }

    [Fact]
    public void Web_ui_shell_is_a_tabbed_management_console()
    {
        var html = DevwtWebUiAssets.RenderShell();

        Assert.Contains("data-action=\"pause\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"resume\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"pause-repository\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"resume-repository\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"set-active-target\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"clear-active-target\"", html, StringComparison.Ordinal);
        Assert.Contains("clearActivePort", html, StringComparison.Ordinal);
        Assert.Contains("removeRepo(${jsArg(repo.id)}, ${jsArg(repo.name)})", html, StringComparison.Ordinal);
        Assert.Contains("${html(repo.id)}", html, StringComparison.Ordinal);
        Assert.Contains("role=\"tablist\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tab=\"overview\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tab=\"routing\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tab=\"contexts\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tab=\"activity\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tab=\"tools\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tab=\"settings\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-overview\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-routing\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-contexts\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-activity\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-tools\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-settings\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"status-strip\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"routing-mode-global\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"routing-mode-per-port\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"global-context-select\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"port-routing-groups\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"activity-search\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"activity-reason-filter\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"activity-view-grouped\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"activity-view-timeline\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"activity-grouped\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"activity-timeline\"", html, StringComparison.Ordinal);
        Assert.Contains("localStorage.getItem('devwt.activityView')", html, StringComparison.Ordinal);
        Assert.Contains("function setActivityView", html, StringComparison.Ordinal);
        Assert.Contains("function groupActivityHistory", html, StringComparison.Ordinal);
        Assert.Contains("function renderGroupedActivity", html, StringComparison.Ordinal);
        Assert.Contains("function renderTimelineActivity", html, StringComparison.Ordinal);
        Assert.Contains("function scopeTargetSummary", html, StringComparison.Ordinal);
        Assert.Contains("function observedPortLabel", html, StringComparison.Ordinal);
        Assert.Contains("data-scope-key=", html, StringComparison.Ordinal);
        Assert.Contains("status.contexts.filter(context => context.status === 'Active')", html, StringComparison.Ordinal);
        Assert.Contains("historyRouteMatches(route, entry)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ontoggle='rememberActivityScope(this, ${jsArg(key)})'", html, StringComparison.Ordinal);
        Assert.Contains("localStorage.getItem('devwt.activeTab')", html, StringComparison.Ordinal);
        Assert.Contains(".global-routing[hidden], .port-groups[hidden]", html, StringComparison.Ordinal);
        Assert.Contains("id=\"context-search\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"filter-ports\"", html, StringComparison.Ordinal);
        Assert.Contains("status.routes", html, StringComparison.Ordinal);
        Assert.Contains("groupRoutesByPort", html, StringComparison.Ordinal);
        Assert.Contains("class=\"routing-port-nav\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"routing-detail\"", html, StringComparison.Ordinal);
        Assert.Contains("function selectRoutingPort", html, StringComparison.Ordinal);
        Assert.Contains("let selectedRoutingPort = null", html, StringComparison.Ordinal);
        Assert.Contains("class=\"activity-pane\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"activity-inspector\"", html, StringComparison.Ordinal);
        Assert.Contains("function selectActivityScope", html, StringComparison.Ordinal);
        Assert.Contains("function renderActivePanel", html, StringComparison.Ordinal);
        Assert.Contains("function renderContextsView", html, StringComparison.Ordinal);
        Assert.Contains("function renderActivityView", html, StringComparison.Ordinal);
        Assert.Contains("class=\"table-wrap context-table-wrap\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<details class=\"activity-scope", html, StringComparison.Ordinal);
        Assert.Contains("endpointLabel(route)", html, StringComparison.Ordinal);
        Assert.Contains("endpointUrlHost(route)", html, StringComparison.Ordinal);
        Assert.Contains("http://${html(host)}:", html, StringComparison.Ordinal);
        Assert.Contains("https://${html(host)}:", html, StringComparison.Ordinal);
        Assert.Contains("TCP", html, StringComparison.Ordinal);
        Assert.Contains("runtimeName", html, StringComparison.Ordinal);
        Assert.Contains("Runtime Backends", html, StringComparison.Ordinal);
        Assert.Contains("Hook-core runtime", html, StringComparison.Ordinal);
        Assert.Contains("Browser Missing-Port Fallback", html, StringComparison.Ordinal);
        Assert.Contains("id=\"browser-fallback-off\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"browser-fallback-on\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"set-browser-fallback-on-missing-port\"", html, StringComparison.Ordinal);
        Assert.Contains("renderBrowserFallbackOnMissingPort", html, StringComparison.Ordinal);
        Assert.Contains("setBrowserFallbackOnMissingPort", html, StringComparison.Ordinal);
        Assert.DoesNotContain("browser-port-redirect-form", html, StringComparison.Ordinal);
        Assert.Contains("No contexts match the current filter", html, StringComparison.Ordinal);
        Assert.Contains("Status unavailable", html, StringComparison.Ordinal);
        Assert.Contains("/hubs/status", html, StringComparison.Ordinal);
        Assert.Contains("connectStatusSocket", html, StringComparison.Ordinal);
        Assert.Contains("SignalR status stream", html, StringComparison.Ordinal);
        Assert.Contains("Session Rules", html, StringComparison.Ordinal);
        Assert.Contains("id=\"session-rule-form\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"add-session-rule\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"remove-session-rule\"", html, StringComparison.Ordinal);
        Assert.Contains("renderSessionRules", html, StringComparison.Ordinal);
        Assert.Contains("set-https-proxy-mode", html, StringComparison.Ordinal);
        Assert.Contains("setHttpsProxyMode", html, StringComparison.Ordinal);
        Assert.Contains("httpsProxyModeFor", html, StringComparison.Ordinal);
        Assert.Contains("TCP handling mode for", html, StringComparison.Ordinal);
        Assert.Contains("HTTP Inspect", html, StringComparison.Ordinal);
        Assert.Contains("TLS Tunnel", html, StringComparison.Ordinal);
        Assert.Contains(">Raw</option>", html, StringComparison.Ordinal);
        Assert.Contains("sessionIdentityKind", html, StringComparison.Ordinal);
        Assert.Contains("sessionMatchKind", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"\" selected disabled>Session selector</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"env\">Environment variable</option>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("isSessionRuleApplicationScoped", html, StringComparison.Ordinal);
        Assert.Contains("id=\"connection-history\"", html, StringComparison.Ordinal);
        Assert.Contains("renderActivity", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"set-process-target\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"set-process-port-target\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"set-image-context-target\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"set-application-target\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"set-session-context-target\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"set-session-port-target\"", html, StringComparison.Ordinal);
        Assert.Contains("processTargets", html, StringComparison.Ordinal);
        Assert.Contains("processPortTargets", html, StringComparison.Ordinal);
        Assert.Contains("applicationContextTargets", html, StringComparison.Ordinal);
        Assert.Contains("sessionContextTargets", html, StringComparison.Ordinal);
        Assert.Contains("sessionPortTargets", html, StringComparison.Ordinal);
        Assert.Contains("setApplicationTarget", html, StringComparison.Ordinal);
        Assert.Contains("contextDisplayName", html, StringComparison.Ordinal);
        Assert.Contains("context.description", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"add-repository\"", html, StringComparison.Ordinal);
        Assert.Contains("addLinkedRepositoryInput", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"describe-context\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"check-port\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"find-port-processes\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"add-ide-watch\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"link-map\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"stop-proxy-child\"", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"kill-proxy-child\"", html, StringComparison.Ordinal);
        Assert.Contains("function renderOverview", html, StringComparison.Ordinal);
        Assert.Contains("function renderTools", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sandboxie", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NoSecurityIsolation", html, StringComparison.Ordinal);
        Assert.DoesNotContain("devwt sandboxie", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("setInterval(refresh, 1500)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON.stringify(status.routing", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<pre id=\"routing\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("target-scheme-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("scheme-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Active Proxy Target", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"active-target-summary\"", html, StringComparison.Ordinal);
    }

    private static DevwtContext Context(string id, string repoId, string root, string ip) =>
        new(id, repoId, Path.GetFileName(root), root, "feature/demo", ip, "DevWT-runtime", DevwtContextStatus.Active);

    private sealed class FakeGitInspector(GitRepositoryInfo info) : IGitInspector
    {
        public bool HooksDirectoryEnsured { get; private set; }

        public GitRepositoryInfo InspectRepository(string workingDirectory) => info;

        public string EnsureHooksDirectory(string workingDirectory, GitRepositoryInfo repository)
        {
            HooksDirectoryEnsured = true;
            return Path.Combine(repository.GitCommonDir, "hooks");
        }
    }

    private class RecordingHookRuntimeConfigurator : IHookRuntimeConfigurator
    {
        public List<DevwtContext> Configured { get; } = [];

        public void Configure(DevwtRepository repository, DevwtContext context)
        {
            Configured.Add(context);
        }

        public virtual void Remove(DevwtContext context)
        {
        }
    }

    private sealed class FailingRemoveHookRuntimeConfigurator : RecordingHookRuntimeConfigurator
    {
        public override void Remove(DevwtContext context)
        {
            throw new IOException("delete failed");
        }
    }

    private sealed class RecordingMaterializer : IWorktreeMaterializer
    {
        public List<string> Repaired { get; } = [];

        public bool RepairMissingTrackedFiles(string worktreePath)
        {
            Repaired.Add(worktreePath);
            return true;
        }
    }

    private sealed class FixedListenerSource(IReadOnlyList<ListenerObservation> listeners) : IListenerObservationSource
    {
        public IReadOnlyList<ListenerObservation> Read() => listeners;
    }

    private sealed class NullActiveTcpConnectionSource : IActiveTcpConnectionSource
    {
        public int? TryFindOwningProcess(IPEndPoint clientEndPoint, IPEndPoint gatewayEndPoint) => null;
    }

    private sealed class FixedActiveTcpConnectionSource(int processId) : IActiveTcpConnectionSource
    {
        public int? TryFindOwningProcess(IPEndPoint clientEndPoint, IPEndPoint gatewayEndPoint) => processId;
    }

    private sealed class FixedActiveUdpEndpointSource(int processId) : IActiveUdpEndpointSource
    {
        public int? TryFindOwningProcess(IPEndPoint localEndPoint) => processId;
    }

    private sealed class ThrowingActiveUdpEndpointSource : IActiveUdpEndpointSource
    {
        public int? TryFindOwningProcess(IPEndPoint localEndPoint) =>
            throw new InvalidOperationException("Single-target UDP routing must not inspect the owning process.");
    }

    private sealed class RecordingActiveTcpConnectionSource(int processId) : IActiveTcpConnectionSource
    {
        public int Calls { get; private set; }

        public int? TryFindOwningProcess(IPEndPoint clientEndPoint, IPEndPoint gatewayEndPoint)
        {
            Calls++;
            return processId;
        }
    }

    private sealed class RecordingProcessController : IDevwtProcessController
    {
        public List<(int ProcessId, bool Force)> Requests { get; } = [];

        public DevwtProcessStopResult Stop(int processId, bool force)
        {
            Requests.Add((processId, force));
            return new DevwtProcessStopResult(processId, force, true, null);
        }
    }

    private sealed class EmptyProcessObservationSource : IProcessObservationSource
    {
        public IReadOnlyList<ProcessObservation> Read() => [];
    }

    private sealed class ThrowingProcessObservationSource : IProcessObservationSource
    {
        public IReadOnlyList<ProcessObservation> Read() =>
            throw new InvalidOperationException("Single-target routing must not inspect the process table.");
    }

    private sealed class FixedProcessObservationSource(IReadOnlyList<ProcessObservation> processes) : IProcessObservationSource
    {
        public IReadOnlyList<ProcessObservation> Read() => processes;
    }

    private sealed class RecordingProcessObservationSource(IReadOnlyList<ProcessObservation> processes) : IProcessObservationSource
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ProcessObservation> Read()
        {
            Calls++;
            return processes;
        }
    }

    private sealed class DelayedProcessObservationSource(IReadOnlyList<ProcessObservation> processes) : IProcessObservationSource
    {
        private int _calls;

        public IReadOnlyList<ProcessObservation> Read() =>
            Interlocked.Increment(ref _calls) == 1 ? [] : processes;
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int FreeDualStackTcpPort()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var ipv4 = new TcpListener(IPAddress.Loopback, 0);
            ipv4.Start();
            var port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
            try
            {
                using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
                ipv6.Start();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException("Could not reserve a TCP port on both localhost address families.");
    }

    private static Type DevwtYarpProxyHostType()
    {
        return Assert.IsAssignableFrom<Type>(
            typeof(DevwtGatewayServer).GetNestedType(
                "DevwtYarpProxyHost",
                BindingFlags.NonPublic));
    }

    private static async Task<WebApplication> StartHttp2BackendAsync(
        int port,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http2));
        var application = builder.Build();
        application.Run(async context =>
        {
            context.Response.ContentLength = 3;
            await context.Response.WriteAsync("h2c", context.RequestAborted);
        });
        await application.StartAsync(cancellationToken);
        return application;
    }

    private static async Task<WebApplication> StartHttpBackendAsync(
        int port,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1));
        var application = builder.Build();
        application.Run(async context =>
        {
            context.Response.ContentLength = 10;
            await context.Response.WriteAsync("plain-http", context.RequestAborted);
        });
        await application.StartAsync(cancellationToken);
        return application;
    }

    private static int FreeUdpPort()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitForPortAsync(int port)
    {
        await WaitForPortAsync(IPAddress.Loopback, port);
    }

    private static async Task WaitForPortAsync(IPAddress address, int port)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address, port, cts.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, cts.Token);
            }
        }

        throw new TimeoutException($"Port {port} did not open.");
    }

    private static async Task<string> RequestGatewayAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"), cancellationToken);
        return await ReadHttpResponseAsync(stream, returnRawResponse: false, cancellationToken);
    }

    private static async Task<string> RequestGatewayRawAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"), cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[256];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
            if (Encoding.ASCII.GetString(memory.GetBuffer(), 0, (int)memory.Length).EndsWith("\r\n\r\nok", StringComparison.Ordinal))
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(memory.ToArray());
    }

    private static async Task AcceptHttpOnceAsync(TcpListener listener, string response, CancellationToken cancellationToken)
    {
        using var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = accepted.GetStream();
        var requestBuffer = new byte[128];
        using var request = new MemoryStream();
        while (!DevwtGatewayHttpHeaders.HasCompleteHeaderBlock(request.GetBuffer().AsSpan(0, (int)request.Length)))
        {
            var received = await stream.ReadAsync(requestBuffer.AsMemory(0, requestBuffer.Length), cancellationToken);
            if (received == 0)
            {
                throw new IOException("Gateway connected to the target but closed before proxying request bytes.");
            }

            request.Write(requestBuffer, 0, received);
        }

        var payload = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\n\r\n{response}"), cancellationToken);
        await Task.Delay(100, cancellationToken);
    }

    private static async Task<string> AcceptHttpRequestOnceAsync(
        TcpListener listener,
        string response,
        CancellationToken cancellationToken)
    {
        using var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = accepted.GetStream();
        var request = await DevwtGatewayHttpHeaders.ReadHttpRequestStartAsync(
            stream,
            cancellationToken: cancellationToken);
        var payload = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{response}"), cancellationToken);
        return Encoding.ASCII.GetString(request);
    }

    private static async Task<string> AcceptTlsHttpOnceAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        string responseBody,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
            using var tls = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11]
            }, cancellationToken);
            byte[] request;
            try
            {
                request = await DevwtGatewayHttpHeaders.ReadHttpRequestStartAsync(
                    tls,
                    cancellationToken: cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }
            if (request.Length == 0)
            {
                continue;
            }

            var responseBytes = Encoding.ASCII.GetBytes(responseBody);
            await tls.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n{responseBody}"), cancellationToken);
            return Encoding.ASCII.GetString(request);
        }
    }

    private static async Task<string> AcceptWebSocketEchoOnceAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = accepted.GetStream();
        var requestBytes = await DevwtGatewayHttpHeaders.ReadHttpRequestStartAsync(
            stream,
            cancellationToken: cancellationToken);
        var request = Encoding.ASCII.GetString(requestBytes);
        var key = request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            ["Sec-WebSocket-Key:".Length..]
            .Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Connection: Upgrade\r\n"
            + "Upgrade: websocket\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), cancellationToken);

        var frameHeader = new byte[2];
        await stream.ReadExactlyAsync(frameHeader, cancellationToken);
        var payloadLength = frameHeader[1] & 0x7f;
        Assert.InRange(payloadLength, 0, 125);
        Assert.NotEqual(0, frameHeader[1] & 0x80);
        var mask = new byte[4];
        await stream.ReadExactlyAsync(mask, cancellationToken);
        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] ^= mask[index % mask.Length];
        }

        await stream.WriteAsync(new byte[] { 0x81, (byte)payload.Length }, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        return request;
    }

    private static async Task<string> ReadHttpResponseAsync(
        Stream stream,
        bool returnRawResponse,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        using var response = new MemoryStream();
        int? headerEnd = null;
        int? contentLength = null;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            response.Write(buffer, 0, read);
            var bytes = response.GetBuffer().AsSpan(0, (int)response.Length);
            if (headerEnd is null)
            {
                for (var index = 0; index <= bytes.Length - 4; index++)
                {
                    if (bytes[index..].StartsWith("\r\n\r\n"u8))
                    {
                        headerEnd = index + 4;
                        var headers = Encoding.ASCII.GetString(bytes[..headerEnd.Value]);
                        var contentLengthLine = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        contentLength = contentLengthLine is null
                            ? 0
                            : int.Parse(contentLengthLine["Content-Length:".Length..].Trim());
                        break;
                    }
                }
            }

            if (headerEnd is int bodyStart
                && contentLength is int bodyLength
                && response.Length >= bodyStart + bodyLength)
            {
                break;
            }
        }

        var result = response.ToArray();
        if (returnRawResponse || headerEnd is null)
        {
            return Encoding.ASCII.GetString(result);
        }

        return Encoding.ASCII.GetString(result, headerEnd.Value, contentLength ?? 0);
    }

    private static async Task AcceptHttpResponseOnceAsync(TcpListener listener, string response, CancellationToken cancellationToken)
    {
        using var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = accepted.GetStream();
        var requestBuffer = new byte[128];
        using var request = new MemoryStream();
        while (!DevwtGatewayHttpHeaders.HasCompleteHeaderBlock(request.GetBuffer().AsSpan(0, (int)request.Length)))
        {
            var received = await stream.ReadAsync(requestBuffer.AsMemory(0, requestBuffer.Length), cancellationToken);
            if (received == 0)
            {
                throw new IOException("Gateway connected to the target but closed before proxying request bytes.");
            }

            request.Write(requestBuffer, 0, received);
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        await Task.Delay(100, cancellationToken);
    }

    private sealed class RecordingCommandRunner(params CommandResult[] results) : ICommandRunner
    {
        private int _index;

        public List<IReadOnlyList<string>> Commands { get; } = [];

        public CommandResult Run(IReadOnlyList<string> arguments)
        {
            Commands.Add(arguments.ToArray());
            return _index < results.Length ? results[_index++] : new CommandResult(0);
        }
    }
}
