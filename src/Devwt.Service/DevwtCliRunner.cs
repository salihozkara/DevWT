using Devwt.Core;

namespace Devwt.Service;

public static class DevwtCliRunner
{
    public static DevwtCommandResult Execute(
        IReadOnlyList<string> args,
        string currentDirectory,
        IDevwtControlClient controlClient)
    {
        try
        {
            var command = DevwtCommandParser.Parse(args, currentDirectory);
            return command switch
            {
                AddRepositoryCommand add => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.AddRepository,
                    AddRepository: new AddRepositoryRequest(add.WorkingDirectory, add.Name, add.LinkedRepositories))),
                RemoveRepositoryCommand remove => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.RemoveRepository,
                    RepositoryName: remove.RepositoryName,
                    WorktreePath: string.IsNullOrWhiteSpace(remove.RepositoryName) ? remove.WorkingDirectory : null)),
                PauseCommand pause => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.Pause,
                    RepositoryName: pause.RepositoryName,
                    WorktreePath: pause.WorktreePath)),
                ResumeCommand resume => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.Resume,
                    RepositoryName: resume.RepositoryName,
                    WorktreePath: resume.WorktreePath)),
                DescribeContextCommand describe => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.DescribeContext,
                    WorktreePath: describe.WorktreePath,
                    ContextDescription: describe.Description,
                    ClearContextDescription: describe.Clear)),
                PortCommand port => controlClient.Send(new DevwtControlRequest(
                    port.Action == PortCommandAction.Process
                        ? DevwtControlOperation.FindPortProcesses
                        : DevwtControlOperation.CheckPort,
                    PortQuery: new DevwtPortQuery(port.Port, port.WorkingDirectory, port.ContextId))),
                WorktreeReadyHookCommand hook => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.WorktreeReady,
                    RepositoryId: hook.RepositoryId,
                    WorktreePath: hook.WorktreePath)),
                LinkMapCommand link => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.LinkMap,
                    LinkMap: new DevwtLinkMap(link.LinkedRepositoryName, link.SourceWorktreePath, link.TargetWorktreePath))),
                ProxyTargetCommand proxy => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.SetActiveTarget,
                    ActiveTarget: new DevwtActiveTarget(proxy.ContextId, proxy.Port, proxy.Scheme))),
                ProxyContextTargetCommand context => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.SetActiveTarget,
                    ActiveTargetMode: DevwtActiveTargetMode.GlobalContext,
                    GlobalActiveContextId: context.ContextId)),
                ProxyClearCommand clear => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.SetActiveTarget,
                    ClearActiveTarget: true,
                    Port: clear.Port)),
                ProxyProcessTargetCommand process => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.SetProcessTarget,
                    ProcessTarget: new DevwtProcessTarget(process.ProcessId, process.ContextId))),
                ProxyProcessClearCommand process => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.SetProcessTarget,
                    ProcessId: process.ProcessId,
                    ClearProcessTarget: true)),
                ProxyChildCommand child => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.StopProxyChild,
                    ProxyChildTarget: new DevwtProxyChildTarget(
                        child.ContextId,
                        child.Port,
                        ParseProtocol(child.Protocol),
                        child.Action == ProxyChildAction.Kill))),
                IdeWatchCommand { Action: IdeWatchAction.Add } ide => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.SetIdeWatch,
                    IdeWatch: new DevwtIdeWatch(ide.Name!, ide.ImagePath, ide.AppId, ide.PackageFamilyName))),
                IdeWatchCommand { Action: IdeWatchAction.Remove } ide => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.RemoveIdeWatch,
                    IdeWatchName: ide.Name,
                    IdeWatchImagePath: ide.ImagePath,
                    IdeWatchAppId: ide.AppId,
                    IdeWatchPackageFamilyName: ide.PackageFamilyName,
                    ClearIdeWatches: ide.All)),
                IdeWatchCommand { Action: IdeWatchAction.List } => controlClient.Send(new DevwtControlRequest(
                    DevwtControlOperation.ListIdeWatch)),
                StatusCommand => controlClient.Send(new DevwtControlRequest(DevwtControlOperation.Status)),
                HelpCommand help => new DevwtCommandResult(help.Message + Environment.NewLine, help.ExitCode),
                UiCommand => new DevwtCommandResult("Use `devwt service run --ui` or open the installed DevWT Web UI.\n", 0),
                _ => new DevwtCommandResult(DevwtCommandParser.HelpText + Environment.NewLine, 2)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException)
        {
            return new DevwtCommandResult(ex.Message + Environment.NewLine, 2);
        }
    }

    private static GatewayRouteProtocol ParseProtocol(string protocol) =>
        protocol.Equals("udp", StringComparison.OrdinalIgnoreCase)
            ? GatewayRouteProtocol.Udp
            : GatewayRouteProtocol.Tcp;
}
