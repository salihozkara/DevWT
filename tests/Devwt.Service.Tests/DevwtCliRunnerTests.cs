using Devwt.Core;

namespace Devwt.Service.Tests;

public sealed class DevwtCliRunnerTests
{
    [Fact]
    public void Add_command_is_sent_to_control_client_instead_of_writing_state_directly()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("created repo sample\n", 0));

        var result = DevwtCliRunner.Execute(
            [
                "add",
                "--name",
                "sample",
                "--linked-repo",
                "shared",
                "--linked-repo-path",
                "../shared"
            ],
            @"C:\repos\sample",
            client);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("created repo sample\n", result.Output);
        var request = Assert.Single(client.Requests);
        Assert.Equal(DevwtControlOperation.AddRepository, request.Operation);
        Assert.Equal(@"C:\repos\sample", request.AddRepository!.WorkingDirectory);
        Assert.Equal("shared", Assert.Single(request.AddRepository.LinkedRepositories).Name);
    }

    [Fact]
    public void Pause_resume_remove_hook_and_link_map_are_sent_to_control_client()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(["pause", "--repo", "sample"], @"C:\repos\sample", client);
        DevwtCliRunner.Execute(["resume", "--worktree", "."], @"C:\repos\sample", client);
        DevwtCliRunner.Execute(["remove"], @"C:\repos\sample", client);
        DevwtCliRunner.Execute(["hook", "worktree-ready", "--repo-id", "repo-sample", "--path", "."], @"C:\work\sample", client);
        DevwtCliRunner.Execute(["link", "map", "--linked-repo", "shared", "--source", ".", "--target", "../shared"], @"C:\work\sample", client);

        Assert.Collection(
            client.Requests,
            request => Assert.Equal(DevwtControlOperation.Pause, request.Operation),
            request => Assert.Equal(DevwtControlOperation.Resume, request.Operation),
            request =>
            {
                Assert.Equal(DevwtControlOperation.RemoveRepository, request.Operation);
                Assert.Null(request.RepositoryName);
                Assert.Equal(@"C:\repos\sample", request.WorktreePath);
            },
            request => Assert.Equal(DevwtControlOperation.WorktreeReady, request.Operation),
            request => Assert.Equal(DevwtControlOperation.LinkMap, request.Operation));
    }

    [Fact]
    public void Context_description_is_sent_as_an_idempotent_worktree_update()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(
            ["context", "describe", "Review gateway routing"],
            @"C:\work\sample",
            client);
        DevwtCliRunner.Execute(
            ["context", "describe", "--clear"],
            @"C:\work\sample",
            client);

        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(DevwtControlOperation.DescribeContext, request.Operation);
                Assert.Equal(@"C:\work\sample", request.WorktreePath);
                Assert.Equal("Review gateway routing", request.ContextDescription);
                Assert.False(request.ClearContextDescription);
            },
            request =>
            {
                Assert.Equal(DevwtControlOperation.DescribeContext, request.Operation);
                Assert.Equal(@"C:\work\sample", request.WorktreePath);
                Assert.Null(request.ContextDescription);
                Assert.True(request.ClearContextDescription);
            });
    }

    [Fact]
    public void Port_commands_send_context_aware_queries()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(
            ["port", "process", "--port", "44334"],
            @"C:\work\sample\src",
            client);
        DevwtCliRunner.Execute(
            ["port", "check", "--port", "44334", "--context", "ctx-review"],
            @"C:\work\sample",
            client);

        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(DevwtControlOperation.FindPortProcesses, request.Operation);
                Assert.Equal(44334, request.PortQuery!.Port);
                Assert.Equal(@"C:\work\sample\src", request.PortQuery.WorkingDirectory);
                Assert.Null(request.PortQuery.ContextId);
            },
            request =>
            {
                Assert.Equal(DevwtControlOperation.CheckPort, request.Operation);
                Assert.Equal(44334, request.PortQuery!.Port);
                Assert.Equal(@"C:\work\sample", request.PortQuery.WorkingDirectory);
                Assert.Equal("ctx-review", request.PortQuery.ContextId);
            });
    }

    [Fact]
    public void Remove_by_repo_does_not_send_current_directory()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(["remove", "--repo", "sample"], @"C:\repos\other", client);

        var request = Assert.Single(client.Requests);
        Assert.Equal(DevwtControlOperation.RemoveRepository, request.Operation);
        Assert.Equal("sample", request.RepositoryName);
        Assert.Null(request.WorktreePath);
    }

    [Fact]
    public void Proxy_target_and_clear_are_sent_to_control_client()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(
            ["proxy", "target", "--context", "ctx-sample", "--port", "5025"],
            @"C:\repos\sample",
            client);
        DevwtCliRunner.Execute(["proxy", "clear"], @"C:\repos\sample", client);

        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(DevwtControlOperation.SetActiveTarget, request.Operation);
                Assert.Equal("ctx-sample", request.ActiveTarget!.ContextId);
                Assert.Equal(5025, request.ActiveTarget.Port);
                Assert.Equal("auto", request.ActiveTarget.Scheme);
            },
            request =>
            {
                Assert.Equal(DevwtControlOperation.SetActiveTarget, request.Operation);
                Assert.True(request.ClearActiveTarget);
            });
    }

    [Fact]
    public void Proxy_global_context_and_port_clear_are_sent_to_control_client()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(
            ["proxy", "context", "--context", "ctx-sample"],
            @"C:\repos\sample",
            client);
        DevwtCliRunner.Execute(
            ["proxy", "clear", "--port", "44334"],
            @"C:\repos\sample",
            client);

        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(DevwtControlOperation.SetActiveTarget, request.Operation);
                Assert.Equal(DevwtActiveTargetMode.GlobalContext, request.ActiveTargetMode);
                Assert.Equal("ctx-sample", request.GlobalActiveContextId);
            },
            request =>
            {
                Assert.Equal(DevwtControlOperation.SetActiveTarget, request.Operation);
                Assert.True(request.ClearActiveTarget);
                Assert.Equal(44334, request.Port);
            });
    }

    [Fact]
    public void Proxy_process_target_and_clear_are_sent_to_control_client()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(
            ["proxy", "process", "target", "--pid", "1234", "--context", "ctx-sample"],
            @"C:\repos\sample",
            client);
        DevwtCliRunner.Execute(
            ["proxy", "process", "clear", "--pid", "1234"],
            @"C:\repos\sample",
            client);

        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(DevwtControlOperation.SetProcessTarget, request.Operation);
                Assert.Equal(1234, request.ProcessTarget!.ProcessId);
                Assert.Equal("ctx-sample", request.ProcessTarget.ContextId);
            },
            request =>
            {
                Assert.Equal(DevwtControlOperation.SetProcessTarget, request.Operation);
                Assert.Equal(1234, request.ProcessId);
                Assert.True(request.ClearProcessTarget);
            });
    }

    [Fact]
    public void Proxy_child_stop_and_kill_are_sent_to_control_client()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(
            ["proxy", "child", "stop", "--port", "44334", "--context", "ctx-sample"],
            @"C:\repos\sample",
            client);
        DevwtCliRunner.Execute(
            ["proxy", "child", "kill", "--port", "44334", "--protocol", "udp"],
            @"C:\repos\sample",
            client);

        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(DevwtControlOperation.StopProxyChild, request.Operation);
                Assert.Equal("ctx-sample", request.ProxyChildTarget!.ContextId);
                Assert.Equal(44334, request.ProxyChildTarget.Port);
                Assert.Equal(GatewayRouteProtocol.Tcp, request.ProxyChildTarget.Protocol);
                Assert.False(request.ProxyChildTarget.Force);
            },
            request =>
            {
                Assert.Equal(DevwtControlOperation.StopProxyChild, request.Operation);
                Assert.Null(request.ProxyChildTarget!.ContextId);
                Assert.Equal(44334, request.ProxyChildTarget.Port);
                Assert.Equal(GatewayRouteProtocol.Udp, request.ProxyChildTarget.Protocol);
                Assert.True(request.ProxyChildTarget.Force);
            });
    }

    [Fact]
    public void Ide_watch_commands_are_sent_to_control_client()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(
            ["ide", "watch", "add", "--name", "Rider", "--path", @"C:\Tools\Rider\bin\rider64.exe"],
            @"C:\repos\sample",
            client);
        DevwtCliRunner.Execute(
            ["ide", "watch", "remove", "--name", "Rider"],
            @"C:\repos\sample",
            client);
        DevwtCliRunner.Execute(
            ["ide", "watch", "list"],
            @"C:\repos\sample",
            client);

        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(DevwtControlOperation.SetIdeWatch, request.Operation);
                Assert.Equal("Rider", request.IdeWatch!.Name);
                Assert.Equal(@"C:\Tools\Rider\bin\rider64.exe", request.IdeWatch.ImagePath);
            },
            request =>
            {
                Assert.Equal(DevwtControlOperation.RemoveIdeWatch, request.Operation);
                Assert.Equal("Rider", request.IdeWatchName);
            },
            request => Assert.Equal(DevwtControlOperation.ListIdeWatch, request.Operation));
    }

    [Fact]
    public void Store_app_ide_watch_commands_are_sent_to_control_client()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        DevwtCliRunner.Execute(
            ["ide", "watch", "add", "--name", "Codex", "--app-id", "OpenAI.Codex_2p2nqsd0c76g0!App"],
            @"C:\repos\sample",
            client);
        DevwtCliRunner.Execute(
            ["ide", "watch", "remove", "--package-family", "OpenAI.Codex_2p2nqsd0c76g0"],
            @"C:\repos\sample",
            client);

        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(DevwtControlOperation.SetIdeWatch, request.Operation);
                Assert.Equal("Codex", request.IdeWatch!.Name);
                Assert.Null(request.IdeWatch.ImagePath);
                Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!App", request.IdeWatch.AppId);
                Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", request.IdeWatch.PackageFamilyName);
            },
            request =>
            {
                Assert.Equal(DevwtControlOperation.RemoveIdeWatch, request.Operation);
                Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", request.IdeWatchPackageFamilyName);
            });
    }

    [Fact]
    public void Sandboxie_commands_return_help_without_control_request()
    {
        var client = new RecordingControlClient(new DevwtCommandResult("ok\n", 0));

        var result = DevwtCliRunner.Execute(["sandboxie", "status"], @"C:\repos\sample", client);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown command: sandboxie", result.Output, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    private sealed class RecordingControlClient(DevwtCommandResult result) : IDevwtControlClient
    {
        public List<DevwtControlRequest> Requests { get; } = [];

        public DevwtCommandResult Send(DevwtControlRequest request)
        {
            Requests.Add(request);
            return result;
        }
    }
}
