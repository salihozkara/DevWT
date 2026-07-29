using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Diagnostics;
using Devwt.Core;

namespace Devwt.Service;

[JsonConverter(typeof(JsonStringEnumConverter<DevwtControlOperation>))]
public enum DevwtControlOperation
{
    AddRepository,
    RemoveRepository,
    Pause,
    Resume,
    DescribeContext,
    FindPortProcesses,
    CheckPort,
    WorktreeReady,
    LinkMap,
    SetActiveTarget,
    SetProcessTarget,
    SetApplicationTarget,
    SetSessionTarget,
    StopProxyChild,
    SetIdeWatch,
    RemoveIdeWatch,
    ListIdeWatch,
    SetSessionRule,
    RemoveSessionRule,
    ListSessionRule,
    SetBrowserFallbackOnMissingPort,
    SetBrowserMissingPortPolicy,
    SetHttpsProxyMode,
    RecordGatewayConnection,
    Status
}

public sealed record DevwtControlRequest(
    DevwtControlOperation Operation,
    AddRepositoryRequest? AddRepository = null,
    string? RepositoryName = null,
    string? WorktreePath = null,
    string? RepositoryId = null,
    string? ContextDescription = null,
    bool ClearContextDescription = false,
    DevwtLinkMap? LinkMap = null,
    DevwtActiveTarget? ActiveTarget = null,
    DevwtProcessTarget? ProcessTarget = null,
    DevwtProxyChildTarget? ProxyChildTarget = null,
    int? ProcessId = null,
    DevwtIdeWatch? IdeWatch = null,
    string? IdeWatchName = null,
    string? IdeWatchImagePath = null,
    string? IdeWatchAppId = null,
    string? IdeWatchPackageFamilyName = null,
    DevwtSessionRule? SessionRule = null,
    string? SessionRuleName = null,
    bool? BrowserFallbackOnMissingPort = null,
    DevwtBrowserMissingPortPolicy? BrowserMissingPortPolicy = null,
    bool ClearBrowserMissingPortPolicy = false,
    bool ClearIdeWatches = false,
    bool ClearActiveTarget = false,
    bool ClearProcessTarget = false,
    string? ActiveTargetBrowserKey = null,
    DevwtApplicationTarget? ApplicationTarget = null,
    string? ApplicationTargetKey = null,
    int? Port = null,
    bool ClearApplicationTarget = false,
    DevwtActiveTargetMode? ActiveTargetMode = null,
    string? GlobalActiveContextId = null,
    DevwtProcessPortTarget? ProcessPortTarget = null,
    bool ClearProcessPortTarget = false,
    DevwtApplicationContextTarget? ApplicationContextTarget = null,
    bool ClearApplicationContextTarget = false,
    DevwtSessionContextTarget? SessionContextTarget = null,
    DevwtSessionPortTarget? SessionPortTarget = null,
    string? SessionId = null,
    bool ClearSessionContextTarget = false,
    bool ClearSessionPortTarget = false,
    DevwtHttpsProxyEndpoint? HttpsProxyEndpoint = null,
    DevwtConnectionHistoryEntry? ConnectionHistoryEntry = null,
    DevwtPortQuery? PortQuery = null);

public sealed record DevwtCommandResult(string Output, int ExitCode);

public sealed record DevwtProxyChildTarget(
    string? ContextId,
    int Port,
    GatewayRouteProtocol Protocol,
    bool Force);

public sealed record DevwtPortQuery(
    int Port,
    string WorkingDirectory,
    string? ContextId);

public sealed record DevwtProcessStopResult(
    int ProcessId,
    bool Force,
    bool Exited,
    string? Message);

public interface IDevwtControlClient
{
    DevwtCommandResult Send(DevwtControlRequest request);
}

public interface IDevwtProcessController
{
    DevwtProcessStopResult Stop(int processId, bool force);
}

public sealed class DevwtProcessController : IDevwtProcessController
{
    public DevwtProcessStopResult Stop(int processId, bool force)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (force)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                return process.HasExited
                    ? new DevwtProcessStopResult(processId, force, true, null)
                    : new DevwtProcessStopResult(processId, force, false, $"Process {processId} did not exit after force kill.");
            }

            if (!process.CloseMainWindow())
            {
                return new DevwtProcessStopResult(processId, force, false, $"Process {processId} has no main window; use proxy child kill for force termination.");
            }

            process.WaitForExit(5000);
            return process.HasExited
                ? new DevwtProcessStopResult(processId, force, true, null)
                : new DevwtProcessStopResult(processId, force, false, $"Process {processId} did not exit after graceful close; use proxy child kill.");
        }
        catch (ArgumentException)
        {
            return new DevwtProcessStopResult(processId, force, true, $"Process {processId} is already gone.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new DevwtProcessStopResult(processId, force, false, ex.Message);
        }
    }
}

public sealed class DevwtControlHandler(
    DevwtManager manager,
    DevwtStateStore store,
    DevwtRouteSnapshotBuilder? routeSnapshotBuilder = null,
    IDevwtProcessController? processController = null,
    IDevwtConnectionHistorySink? connectionHistory = null)
{
    private static readonly ConcurrentDictionary<string, object> HandleGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _handleGate = HandleGates.GetOrAdd(Path.GetFullPath(store.StateRoot), _ => new object());

    public DevwtCommandResult Handle(DevwtControlRequest request)
    {
        lock (_handleGate)
        {
            return HandleSerialized(request);
        }
    }

    private DevwtCommandResult HandleSerialized(DevwtControlRequest request)
    {
        try
        {
            return request.Operation switch
            {
                DevwtControlOperation.AddRepository => AddRepository(request.AddRepository
                    ?? throw new ArgumentException("AddRepository request is missing payload.")),
                DevwtControlOperation.RemoveRepository => RemoveRepository(request.RepositoryName, request.WorktreePath),
                DevwtControlOperation.Pause => PauseResume(request.RepositoryName, request.WorktreePath, paused: true),
                DevwtControlOperation.Resume => PauseResume(request.RepositoryName, request.WorktreePath, paused: false),
                DevwtControlOperation.DescribeContext => DescribeContext(request),
                DevwtControlOperation.FindPortProcesses => QueryPort(
                    request.PortQuery ?? throw new ArgumentException("FindPortProcesses request is missing payload."),
                    checkOnly: false),
                DevwtControlOperation.CheckPort => QueryPort(
                    request.PortQuery ?? throw new ArgumentException("CheckPort request is missing payload."),
                    checkOnly: true),
                DevwtControlOperation.WorktreeReady => WorktreeReady(request),
                DevwtControlOperation.LinkMap => LinkMap(request.LinkMap
                    ?? throw new ArgumentException("LinkMap request is missing payload.")),
                DevwtControlOperation.SetActiveTarget => SetActiveTarget(request),
                DevwtControlOperation.SetProcessTarget => SetProcessTarget(request),
                DevwtControlOperation.SetApplicationTarget => SetApplicationTarget(request),
                DevwtControlOperation.SetSessionTarget => SetSessionTarget(request),
                DevwtControlOperation.StopProxyChild => StopProxyChild(request.ProxyChildTarget
                    ?? throw new ArgumentException("StopProxyChild request is missing payload.")),
                DevwtControlOperation.SetIdeWatch => SetIdeWatch(request.IdeWatch
                    ?? throw new ArgumentException("SetIdeWatch request is missing payload.")),
                DevwtControlOperation.RemoveIdeWatch => RemoveIdeWatch(request),
                DevwtControlOperation.ListIdeWatch => ListIdeWatch(),
                DevwtControlOperation.SetSessionRule => SetSessionRule(request.SessionRule
                    ?? throw new ArgumentException("SetSessionRule request is missing payload.")),
                DevwtControlOperation.RemoveSessionRule => RemoveSessionRule(request.SessionRuleName
                    ?? throw new ArgumentException("RemoveSessionRule requires a rule name.")),
                DevwtControlOperation.ListSessionRule => ListSessionRule(),
                DevwtControlOperation.SetBrowserFallbackOnMissingPort => SetBrowserFallbackOnMissingPort(
                    request.BrowserFallbackOnMissingPort
                    ?? throw new ArgumentException("SetBrowserFallbackOnMissingPort request is missing payload.")),
                DevwtControlOperation.SetBrowserMissingPortPolicy => SetBrowserMissingPortPolicy(request),
                DevwtControlOperation.SetHttpsProxyMode => SetHttpsProxyMode(
                    request.HttpsProxyEndpoint
                    ?? throw new ArgumentException("SetHttpsProxyMode request is missing payload.")),
                DevwtControlOperation.RecordGatewayConnection => RecordGatewayConnection(
                    request.ConnectionHistoryEntry
                    ?? throw new ArgumentException("RecordGatewayConnection request is missing payload.")),
                DevwtControlOperation.Status => Status(),
                _ => new DevwtCommandResult("unknown operation\n", 2)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException)
        {
            return new DevwtCommandResult(ex.Message + Environment.NewLine, 2);
        }
    }

    private DevwtCommandResult RecordGatewayConnection(DevwtConnectionHistoryEntry entry)
    {
        connectionHistory?.Add(entry);
        return new DevwtCommandResult(string.Empty, 0);
    }

    private DevwtCommandResult SetHttpsProxyMode(DevwtHttpsProxyEndpoint endpoint)
    {
        if (endpoint.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("TCP handling endpoint port must be between 1 and 65535.");
        }
        if (!System.Net.IPAddress.TryParse(endpoint.Ip, out var address))
        {
            throw new ArgumentException("TCP handling endpoint IP is invalid.");
        }

        var normalized = endpoint with { Ip = address.ToString() };
        var routing = store.LoadRouting();
        var endpoints = routing.HttpsProxyEndpoints
            .Where(existing => existing.Port != normalized.Port
                || !existing.Ip.Equals(normalized.Ip, StringComparison.OrdinalIgnoreCase))
            .Append(normalized)
            .ToArray();
        store.SaveRouting(routing with { HttpsProxyEndpoints = endpoints });
        return new DevwtCommandResult(
            $"set TCP handling mode {normalized.Ip}:{normalized.Port} {normalized.Mode}{Environment.NewLine}",
            0);
    }

    private DevwtCommandResult AddRepository(AddRepositoryRequest request)
    {
        var result = manager.AddRepository(request);
        return new DevwtCommandResult(
            $"registered repo {result.Repository.Name} ({result.Contexts.Count} worktrees){Environment.NewLine}",
            0);
    }

    private DevwtCommandResult RemoveRepository(string? repositoryName, string? worktreePath)
    {
        var result = manager.RemoveRepository(repositoryName, worktreePath);
        var lines = new List<string>
        {
            $"removed {result.RemovedRepositories} repo registration(s), {result.RemovedContexts} context(s)"
        };
        lines.AddRange(result.Warnings);
        return new DevwtCommandResult(string.Join(Environment.NewLine, lines) + Environment.NewLine, 0);
    }

    private DevwtCommandResult PauseResume(string? repositoryName, string? worktreePath, bool paused)
    {
        manager.SetPaused(repositoryName, worktreePath, paused);
        return new DevwtCommandResult((paused ? "paused" : "resumed") + Environment.NewLine, 0);
    }

    private DevwtCommandResult DescribeContext(DevwtControlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorktreePath))
        {
            throw new ArgumentException("DescribeContext requires a worktree path.");
        }
        if (request.ClearContextDescription && !string.IsNullOrWhiteSpace(request.ContextDescription))
        {
            throw new ArgumentException("DescribeContext clear does not accept a description.");
        }
        if (!request.ClearContextDescription && string.IsNullOrWhiteSpace(request.ContextDescription))
        {
            throw new ArgumentException("DescribeContext requires a description or clear flag.");
        }

        var context = manager.SetDescription(
            request.WorktreePath,
            request.ClearContextDescription ? null : request.ContextDescription);
        return new DevwtCommandResult(
            request.ClearContextDescription
                ? $"cleared context description for {context.WorktreeRootPath}{Environment.NewLine}"
                : $"described context {context.WorktreeRootPath}: {context.Description}{Environment.NewLine}",
            0);
    }

    private DevwtCommandResult QueryPort(DevwtPortQuery query, bool checkOnly)
    {
        if (query.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("Port query requires a port between 1 and 65535.");
        }

        var context = ResolvePortContext(query);
        var bindings = ResolveRouteSnapshotBuilder().FindLiveBindings(context.Id, query.Port);
        var contextLabel = string.IsNullOrWhiteSpace(context.Description)
            ? context.Name
            : context.Description;
        var contextDisplay = $"\"{contextLabel}\" ({context.Id})";
        if (bindings.Count == 0)
        {
            return new DevwtCommandResult(
                $"no application is listening in context {contextDisplay} on original port {query.Port}{Environment.NewLine}",
                1);
        }

        var processDetails = bindings
            .Select(binding => binding.ProcessId)
            .Distinct()
            .ToDictionary(processId => processId, DescribeProcess);
        if (checkOnly)
        {
            return new DevwtCommandResult(
                $"application listening in context {contextDisplay} on original port {query.Port}: {processDetails.Count} process(es), {bindings.Count} binding(s){Environment.NewLine}",
                0);
        }

        var lines = new List<string>
        {
            $"context {contextDisplay} original port {query.Port}: {processDetails.Count} process(es), {bindings.Count} binding(s)"
        };
        foreach (var binding in bindings)
        {
            var process = processDetails[binding.ProcessId];
            lines.Add(
                $"{binding.Protocol.ToString().ToLowerInvariant()} "
                + $"{binding.OriginalIp}:{binding.OriginalPort} -> {binding.TargetIp}:{binding.TargetPort} "
                + $"PID {binding.ProcessId} {process.Name}");
            if (!string.IsNullOrWhiteSpace(process.ImagePath))
            {
                lines.Add($"  image: {process.ImagePath}");
            }
        }

        return new DevwtCommandResult(string.Join(Environment.NewLine, lines) + Environment.NewLine, 0);
    }

    private DevwtContext ResolvePortContext(DevwtPortQuery query)
    {
        var contexts = store.LoadContexts().Contexts;
        if (!string.IsNullOrWhiteSpace(query.ContextId))
        {
            return contexts.FirstOrDefault(context =>
                    context.Id.Equals(query.ContextId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown context: {query.ContextId}");
        }

        if (string.IsNullOrWhiteSpace(query.WorkingDirectory))
        {
            throw new ArgumentException("Port query requires --context <context-id> outside a registered worktree.");
        }

        var workingDirectory = DevwtPath.Normalize(query.WorkingDirectory);
        return contexts
            .Where(context => DevwtPath.IsUnderRoot(workingDirectory, context.WorktreeRootPath))
            .OrderByDescending(context => context.WorktreeRootPath.Length)
            .FirstOrDefault()
            ?? throw new ArgumentException(
                $"Current directory is not inside a registered DevWT context: {workingDirectory}. Specify --context <context-id>.");
    }

    private static PortProcessDetails DescribeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string? imagePath = null;
            try
            {
                imagePath = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
            }

            var name = string.IsNullOrWhiteSpace(imagePath)
                ? process.ProcessName
                : Path.GetFileName(imagePath);
            return new PortProcessDetails(
                string.IsNullOrWhiteSpace(name) ? "<unknown>" : name,
                imagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return new PortProcessDetails("<exited or inaccessible>", null);
        }
    }

    private sealed record PortProcessDetails(string Name, string? ImagePath);

    private DevwtCommandResult WorktreeReady(DevwtControlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryId) || string.IsNullOrWhiteSpace(request.WorktreePath))
        {
            throw new ArgumentException("WorktreeReady requires repository id and worktree path.");
        }

        var context = manager.WorktreeReady(request.RepositoryId, request.WorktreePath);
        return new DevwtCommandResult($"registered worktree {context.Name}{Environment.NewLine}", 0);
    }

    private DevwtCommandResult LinkMap(DevwtLinkMap map)
    {
        var routing = store.LoadRouting();
        var next = routing.ExplicitLinkMaps
            .Where(existing => !existing.LinkedRepositoryName.Equals(map.LinkedRepositoryName, StringComparison.OrdinalIgnoreCase)
                || !existing.SourceWorktreePath.Equals(map.SourceWorktreePath, StringComparison.OrdinalIgnoreCase))
            .Append(map)
            .ToArray();
        store.SaveRouting(routing with { ExplicitLinkMaps = next });
        return new DevwtCommandResult("created link map\n", 0);
    }

    private DevwtCommandResult SetActiveTarget(DevwtControlRequest request)
    {
        var routing = store.LoadRouting();
        var browserScoped = !string.IsNullOrWhiteSpace(request.ActiveTargetBrowserKey);
        if (browserScoped)
        {
            var browserKey = DevwtBrowserKey.Normalize(request.ActiveTargetBrowserKey!);
            if (request.ClearActiveTarget)
            {
                var remainingBrowserTargets = routing.BrowserActiveTargets
                    .Where(target => !DevwtBrowserKey.Equals(target.BrowserKey, browserKey))
                    .ToArray();
                store.SaveRouting(routing with { BrowserActiveTargets = remainingBrowserTargets });
                return new DevwtCommandResult($"cleared browser proxy active target {browserKey}\n", 0);
            }

            var target = request.ActiveTarget
                ?? throw new ArgumentException("Browser-scoped target requires an active target payload.");
            ValidateActiveTarget(target);
            var browserTarget = new DevwtBrowserActiveTarget(browserKey, target.ContextId, target.Port, target.Scheme);
            var browserTargets = routing.BrowserActiveTargets
                .Where(existing => !(DevwtBrowserKey.Equals(existing.BrowserKey, browserKey) && existing.Port == target.Port))
                .Append(browserTarget)
                .OrderBy(existing => existing.BrowserKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(existing => existing.Port)
                .ToArray();
            store.SaveRouting(routing with { BrowserActiveTargets = browserTargets });
            return new DevwtCommandResult($"set browser proxy active target {browserKey} {target.ContextId}:{target.Port}/{target.Scheme}{Environment.NewLine}", 0);
        }

        if (request.ClearActiveTarget)
        {
            if (request.Port is int port)
            {
                ValidatePort(port);
                var remainingPortTargets = routing.PortActiveTargets
                    .Where(target => target.Port != port)
                    .ToArray();
                store.SaveRouting(routing with { PortActiveTargets = remainingPortTargets });
                return new DevwtCommandResult($"cleared proxy active target for port {port}{Environment.NewLine}", 0);
            }

            var cleared = routing.ActiveTargetMode == DevwtActiveTargetMode.GlobalContext
                ? routing with { GlobalActiveContextId = null }
                : routing with { PortActiveTargets = [] };
            store.SaveRouting(cleared);
            return new DevwtCommandResult("cleared proxy active target\n", 0);
        }

        if (!string.IsNullOrWhiteSpace(request.GlobalActiveContextId))
        {
            ValidateContext(request.GlobalActiveContextId);
            store.SaveRouting(routing with
            {
                ActiveTargetMode = DevwtActiveTargetMode.GlobalContext,
                GlobalActiveContextId = request.GlobalActiveContextId
            });
            return new DevwtCommandResult($"set global proxy context {request.GlobalActiveContextId}{Environment.NewLine}", 0);
        }

        if (request.ActiveTarget is null && request.ActiveTargetMode is { } mode)
        {
            store.SaveRouting(routing with { ActiveTargetMode = mode });
            return new DevwtCommandResult($"set proxy target mode {mode}{Environment.NewLine}", 0);
        }

        var portTarget = request.ActiveTarget
            ?? throw new ArgumentException("SetActiveTarget requires a target, mode, global context, or clear flag.");
        ValidateActiveTarget(portTarget);
        var portTargets = routing.PortActiveTargets
            .Where(existing => existing.Port != portTarget.Port)
            .Append(new DevwtPortActiveTarget(portTarget.ContextId, portTarget.Port))
            .OrderBy(existing => existing.Port)
            .ToArray();
        store.SaveRouting(routing with
        {
            ActiveTargetMode = DevwtActiveTargetMode.PerPort,
            PortActiveTargets = portTargets
        });
        return new DevwtCommandResult($"set proxy active target {portTarget.ContextId}:{portTarget.Port}{Environment.NewLine}", 0);

        void ValidateActiveTarget(DevwtActiveTarget target)
        {
            ValidatePort(target.Port);
            ValidateContext(target.ContextId);
            if (target.Scheme is not ("auto" or "http" or "https"))
            {
                throw new ArgumentException("Active target scheme must be auto, http or https.");
            }
        }

        void ValidateContext(string contextId)
        {
            if (!store.LoadContexts().Contexts.Any(context => context.Id.Equals(contextId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Unknown context: {contextId}");
            }
        }

        static void ValidatePort(int port)
        {
            if (port is <= 0 or > 65535)
            {
                throw new ArgumentException("Active target port must be between 1 and 65535.");
            }
        }
    }

    private DevwtCommandResult SetProcessTarget(DevwtControlRequest request)
    {
        var routing = store.LoadRouting();
        if (request.ClearProcessPortTarget)
        {
            var processId = RequireProcessId(request.ProcessId, "ClearProcessPortTarget");
            var port = RequirePort(request.Port, "ClearProcessPortTarget");
            var next = routing.ProcessPortTargets
                .Where(target => target.ProcessId != processId || target.Port != port)
                .ToArray();
            store.SaveRouting(routing with { ProcessPortTargets = next });
            return new DevwtCommandResult($"cleared process proxy target {processId}:{port}{Environment.NewLine}", 0);
        }

        if (request.ClearProcessTarget)
        {
            if (request.ProcessId is not int processId || processId <= 0)
            {
                throw new ArgumentException("ClearProcessTarget requires a positive process id.");
            }

            var next = routing.ProcessTargets
                .Where(target => target.ProcessId != processId)
                .OrderBy(target => target.ProcessId)
                .ToArray();
            store.SaveRouting(routing with { ProcessTargets = next });
            return new DevwtCommandResult($"cleared process proxy target {processId}{Environment.NewLine}", 0);
        }

        if (request.ProcessPortTarget is { } processPortTarget)
        {
            RequireProcessId(processPortTarget.ProcessId, "Process port target");
            RequirePort(processPortTarget.Port, "Process port target");
            ValidateScheme(processPortTarget.Scheme, "Process port target");
            ValidateContext(processPortTarget.ContextId);
            var next = routing.ProcessPortTargets
                .Where(target => target.ProcessId != processPortTarget.ProcessId || target.Port != processPortTarget.Port)
                .Append(processPortTarget)
                .OrderBy(target => target.ProcessId)
                .ThenBy(target => target.Port)
                .ToArray();
            store.SaveRouting(routing with { ProcessPortTargets = next });
            return new DevwtCommandResult($"set process proxy target {processPortTarget.ProcessId} {processPortTarget.ContextId}:{processPortTarget.Port}/{processPortTarget.Scheme}{Environment.NewLine}", 0);
        }

        var processTarget = request.ProcessTarget
            ?? throw new ArgumentException("SetProcessTarget requires process target payload or clear flag.");
        if (processTarget.ProcessId <= 0)
        {
            throw new ArgumentException("Process target requires a positive process id.");
        }

        var contexts = store.LoadContexts();
        if (!contexts.Contexts.Any(context => context.Id.Equals(processTarget.ContextId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown context: {processTarget.ContextId}");
        }

        var processTargets = routing.ProcessTargets
            .Where(existing => existing.ProcessId != processTarget.ProcessId)
            .Append(processTarget)
            .OrderBy(existing => existing.ProcessId)
            .ToArray();
        store.SaveRouting(routing with { ProcessTargets = processTargets });
        return new DevwtCommandResult($"set process proxy target {processTarget.ProcessId} {processTarget.ContextId}{Environment.NewLine}", 0);
    }

    private DevwtCommandResult SetApplicationTarget(DevwtControlRequest request)
    {
        var routing = store.LoadRouting();
        if (request.ClearApplicationContextTarget)
        {
            var clearApplicationKey = NormalizeApplicationTargetKey(request.ApplicationTargetKey);
            var next = routing.ApplicationContextTargets
                .Where(target => !DevwtBrowserKey.Equals(target.ApplicationKey, clearApplicationKey))
                .ToArray();
            store.SaveRouting(routing with { ApplicationContextTargets = next });
            return new DevwtCommandResult($"cleared application context target {clearApplicationKey}{Environment.NewLine}", 0);
        }

        if (request.ClearApplicationTarget)
        {
            var clearApplicationKey = NormalizeApplicationTargetKey(request.ApplicationTargetKey);
            if (request.Port is not int port || port is <= 0 or > 65535)
            {
                throw new ArgumentException("ClearApplicationTarget requires a port between 1 and 65535.");
            }

            var next = routing.ApplicationTargets
                .Where(target => !(target.Port == port && DevwtBrowserKey.Equals(target.ApplicationKey, clearApplicationKey)))
                .OrderBy(target => target.ApplicationKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => target.Port)
                .ToArray();
            store.SaveRouting(routing with { ApplicationTargets = next });
            return new DevwtCommandResult($"cleared application proxy target {clearApplicationKey}:{port}{Environment.NewLine}", 0);
        }

        if (request.ApplicationContextTarget is { } applicationContextTarget)
        {
            var contextApplicationKey = NormalizeApplicationTargetKey(applicationContextTarget.ApplicationKey);
            ValidateContext(applicationContextTarget.ContextId);
            var normalizedTarget = applicationContextTarget with { ApplicationKey = contextApplicationKey };
            var next = routing.ApplicationContextTargets
                .Where(target => !DevwtBrowserKey.Equals(target.ApplicationKey, contextApplicationKey))
                .Append(normalizedTarget)
                .OrderBy(target => target.ApplicationKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            store.SaveRouting(routing with { ApplicationContextTargets = next });
            return new DevwtCommandResult($"set application context target {contextApplicationKey} {applicationContextTarget.ContextId}{Environment.NewLine}", 0);
        }

        var target = request.ApplicationTarget
            ?? throw new ArgumentException("SetApplicationTarget requires application target payload or clear flag.");
        if (target.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("Application target requires a port between 1 and 65535.");
        }

        if (target.Scheme is not ("auto" or "http" or "https"))
        {
            throw new ArgumentException("Application target scheme must be auto, http or https.");
        }

        var applicationKey = NormalizeApplicationTargetKey(target.ApplicationKey);
        var contexts = store.LoadContexts();
        if (!contexts.Contexts.Any(context => context.Id.Equals(target.ContextId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown context: {target.ContextId}");
        }

        var applicationTarget = target with { ApplicationKey = applicationKey };
        var applicationTargets = routing.ApplicationTargets
            .Where(existing => !(existing.Port == applicationTarget.Port && DevwtBrowserKey.Equals(existing.ApplicationKey, applicationKey)))
            .Append(applicationTarget)
            .OrderBy(existing => existing.ApplicationKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(existing => existing.Port)
            .ToArray();
        store.SaveRouting(routing with { ApplicationTargets = applicationTargets });
        return new DevwtCommandResult($"set application proxy target {applicationKey} {target.ContextId}:{target.Port}/{target.Scheme}{Environment.NewLine}", 0);
    }

    private DevwtCommandResult SetSessionTarget(DevwtControlRequest request)
    {
        var routing = store.LoadRouting();
        if (request.ClearSessionPortTarget)
        {
            var sessionId = NormalizeSessionId(request.SessionId);
            var port = RequirePort(request.Port, "ClearSessionPortTarget");
            var next = routing.SessionPortTargets
                .Where(target => target.Port != port || !target.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            store.SaveRouting(routing with { SessionPortTargets = next });
            return new DevwtCommandResult($"cleared session proxy target {sessionId}:{port}{Environment.NewLine}", 0);
        }

        if (request.ClearSessionContextTarget)
        {
            var sessionId = NormalizeSessionId(request.SessionId);
            var next = routing.SessionContextTargets
                .Where(target => !target.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            store.SaveRouting(routing with { SessionContextTargets = next });
            return new DevwtCommandResult($"cleared session context target {sessionId}{Environment.NewLine}", 0);
        }

        if (request.SessionPortTarget is { } sessionPortTarget)
        {
            var sessionId = NormalizeSessionId(sessionPortTarget.SessionId);
            RequirePort(sessionPortTarget.Port, "Session port target");
            ValidateScheme(sessionPortTarget.Scheme, "Session port target");
            ValidateContext(sessionPortTarget.ContextId);
            var normalizedTarget = sessionPortTarget with { SessionId = sessionId };
            var next = routing.SessionPortTargets
                .Where(target => target.Port != normalizedTarget.Port || !target.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                .Append(normalizedTarget)
                .OrderBy(target => target.SessionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => target.Port)
                .ToArray();
            store.SaveRouting(routing with { SessionPortTargets = next });
            return new DevwtCommandResult($"set session proxy target {sessionId} {sessionPortTarget.ContextId}:{sessionPortTarget.Port}/{sessionPortTarget.Scheme}{Environment.NewLine}", 0);
        }

        if (request.SessionContextTarget is { } sessionContextTarget)
        {
            var sessionId = NormalizeSessionId(sessionContextTarget.SessionId);
            ValidateContext(sessionContextTarget.ContextId);
            var normalizedTarget = sessionContextTarget with { SessionId = sessionId };
            var next = routing.SessionContextTargets
                .Where(target => !target.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                .Append(normalizedTarget)
                .OrderBy(target => target.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            store.SaveRouting(routing with { SessionContextTargets = next });
            return new DevwtCommandResult($"set session context target {sessionId} {sessionContextTarget.ContextId}{Environment.NewLine}", 0);
        }

        throw new ArgumentException("SetSessionTarget requires a session target payload or clear flag.");
    }

    private static string NormalizeApplicationTargetKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Application target requires an application key.");
        }

        return DevwtBrowserKey.Normalize(value);
    }

    private static string NormalizeSessionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Session target requires a session id.");
        }

        return value.Trim();
    }

    private void ValidateContext(string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId)
            || !store.LoadContexts().Contexts.Any(context => context.Id.Equals(contextId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown context: {contextId}");
        }
    }

    private static int RequireProcessId(int? processId, string operation)
    {
        if (processId is not int value || value <= 0)
        {
            throw new ArgumentException($"{operation} requires a positive process id.");
        }

        return value;
    }

    private static int RequirePort(int? port, string operation)
    {
        if (port is not int value || value is <= 0 or > 65535)
        {
            throw new ArgumentException($"{operation} requires a port between 1 and 65535.");
        }

        return value;
    }

    private static void ValidateScheme(string scheme, string operation)
    {
        if (scheme is not ("auto" or "http" or "https"))
        {
            throw new ArgumentException($"{operation} scheme must be auto, http or https.");
        }
    }

    private DevwtCommandResult StopProxyChild(DevwtProxyChildTarget target)
    {
        if (target.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("Proxy child target requires a port between 1 and 65535.");
        }

        var route = ResolveProxyChildRoute(target);
        var controller = processController ?? new DevwtProcessController();
        var result = controller.Stop(route.ListenerProcessId, target.Force);
        if (!result.Exited)
        {
            return new DevwtCommandResult((result.Message ?? $"Process {result.ProcessId} did not exit.") + Environment.NewLine, 2);
        }

        var verb = target.Force ? "killed" : "stopped";
        var detail = string.IsNullOrWhiteSpace(result.Message) ? "" : $" ({result.Message})";
        return new DevwtCommandResult(
            $"{verb} proxy child PID {route.ListenerProcessId} for {route.ContextId} {route.Protocol.ToString().ToLowerInvariant()} {route.ListenIp}:{route.Port} -> {route.TargetIp}:{route.TargetPort}{detail}{Environment.NewLine}",
            0);
    }

    private GatewayRoute ResolveProxyChildRoute(DevwtProxyChildTarget target)
    {
        var table = ResolveRouteSnapshotBuilder().BuildRouteTable().WithRouting(store.LoadRouting());
        var candidates = table.CandidatesForPort(target.Port, target.Protocol).ToArray();
        if (!string.IsNullOrWhiteSpace(target.ContextId))
        {
            return candidates.FirstOrDefault(route => route.ContextId.Equals(target.ContextId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No proxy child found for context {target.ContextId} on {target.Protocol.ToString().ToLowerInvariant()} port {target.Port}.");
        }

        if (table.ResolveGlobalActiveTarget(target.Port, target.Protocol) is { } activeRoute)
        {
            return activeRoute;
        }

        return candidates.Length switch
        {
            0 => throw new ArgumentException($"No proxy child found on {target.Protocol.ToString().ToLowerInvariant()} port {target.Port}."),
            1 => candidates[0],
            _ => throw new ArgumentException(
                $"Ambiguous proxy child target for {target.Protocol.ToString().ToLowerInvariant()} port {target.Port}; specify --context. Candidates: {string.Join(", ", candidates.Select(route => route.ContextId).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))}")
        };
    }

    private DevwtRouteSnapshotBuilder ResolveRouteSnapshotBuilder() =>
        routeSnapshotBuilder ?? new DevwtRouteSnapshotBuilder(
            store,
            new WindowsTcpListenerObservationSource(new ProcessCommandRunner()));

    private DevwtCommandResult SetIdeWatch(DevwtIdeWatch watch)
    {
        watch = NormalizeIdeWatch(watch);
        if (string.IsNullOrWhiteSpace(watch.Name) || !HasExactlyOneIdeWatchSelector(watch))
        {
            throw new ArgumentException("IDE watch requires name and exactly one selector: image path or package family.");
        }

        var settings = store.LoadRuntimeSettings();
        var next = settings.IdeWatches
            .Where(existing => !existing.Name.Equals(watch.Name, StringComparison.OrdinalIgnoreCase)
                && !SameIdeWatchSelector(existing, watch))
            .Append(watch)
            .OrderBy(existing => existing.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        store.SaveRuntimeSettings(settings with { IdeWatches = next });
        return new DevwtCommandResult($"watching IDE {watch.Name}: {DescribeIdeWatchSelector(watch)}{Environment.NewLine}", 0);
    }

    private DevwtCommandResult RemoveIdeWatch(DevwtControlRequest request)
    {
        var settings = store.LoadRuntimeSettings();
        DevwtIdeWatch[] next;
        if (request.ClearIdeWatches)
        {
            next = [];
        }
        else if (!string.IsNullOrWhiteSpace(request.IdeWatchName))
        {
            next = settings.IdeWatches
                .Where(existing => !existing.Name.Equals(request.IdeWatchName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else if (!string.IsNullOrWhiteSpace(request.IdeWatchImagePath))
        {
            next = settings.IdeWatches
                .Where(existing => !string.Equals(existing.ImagePath, request.IdeWatchImagePath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else if (!string.IsNullOrWhiteSpace(request.IdeWatchPackageFamilyName) || !string.IsNullOrWhiteSpace(request.IdeWatchAppId))
        {
            var packageFamilyName = !string.IsNullOrWhiteSpace(request.IdeWatchPackageFamilyName)
                ? request.IdeWatchPackageFamilyName
                : PackageFamilyNameFromAppId(request.IdeWatchAppId!);
            next = settings.IdeWatches
                .Where(existing => !string.Equals(existing.PackageFamilyName, packageFamilyName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            throw new ArgumentException("RemoveIdeWatch requires name, image path, package family, app id, or clear flag.");
        }

        var removed = settings.IdeWatches.Count - next.Length;
        store.SaveRuntimeSettings(settings with { IdeWatches = next });
        return new DevwtCommandResult($"removed {removed} IDE watch entr{(removed == 1 ? "y" : "ies")}{Environment.NewLine}", 0);
    }

    private DevwtCommandResult ListIdeWatch()
    {
        var settings = store.LoadRuntimeSettings();
        if (settings.IdeWatches.Count == 0)
        {
            return new DevwtCommandResult("IDE watches: none\n", 0);
        }

        var lines = new List<string> { $"IDE watches: {settings.IdeWatches.Count}" };
        lines.AddRange(settings.IdeWatches.Select(watch => $"{watch.Name}: {DescribeIdeWatchSelector(watch)}"));
        return new DevwtCommandResult(string.Join(Environment.NewLine, lines) + Environment.NewLine, 0);
    }

    private DevwtCommandResult SetSessionRule(DevwtSessionRule rule)
    {
        rule = NormalizeSessionRule(rule);
        ValidateSessionRule(rule);
        var settings = store.LoadRuntimeSettings();
        var next = settings.SessionRules
            .Where(existing => !existing.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))
            .Append(rule)
            .OrderBy(existing => existing.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        store.SaveRuntimeSettings(settings with { SessionRules = next });
        return new DevwtCommandResult($"saved session rule {rule.Name}: {DescribeSessionRule(rule)}{Environment.NewLine}", 0);
    }

    private DevwtCommandResult RemoveSessionRule(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("RemoveSessionRule requires a rule name.");
        }

        var settings = store.LoadRuntimeSettings();
        var next = settings.SessionRules
            .Where(existing => !existing.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var removed = settings.SessionRules.Count - next.Length;
        store.SaveRuntimeSettings(settings with { SessionRules = next });
        return new DevwtCommandResult($"removed {removed} session rule entr{(removed == 1 ? "y" : "ies")}{Environment.NewLine}", 0);
    }

    private DevwtCommandResult ListSessionRule()
    {
        var settings = store.LoadRuntimeSettings();
        if (settings.SessionRules.Count == 0)
        {
            return new DevwtCommandResult("session rules: none\n", 0);
        }

        var lines = new List<string> { $"session rules: {settings.SessionRules.Count}" };
        lines.AddRange(settings.SessionRules.Select(rule => $"{rule.Name}: {DescribeSessionRule(rule)}"));
        return new DevwtCommandResult(string.Join(Environment.NewLine, lines) + Environment.NewLine, 0);
    }

    private DevwtCommandResult SetBrowserFallbackOnMissingPort(bool enabled)
    {
        var settings = store.LoadRuntimeSettings();
        store.SaveRuntimeSettings(settings with { BrowserFallbackOnMissingPort = enabled });
        return new DevwtCommandResult(
            $"browser missing-port fallback: {(enabled ? "enabled" : "disabled")}{Environment.NewLine}",
            0);
    }

    private DevwtCommandResult SetBrowserMissingPortPolicy(DevwtControlRequest request)
    {
        var requested = request.BrowserMissingPortPolicy
            ?? throw new ArgumentException("Browser missing-port policy payload is required.");
        var contextId = requested.ContextId?.Trim() ?? "";
        if (contextId.Length == 0)
        {
            throw new ArgumentException("Browser missing-port policy requires a context.");
        }
        if (requested.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("Browser missing-port policy requires a port between 1 and 65535.");
        }
        if (!Enum.IsDefined(requested.Mode))
        {
            throw new ArgumentException("Browser missing-port policy mode is invalid.");
        }

        var contexts = store.LoadContexts().Contexts;
        var activeContext = contexts.FirstOrDefault(context => context.Id == contextId)
            ?? throw new ArgumentException($"Context not found: {contextId}");
        var settings = store.LoadRuntimeSettings();
        var next = settings.BrowserMissingPortPolicies
            .Where(existing => existing.ContextId != contextId || existing.Port != requested.Port)
            .ToList();
        if (!request.ClearBrowserMissingPortPolicy)
        {
            string? targetContextId = null;
            if (requested.Mode == DevwtBrowserMissingPortPolicyMode.Redirect)
            {
                targetContextId = requested.TargetContextId?.Trim();
                var targetContext = contexts.FirstOrDefault(context => context.Id == targetContextId)
                    ?? throw new ArgumentException($"Redirect context not found: {targetContextId}");
                if (targetContext.Id == activeContext.Id
                    || targetContext.RepositoryId != activeContext.RepositoryId)
                {
                    throw new ArgumentException(
                        "A browser missing-port redirect must target another worktree in the same repository.");
                }
            }

            next.Add(requested with
            {
                ContextId = contextId,
                TargetContextId = targetContextId
            });
        }

        store.SaveRuntimeSettings(settings with
        {
            BrowserMissingPortPolicies = next
                .OrderBy(policy => policy.ContextId, StringComparer.Ordinal)
                .ThenBy(policy => policy.Port)
                .ToArray()
        });
        var action = request.ClearBrowserMissingPortPolicy
            ? "cleared"
            : requested.Mode.ToString().ToLowerInvariant();
        return new DevwtCommandResult(
            $"browser missing-port policy {contextId}:{requested.Port}: {action}{Environment.NewLine}",
            0);
    }

    private static DevwtSessionRule NormalizeSessionRule(DevwtSessionRule rule) =>
        rule with
        {
            Name = rule.Name.Trim(),
            Match = rule.Match with
            {
                ProcessName = TrimToNull(rule.Match.ProcessName),
                ImagePathContains = TrimToNull(rule.Match.ImagePathContains),
                CommandLineContains = TrimToNull(rule.Match.CommandLineContains),
                EnvironmentVariable = TrimToNull(rule.Match.EnvironmentVariable)
            },
            Identity = rule.Identity with
            {
                Value = TrimToNull(rule.Identity.Value),
                Prefix = rule.Identity.Prefix ?? ""
            }
        };

    private static void ValidateSessionRule(DevwtSessionRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            throw new ArgumentException("Session rule requires a name.");
        }

        var hasMatch = !string.IsNullOrWhiteSpace(rule.Match.ProcessName)
            || !string.IsNullOrWhiteSpace(rule.Match.ImagePathContains)
            || !string.IsNullOrWhiteSpace(rule.Match.CommandLineContains)
            || !string.IsNullOrWhiteSpace(rule.Match.EnvironmentVariable);
        if (!hasMatch)
        {
            throw new ArgumentException("Session rule requires at least one match selector.");
        }

        if (rule.Identity.Kind == DevwtSessionIdentityKind.EnvironmentVariable
            && string.IsNullOrWhiteSpace(rule.Identity.Value)
            && string.IsNullOrWhiteSpace(rule.Match.EnvironmentVariable))
        {
            throw new ArgumentException("Environment-variable session identity requires an environment variable name.");
        }

        if (rule.Identity.Kind == DevwtSessionIdentityKind.CommandLineRegex
            && string.IsNullOrWhiteSpace(rule.Identity.Value))
        {
            throw new ArgumentException("Command-line-regex session identity requires a regex value.");
        }
    }

    private static string DescribeSessionRule(DevwtSessionRule rule) =>
        $"match {DescribeSessionMatch(rule.Match)} identity {DescribeSessionIdentity(rule.Identity)}";

    private static string DescribeSessionMatch(DevwtSessionMatch match)
    {
        if (!string.IsNullOrWhiteSpace(match.ProcessName))
        {
            return $"process-name {match.ProcessName}";
        }

        if (!string.IsNullOrWhiteSpace(match.ImagePathContains))
        {
            return $"image-path-contains {match.ImagePathContains}";
        }

        if (!string.IsNullOrWhiteSpace(match.CommandLineContains))
        {
            return $"command-line-contains {match.CommandLineContains}";
        }

        return $"env {match.EnvironmentVariable}";
    }

    private static string DescribeSessionIdentity(DevwtSessionIdentity identity) =>
        identity.Kind switch
        {
            DevwtSessionIdentityKind.EnvironmentVariable => $"env {identity.Value}",
            DevwtSessionIdentityKind.RootProcess => "root-process",
            DevwtSessionIdentityKind.Process => "process",
            DevwtSessionIdentityKind.CommandLineRegex => $"command-line-regex {identity.Value}",
            _ => identity.Kind.ToString()
        };

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DevwtIdeWatch NormalizeIdeWatch(DevwtIdeWatch watch)
    {
        if (!string.IsNullOrWhiteSpace(watch.AppId) && string.IsNullOrWhiteSpace(watch.PackageFamilyName))
        {
            return watch with { PackageFamilyName = PackageFamilyNameFromAppId(watch.AppId) };
        }

        return watch;
    }

    private static bool HasExactlyOneIdeWatchSelector(DevwtIdeWatch watch)
    {
        var selectors = (string.IsNullOrWhiteSpace(watch.ImagePath) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(watch.PackageFamilyName) ? 0 : 1);
        return selectors == 1;
    }

    private static bool SameIdeWatchSelector(DevwtIdeWatch left, DevwtIdeWatch right)
    {
        left = NormalizeIdeWatch(left);
        right = NormalizeIdeWatch(right);
        if (!string.IsNullOrWhiteSpace(left.ImagePath) && !string.IsNullOrWhiteSpace(right.ImagePath))
        {
            return string.Equals(left.ImagePath, right.ImagePath, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(left.PackageFamilyName) && !string.IsNullOrWhiteSpace(right.PackageFamilyName))
        {
            return string.Equals(left.PackageFamilyName, right.PackageFamilyName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string DescribeIdeWatchSelector(DevwtIdeWatch watch)
    {
        watch = NormalizeIdeWatch(watch);
        if (!string.IsNullOrWhiteSpace(watch.ImagePath))
        {
            return watch.ImagePath;
        }

        if (!string.IsNullOrWhiteSpace(watch.AppId))
        {
            return $"app-id {watch.AppId} package-family {watch.PackageFamilyName}";
        }

        return $"package-family {watch.PackageFamilyName}";
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

    private DevwtCommandResult Status()
    {
        var repos = store.LoadRepositories();
        var contexts = store.LoadContexts();
        var lines = new List<string>
        {
            $"repos: {repos.Repositories.Count}",
            $"contexts: {contexts.Contexts.Count}",
            $"ide watches: {store.LoadRuntimeSettings().IdeWatches.Count}",
            $"session rules: {store.LoadRuntimeSettings().SessionRules.Count}"
        };
        lines.AddRange(repos.Repositories.Select(repo =>
            $"repo {repo.Name}: {repo.RootPath} linked={repo.LinkedRepositories.Count}"));
        lines.AddRange(contexts.Contexts.Select(context =>
            $"context {context.Name}: {context.WorktreeRootPath} {context.AssignedIp} {context.Status}"));
        return new DevwtCommandResult(string.Join(Environment.NewLine, lines) + Environment.NewLine, 0);
    }
}

public sealed class DevwtNamedPipeControlClient(
    string pipeName = DevwtNamedPipeControlServer.DefaultPipeName,
    TimeSpan? connectTimeout = null) : IDevwtControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<DevwtControlOperation>() }
    };

    public DevwtCommandResult Send(DevwtControlRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        try
        {
            pipe.Connect(connectTimeout ?? TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            return new DevwtCommandResult("DevWT service is not running. Install/start DevWTService, then retry.\n", 2);
        }
        catch (UnauthorizedAccessException)
        {
            return new DevwtCommandResult("DevWT service pipe rejected this user. Restart/reinstall DevWTService with the current version, then retry.\n", 2);
        }

        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
        var line = reader.ReadLine();
        return string.IsNullOrWhiteSpace(line)
            ? new DevwtCommandResult("DevWT service returned an empty response.\n", 2)
            : JsonSerializer.Deserialize<DevwtCommandResult>(line, JsonOptions)
                ?? new DevwtCommandResult("DevWT service returned an invalid response.\n", 2);
    }
}

public sealed class DevwtNamedPipeControlServer(
    DevwtControlHandler handler,
    string pipeName = DevwtNamedPipeControlServer.DefaultPipeName)
{
    public const string DefaultPipeName = "DevWT.Control";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<DevwtControlOperation>() }
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity: CreatePipeSecurity());
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandlePipeAsync(pipe, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        AddAccess(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        AddAccess(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);
        AddAccess(security, WellKnownSidType.AuthenticatedUserSid, PipeAccessRights.ReadWrite);
        return security;
    }

    private static void AddAccess(PipeSecurity security, WellKnownSidType sidType, PipeAccessRights rights)
    {
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(sidType, null),
            rights,
            AccessControlType.Allow));
    }

    private async Task HandlePipeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, leaveOpen: true))
        await using (var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            DevwtCommandResult result;
            if (string.IsNullOrWhiteSpace(line))
            {
                result = new DevwtCommandResult("empty control request\n", 2);
            }
            else
            {
                var request = JsonSerializer.Deserialize<DevwtControlRequest>(line, JsonOptions);
                result = request is null
                    ? new DevwtCommandResult("invalid control request\n", 2)
                    : handler.Handle(request);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions).AsMemory(), cancellationToken);
        }
    }
}
