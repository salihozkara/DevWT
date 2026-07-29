using Devwt.Core;
using System.Net;
using System.Text.Json.Serialization;

namespace Devwt.Service;

[JsonConverter(typeof(JsonStringEnumConverter<GatewayRouteProtocol>))]
public enum GatewayRouteProtocol
{
    Tcp,
    Udp
}

public sealed record GatewayRoute(
    string ContextId,
    string RepositoryId,
    string WorktreeRootPath,
    int Port,
    string TargetIp,
    int TargetPort,
    int ListenerProcessId,
    GatewayRouteProtocol Protocol = GatewayRouteProtocol.Tcp,
    string ListenIp = DevwtPortShift.LoopbackAddress);

public sealed record GatewayListenEndpoint(
    string Ip,
    int Port,
    GatewayRouteProtocol Protocol = GatewayRouteProtocol.Tcp);

public sealed class GatewayRouteTable
{
    private readonly IReadOnlyList<GatewayRoute> _routes;
    private readonly DevwtRepositoryState _repositories;
    private readonly DevwtContextState _contexts;
    private readonly DevwtRoutingState _routing;

    private GatewayRouteTable(
        IReadOnlyList<GatewayRoute> routes,
        DevwtRepositoryState repositories,
        DevwtContextState contexts,
        DevwtRoutingState routing)
    {
        _routes = routes;
        _repositories = repositories;
        _contexts = contexts;
        _routing = DevwtRoutingState.Normalize(routing);
    }

    public static GatewayRouteTable FromRoutes(
        IReadOnlyList<GatewayRoute> routes,
        DevwtRepositoryState repositories,
        DevwtContextState contexts,
        DevwtRoutingState routing) =>
        new(routes, repositories, contexts, routing);

    public GatewayRouteTable WithRouting(DevwtRoutingState routing) =>
        new(_routes, _repositories, _contexts, routing);

    public DevwtGatewayRouteSnapshot ToSnapshot() =>
        new(_routes, _repositories, _contexts, _routing);

    public string? DescriptionForContext(string contextId) =>
        _contexts.Contexts
            .FirstOrDefault(context => context.Id.Equals(contextId, StringComparison.OrdinalIgnoreCase))
            ?.Description;

    public DevwtHttpsProxyMode TcpHandlingModeFor(string ip, int port)
    {
        var normalizedIp = IPAddress.TryParse(ip, out var address) ? address.ToString() : ip;
        return _routing.HttpsProxyEndpoints
            .LastOrDefault(endpoint => endpoint.Port == port
                && endpoint.Ip.Equals(normalizedIp, StringComparison.OrdinalIgnoreCase))
            ?.Mode
            ?? DevwtHttpsProxyMode.Auto;
    }

    public GatewayRoute? Resolve(
        int port,
        string? callerContextId,
        string? requestContextId,
        string? cookieContextId,
        bool includeActiveTarget,
        string? browserKey = null,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        var candidates = CandidatesForPort(port, protocol, listenIp).ToArray();
        if (!string.IsNullOrWhiteSpace(requestContextId))
        {
            return ResolveRequestContext(port, requestContextId, protocol, listenIp);
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(callerContextId)
            && ResolveLinkedRoute(candidates, callerContextId) is { } linked)
        {
            return linked;
        }

        if (!string.IsNullOrWhiteSpace(callerContextId)
            && candidates.FirstOrDefault(route => route.ContextId.Equals(callerContextId, StringComparison.OrdinalIgnoreCase)) is { } caller)
        {
            return caller;
        }

        if (!string.IsNullOrWhiteSpace(cookieContextId)
            && candidates.FirstOrDefault(route => route.ContextId.Equals(cookieContextId, StringComparison.OrdinalIgnoreCase)) is { } cookie)
        {
            return cookie;
        }

        if (ResolveActiveTarget(candidates, port, browserKey, includeActiveTarget) is { } activeRoute)
        {
            return activeRoute;
        }

        return candidates.Length == 1 ? candidates[0] : candidates[^1];
    }

    public GatewayRoute? ResolveWithoutCaller(
        int port,
        string? requestContextId,
        string? cookieContextId,
        bool includeActiveTarget,
        string? browserKey = null,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        var candidates = CandidatesForPort(port, protocol, listenIp).ToArray();
        if (!string.IsNullOrWhiteSpace(requestContextId))
        {
            return ResolveRequestContext(port, requestContextId, protocol, listenIp);
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(cookieContextId)
            && candidates.FirstOrDefault(route => route.ContextId.Equals(cookieContextId, StringComparison.OrdinalIgnoreCase)) is { } cookie)
        {
            return cookie;
        }

        if (ResolveActiveTarget(candidates, port, browserKey, includeActiveTarget) is { } activeRoute)
        {
            return activeRoute;
        }

        return candidates.Length == 1 ? candidates[0] : candidates[^1];
    }

    public IReadOnlyList<GatewayRoute> CandidatesForPort(int port) =>
        _routes.Where(route => route.Port == port).ToArray();

    public IReadOnlyList<GatewayRoute> CandidatesForPort(
        int port,
        GatewayRouteProtocol protocol,
        string? listenIp = null) =>
        _routes
            .Where(route => route.Port == port
                && route.Protocol == protocol
                && ListenIpMatches(route.ListenIp, listenIp))
            .ToArray();

    public GatewayRoute? ResolveSingleTarget(
        int port,
        GatewayRouteProtocol protocol,
        string? listenIp = null)
    {
        var candidates = CandidatesForPort(port, protocol, listenIp);
        if (candidates.Count == 0)
        {
            return null;
        }

        var first = candidates[0];
        return candidates.All(route =>
            route.ContextId.Equals(first.ContextId, StringComparison.OrdinalIgnoreCase)
            && route.TargetIp.Equals(first.TargetIp, StringComparison.OrdinalIgnoreCase)
            && route.TargetPort == first.TargetPort)
            ? first
            : null;
    }

    public IReadOnlyList<GatewayRoute> Routes => _routes;

    public IReadOnlyList<int> Ports =>
        _routes.Select(route => route.Port).Distinct().Order().ToArray();

    public IReadOnlyList<int> TcpPorts =>
        _routes.Where(route => route.Protocol == GatewayRouteProtocol.Tcp).Select(route => route.Port).Distinct().Order().ToArray();

    public IReadOnlyList<int> UdpPorts =>
        _routes.Where(route => route.Protocol == GatewayRouteProtocol.Udp).Select(route => route.Port).Distinct().Order().ToArray();

    public IReadOnlyList<GatewayListenEndpoint> TcpEndpoints =>
        _routes
            .Where(route => route.Protocol == GatewayRouteProtocol.Tcp)
            .Select(route => new GatewayListenEndpoint(route.ListenIp, route.Port, GatewayRouteProtocol.Tcp))
            .Distinct()
            .OrderBy(endpoint => endpoint.Ip, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Port)
            .ToArray();

    public IReadOnlyList<GatewayListenEndpoint> UdpEndpoints =>
        _routes
            .Where(route => route.Protocol == GatewayRouteProtocol.Udp)
            .Select(route => new GatewayListenEndpoint(route.ListenIp, route.Port, GatewayRouteProtocol.Udp))
            .Distinct()
            .OrderBy(endpoint => endpoint.Ip, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Port)
            .ToArray();

    public bool HasBrowserActiveTargetForPort(int port) =>
        _routing.BrowserActiveTargets.Any(target => target.Port == port);

    public GatewayRoute? ResolveRequestContext(
        int port,
        string? requestContextId,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        if (string.IsNullOrWhiteSpace(requestContextId))
        {
            return null;
        }

        var exactRoute = CandidatesForPort(port, protocol, listenIp)
            .FirstOrDefault(route => route.ContextId.Equals(requestContextId, StringComparison.OrdinalIgnoreCase));
        if (exactRoute is not null || !IsStandardLocalhostAddress(listenIp))
        {
            return exactRoute;
        }

        return CandidatesForPort(port, protocol)
            .FirstOrDefault(route =>
                IsStandardLocalhostAddress(route.ListenIp)
                && route.ContextId.Equals(requestContextId, StringComparison.OrdinalIgnoreCase));
    }

    public GatewayRoute? ResolveBrowserActiveTarget(
        int port,
        string? browserKey,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        if (string.IsNullOrWhiteSpace(browserKey))
        {
            return null;
        }

        var candidates = CandidatesForPort(port, protocol, listenIp);
        if (_routing.BrowserActiveTargets.FirstOrDefault(target =>
                target.Port == port
                && DevwtBrowserKey.Equals(target.BrowserKey, browserKey)) is { } browserTarget
            && candidates.FirstOrDefault(route => route.ContextId.Equals(browserTarget.ContextId, StringComparison.OrdinalIgnoreCase)) is { } browserRoute)
        {
            return browserRoute;
        }

        return null;
    }

    public GatewayRoute? ResolveApplicationTarget(
        int port,
        string? applicationKey,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        if (string.IsNullOrWhiteSpace(applicationKey))
        {
            return null;
        }

        var candidates = CandidatesForPort(port, protocol, listenIp);
        if (_routing.ApplicationTargets.FirstOrDefault(target =>
                target.Port == port
                && DevwtBrowserKey.Equals(target.ApplicationKey, applicationKey)) is { } applicationTarget
            && candidates.FirstOrDefault(route => route.ContextId.Equals(applicationTarget.ContextId, StringComparison.OrdinalIgnoreCase)) is { } applicationRoute)
        {
            return applicationRoute;
        }

        if (_routing.ApplicationContextTargets.FirstOrDefault(target =>
                DevwtBrowserKey.Equals(target.ApplicationKey, applicationKey)) is { } applicationContextTarget
            && candidates.FirstOrDefault(route => route.ContextId.Equals(applicationContextTarget.ContextId, StringComparison.OrdinalIgnoreCase)) is { } applicationContextRoute)
        {
            return applicationContextRoute;
        }

        return null;
    }

    public GatewayRoute? ResolveSessionTarget(
        int port,
        string? sessionId,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var candidates = CandidatesForPort(port, protocol, listenIp);
        if (_routing.SessionPortTargets.FirstOrDefault(target =>
                target.Port == port
                && target.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) is { } sessionPortTarget
            && candidates.FirstOrDefault(route => route.ContextId.Equals(sessionPortTarget.ContextId, StringComparison.OrdinalIgnoreCase)) is { } sessionPortRoute)
        {
            return sessionPortRoute;
        }

        if (_routing.SessionContextTargets.FirstOrDefault(target =>
                target.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) is { } sessionContextTarget
            && candidates.FirstOrDefault(route => route.ContextId.Equals(sessionContextTarget.ContextId, StringComparison.OrdinalIgnoreCase)) is { } sessionContextRoute)
        {
            return sessionContextRoute;
        }

        return null;
    }

    public GatewayRoute? ResolveListenerProcessTarget(
        int port,
        int? processId,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        if (processId is not int pid)
        {
            return null;
        }

        return CandidatesForPort(port, protocol, listenIp)
            .FirstOrDefault(route => route.ListenerProcessId == pid);
    }

    public GatewayRoute? ResolveCallerContext(
        int port,
        string? callerContextId,
        string? cookieContextId,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        var candidates = CandidatesForPort(port, protocol, listenIp);
        if (candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(callerContextId)
            && ResolveLinkedRoute(candidates, callerContextId) is { } linked)
        {
            return linked;
        }

        if (!string.IsNullOrWhiteSpace(callerContextId)
            && candidates.FirstOrDefault(route => route.ContextId.Equals(callerContextId, StringComparison.OrdinalIgnoreCase)) is { } caller)
        {
            return caller;
        }

        if (!string.IsNullOrWhiteSpace(cookieContextId)
            && candidates.FirstOrDefault(route => route.ContextId.Equals(cookieContextId, StringComparison.OrdinalIgnoreCase)) is { } cookie)
        {
            return cookie;
        }

        return null;
    }

    public GatewayRoute? ResolveGlobalActiveTarget(
        int port,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        var candidates = CandidatesForPort(port, protocol, listenIp);
        var contextId = ResolveConfiguredActiveContextId(port);
        if (!string.IsNullOrWhiteSpace(contextId)
            && candidates.FirstOrDefault(route => route.ContextId.Equals(contextId, StringComparison.OrdinalIgnoreCase)) is { } activeRoute)
        {
            return activeRoute;
        }

        return null;
    }

    public GatewayRoute? ResolveNewest(
        int port,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp,
        string? listenIp = null)
    {
        var candidates = CandidatesForPort(port, protocol, listenIp);
        return candidates.Count == 0 ? null : candidates[^1];
    }

    private static bool ListenIpMatches(string routeListenIp, string? requestedListenIp) =>
        string.IsNullOrWhiteSpace(requestedListenIp)
        || routeListenIp.Equals(requestedListenIp, StringComparison.OrdinalIgnoreCase);

    private static bool IsStandardLocalhostAddress(string? ip)
    {
        return IPAddress.TryParse(ip, out var address)
            && (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback));
    }

    private GatewayRoute? ResolveActiveTarget(IReadOnlyList<GatewayRoute> candidates, int port, string? browserKey, bool includeActiveTarget)
    {
        if (!includeActiveTarget)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(browserKey)
            && _routing.BrowserActiveTargets.FirstOrDefault(target =>
                target.Port == port
                && DevwtBrowserKey.Equals(target.BrowserKey, browserKey)) is { } browserTarget
            && candidates.FirstOrDefault(route => route.ContextId.Equals(browserTarget.ContextId, StringComparison.OrdinalIgnoreCase)) is { } browserRoute)
        {
            return browserRoute;
        }

        var contextId = ResolveConfiguredActiveContextId(port);
        if (!string.IsNullOrWhiteSpace(contextId)
            && candidates.FirstOrDefault(route => route.ContextId.Equals(contextId, StringComparison.OrdinalIgnoreCase)) is { } activeRoute)
        {
            return activeRoute;
        }

        return null;
    }

    private string? ResolveConfiguredActiveContextId(int port) =>
        _routing.ActiveTargetMode switch
        {
            DevwtActiveTargetMode.GlobalContext => _routing.GlobalActiveContextId,
            DevwtActiveTargetMode.PerPort => _routing.PortActiveTargets
                .FirstOrDefault(target => target.Port == port)?.ContextId,
            _ => null
        };

    private GatewayRoute? ResolveLinkedRoute(IReadOnlyList<GatewayRoute> candidates, string callerContextId)
    {
        var callerContext = _contexts.Contexts.FirstOrDefault(context =>
            context.Id.Equals(callerContextId, StringComparison.OrdinalIgnoreCase));
        if (callerContext is null)
        {
            return null;
        }

        foreach (var map in _routing.ExplicitLinkMaps)
        {
            if (!map.SourceWorktreePath.Equals(callerContext.WorktreeRootPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var explicitTarget = _contexts.Contexts.FirstOrDefault(context =>
                context.WorktreeRootPath.Equals(map.TargetWorktreePath, StringComparison.OrdinalIgnoreCase));
            if (explicitTarget is not null
                && candidates.FirstOrDefault(route => route.ContextId.Equals(explicitTarget.Id, StringComparison.OrdinalIgnoreCase)) is { } explicitRoute)
            {
                return explicitRoute;
            }
        }

        var callerRepo = _repositories.Repositories.FirstOrDefault(repo =>
            repo.Id.Equals(callerContext.RepositoryId, StringComparison.OrdinalIgnoreCase));
        if (callerRepo is null)
        {
            return null;
        }

        foreach (var linked in callerRepo.LinkedRepositories)
        {
            var expectedRoot = DevwtPath.Normalize(Path.Combine(callerContext.WorktreeRootPath, linked.Path));
            var linkedContext = _contexts.Contexts.FirstOrDefault(context =>
                context.WorktreeRootPath.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase));
            if (linkedContext is null)
            {
                continue;
            }

            var linkedRepo = _repositories.Repositories.FirstOrDefault(repo =>
                repo.Id.Equals(linkedContext.RepositoryId, StringComparison.OrdinalIgnoreCase)
                && repo.Name.Equals(linked.Name, StringComparison.OrdinalIgnoreCase));
            if (linkedRepo is null)
            {
                continue;
            }

            if (candidates.FirstOrDefault(route => route.ContextId.Equals(linkedContext.Id, StringComparison.OrdinalIgnoreCase)) is { } route)
            {
                return route;
            }
        }

        return null;
    }
}
