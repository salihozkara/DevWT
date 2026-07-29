using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Devwt.Core;

namespace Devwt.Service;

public sealed class DevwtRouteSnapshotBuilder(
    DevwtStateStore store,
    IListenerObservationSource listenerSource) : IDevwtGatewayRouteTableSource
{
    public GatewayRouteTable BuildRouteTable()
    {
        var repositories = store.LoadRepositories();
        var contexts = store.LoadContexts();
        var routing = store.LoadRouting();
        var contextsById = contexts.Contexts
            .Where(context => context.Status == DevwtContextStatus.Active)
            .ToDictionary(context => context.Id, StringComparer.OrdinalIgnoreCase);
        var bindings = LiveBindings(listenerSource.Read());
        var routes = new List<GatewayRoute>();

        foreach (var binding in bindings)
        {
            if (!contextsById.TryGetValue(binding.ContextId, out var context))
            {
                continue;
            }

            routes.Add(new GatewayRoute(
                context.Id,
                context.RepositoryId,
                context.WorktreeRootPath,
                binding.OriginalPort,
                binding.TargetIp,
                binding.TargetPort,
                binding.ProcessId,
                binding.Protocol,
                binding.OriginalIp));
        }

        return GatewayRouteTable.FromRoutes(routes, repositories, contexts, routing);
    }

    public IReadOnlyList<HookPortBinding> FindLiveBindings(string contextId, int port) =>
        LiveBindings(listenerSource.Read())
            .Where(binding => binding.ContextId.Equals(contextId, StringComparison.OrdinalIgnoreCase)
                && binding.OriginalPort == port)
            .OrderBy(binding => binding.Protocol)
            .ThenBy(binding => binding.OriginalIp, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binding => binding.ProcessId)
            .ThenBy(binding => binding.TargetPort)
            .ToArray();

    private IReadOnlyList<HookPortBinding> LiveBindings(IReadOnlyList<ListenerObservation> listeners) =>
        HookPortBindingMap.Read(HookPortBindingMap.ResolvePath(store.StateRoot))
            .Where(binding => listeners.Any(item =>
                item.ProcessId == binding.ProcessId
                && item.Port == binding.TargetPort
                && item.Protocol == binding.Protocol
                && EndpointAddressMatches(item.LocalAddress, binding.TargetIp)))
            .ToArray();

    private static bool EndpointAddressMatches(string listenerAddress, string bindingAddress) =>
        listenerAddress.Equals(bindingAddress, StringComparison.OrdinalIgnoreCase)
        || listenerAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase)
        || listenerAddress.Equals("::", StringComparison.OrdinalIgnoreCase)
        || listenerAddress.Equals("::1", StringComparison.OrdinalIgnoreCase)
            && bindingAddress.Equals(DevwtPortShift.LoopbackAddress, StringComparison.OrdinalIgnoreCase);
}

public static class ProcessContextMatcher
{
    public static Dictionary<int, string> ResolveProcessContexts(
        DevwtContextState contexts,
        IReadOnlyList<ProcessObservation> processes)
    {
        var result = new Dictionary<int, string>();
        var madeProgress = true;
        while (madeProgress)
        {
            madeProgress = false;
            foreach (var process in processes)
            {
                if (result.ContainsKey(process.ProcessId))
                {
                    continue;
                }

                var context = contexts.Contexts
                    .Where(item => item.Status == DevwtContextStatus.Active)
                    .Where(item => IsUnder(process.WorkingDirectory, item.WorktreeRootPath)
                        || IsUnder(process.ImagePath, item.WorktreeRootPath)
                        || ContainsPath(process.CommandLine, item.WorktreeRootPath))
                    .OrderByDescending(item => item.WorktreeRootPath.Length)
                    .FirstOrDefault();
                if (context is not null)
                {
                    result[process.ProcessId] = context.Id;
                    madeProgress = true;
                    continue;
                }

                if (process.ParentProcessId is { } parentId && result.TryGetValue(parentId, out var parentContext))
                {
                    result[process.ProcessId] = parentContext;
                    madeProgress = true;
                }
            }
        }

        return result;
    }

    private static bool IsUnder(string? value, string root) =>
        !string.IsNullOrWhiteSpace(value) && DevwtPath.IsUnderRoot(value, root);

    private static bool ContainsPath(string? value, string root) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Replace('/', '\\').Contains(root, StringComparison.OrdinalIgnoreCase);
}

public static class ProcessContextTargetResolver
{
    public static string? ResolveConfiguredTarget(
        int processId,
        DevwtContextState contexts,
        IReadOnlyList<ProcessObservation> processes,
        DevwtRoutingState routing) =>
        ResolveConfiguredTarget(processId, port: 0, contexts, processes, routing);

    public static string? ResolveConfiguredTarget(
        int processId,
        int port,
        DevwtContextState contexts,
        IReadOnlyList<ProcessObservation> processes,
        DevwtRoutingState routing)
    {
        if (routing.ProcessTargets.Count == 0 && routing.ProcessPortTargets.Count == 0)
        {
            return null;
        }

        var activeContextIds = contexts.Contexts
            .Where(context => context.Status == DevwtContextStatus.Active)
            .Select(context => context.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetsByProcess = routing.ProcessTargets
            .GroupBy(target => target.ProcessId)
            .ToDictionary(group => group.Key, group => group.Last());
        var portTargetsByProcess = routing.ProcessPortTargets
            .GroupBy(target => (target.ProcessId, target.Port))
            .ToDictionary(group => group.Key, group => group.Last());
        var byPid = processes
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.Last());
        var visited = new HashSet<int>();
        int? current = processId;
        while (current is int currentPid && visited.Add(currentPid))
        {
            if (portTargetsByProcess.TryGetValue((currentPid, port), out var portTarget)
                && activeContextIds.Contains(portTarget.ContextId))
            {
                return portTarget.ContextId;
            }

            if (targetsByProcess.TryGetValue(currentPid, out var target)
                && activeContextIds.Contains(target.ContextId))
            {
                return target.ContextId;
            }

            current = byPid.TryGetValue(currentPid, out var process)
                ? process.ParentProcessId
                : null;
        }

        return null;
    }
}

public static class ProcessSessionResolver
{
    private const string BuiltInSessionEnvironmentVariable = "DEVWT_SESSION_ID";

    public static string? ResolveSessionId(
        int processId,
        IReadOnlyList<ProcessObservation> processes,
        DevwtRuntimeSettings settings)
    {
        var byPid = processes
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.Last());
        var visited = new HashSet<int>();
        int? current = processId;
        while (current is int currentPid && visited.Add(currentPid))
        {
            if (!byPid.TryGetValue(currentPid, out var process))
            {
                return null;
            }

            if (TryGetEnvironmentValue(process, BuiltInSessionEnvironmentVariable) is { } builtInSession)
            {
                return builtInSession;
            }

            foreach (var rule in settings.SessionRules)
            {
                if (!RuleMatches(process, rule.Match))
                {
                    continue;
                }

                if (BuildSessionId(process, rule) is { } sessionId)
                {
                    return sessionId;
                }
            }

            current = ResolveValidParentProcessId(process, byPid);
        }

        return null;
    }

    public static string? ResolveSessionContext(
        int processId,
        IReadOnlyList<GatewayRoute> routes,
        IReadOnlyList<ProcessObservation> processes,
        DevwtRuntimeSettings settings) =>
        ResolveSessionContext(ResolveSessionId(processId, processes, settings), routes, processes, settings);

    public static string? ResolveSessionContext(
        string? callerSessionId,
        IReadOnlyList<GatewayRoute> routes,
        IReadOnlyList<ProcessObservation> processes,
        DevwtRuntimeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(callerSessionId))
        {
            return null;
        }

        foreach (var route in routes)
        {
            var listenerSessionId = ResolveSessionId(route.ListenerProcessId, processes, settings);
            if (string.Equals(listenerSessionId, callerSessionId, StringComparison.OrdinalIgnoreCase))
            {
                return route.ContextId;
            }
        }

        return null;
    }

    private static bool RuleMatches(ProcessObservation process, DevwtSessionMatch match)
    {
        if (!string.IsNullOrWhiteSpace(match.ProcessName) && !ProcessNameMatches(process, match.ProcessName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(match.ImagePathContains)
            && (string.IsNullOrWhiteSpace(process.ImagePath)
                || !process.ImagePath.Contains(match.ImagePathContains, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(match.CommandLineContains)
            && (string.IsNullOrWhiteSpace(process.CommandLine)
                || !process.CommandLine.Contains(match.CommandLineContains, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(match.EnvironmentVariable)
            && TryGetEnvironmentValue(process, match.EnvironmentVariable) is null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(match.ProcessName)
            || !string.IsNullOrWhiteSpace(match.ImagePathContains)
            || !string.IsNullOrWhiteSpace(match.CommandLineContains)
            || !string.IsNullOrWhiteSpace(match.EnvironmentVariable);
    }

    private static int? ResolveValidParentProcessId(
        ProcessObservation process,
        IReadOnlyDictionary<int, ProcessObservation> byPid)
    {
        if (process.ParentProcessId is not int parentProcessId
            || !byPid.TryGetValue(parentProcessId, out var parent))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(process.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var processStart)
            && DateTimeOffset.TryParse(parent.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parentStart)
            && parentStart > processStart)
        {
            return null;
        }

        return parentProcessId;
    }

    private static bool ProcessNameMatches(ProcessObservation process, string expected)
    {
        if (string.IsNullOrWhiteSpace(process.ImagePath))
        {
            return false;
        }

        var fileName = Path.GetFileName(process.ImagePath);
        var withoutExtension = Path.GetFileNameWithoutExtension(process.ImagePath);
        return fileName.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || withoutExtension.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildSessionId(ProcessObservation process, DevwtSessionRule rule) =>
        rule.Identity.Kind switch
        {
            DevwtSessionIdentityKind.EnvironmentVariable => BuildEnvironmentSessionId(process, rule),
            DevwtSessionIdentityKind.Process or DevwtSessionIdentityKind.RootProcess => BuildProcessSessionId(process, rule.Identity.Prefix),
            DevwtSessionIdentityKind.CommandLineRegex => BuildCommandLineRegexSessionId(process, rule.Identity),
            _ => null
        };

    private static string? BuildEnvironmentSessionId(ProcessObservation process, DevwtSessionRule rule)
    {
        var variable = !string.IsNullOrWhiteSpace(rule.Identity.Value)
            ? rule.Identity.Value
            : rule.Match.EnvironmentVariable;
        if (string.IsNullOrWhiteSpace(variable))
        {
            return null;
        }

        return TryGetEnvironmentValue(process, variable) is { } value
            ? rule.Identity.Prefix + value
            : null;
    }

    private static string BuildProcessSessionId(ProcessObservation process, string prefix)
    {
        var startTime = string.IsNullOrWhiteSpace(process.StartTime) ? "unknown" : process.StartTime.Trim();
        return $"{prefix}{process.ProcessId}:{startTime}";
    }

    private static string? BuildCommandLineRegexSessionId(ProcessObservation process, DevwtSessionIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(process.CommandLine) || string.IsNullOrWhiteSpace(identity.Value))
        {
            return null;
        }

        var match = Regex.Match(process.CommandLine, identity.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["id"] is { Success: true } named
            ? named.Value
            : match.Groups.Count > 1
                ? match.Groups[1].Value
                : match.Value;
        return string.IsNullOrWhiteSpace(value) ? null : identity.Prefix + value;
    }

    private static string? TryGetEnvironmentValue(ProcessObservation process, string variable)
    {
        if (process.EnvironmentVariables is null)
        {
            return null;
        }

        foreach (var item in process.EnvironmentVariables)
        {
            if (item.Key.Equals(variable, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Value))
            {
                return item.Value;
            }
        }

        return null;
    }
}

public sealed partial class DevwtGatewayServer(
    IDevwtGatewayRouteTableSource snapshotBuilder,
    IActiveTcpConnectionSource connectionSource,
    IProcessObservationSource processSource,
    DevwtStateStore store,
    IActiveUdpEndpointSource? udpEndpointSource = null,
    IPAddress? listenAddress = null,
    IDevwtConnectionHistorySink? connectionHistory = null,
    Func<GatewayListenEndpoint, bool>? endpointFilter = null,
    bool requireEndpointOwnership = false,
    DevwtGatewayCertificateStore? certificateStore = null)
{
    private readonly IPAddress? _listenAddressOverride = listenAddress;
    private readonly IDevwtConnectionHistorySink? _connectionHistory = connectionHistory;
    private readonly Func<GatewayListenEndpoint, bool> _endpointFilter = endpointFilter ?? (_ => true);
    private readonly bool _requireEndpointOwnership = requireEndpointOwnership;
    private readonly DevwtGatewayCertificateStore? _certificateStore = certificateStore;
    private readonly object _certificateGate = new();
    private readonly Dictionary<GatewayListenEndpoint, TcpListener> _tcpListeners = [];
    private readonly Dictionary<GatewayListenEndpoint, UdpClient> _udpListeners = [];
    private readonly ConcurrentDictionary<string, UdpProxySession> _udpSessions = [];
    private readonly ProcessRoutingCache _processRoutingCache = new();
    private readonly ProcessSnapshotCache _processSnapshotCache = new();
    private GatewayRouteTable _routes = GatewayRouteTable.FromRoutes([], DevwtRepositoryState.Empty, DevwtContextState.Empty, DevwtRoutingState.Empty);
    private X509Certificate2? _serverCertificate;
    private DevwtYarpProxyHost? _httpProxyHost;
    private bool _machineCertificateTrusted;
    private DateTimeOffset _machineTrustCheckedAt;

    private sealed record ClientProcessIdentity(
        int? ProcessId,
        string? ProcessImagePath,
        string? ApplicationKey,
        string? SessionId)
    {
        public static ClientProcessIdentity Empty { get; } = new(null, null, null, null);
    }

    private sealed record GatewayRouteDecision(
        GatewayRoute Route,
        string RouteReason,
        int? ProcessId,
        string? ProcessImagePath,
        string? ApplicationKey,
        string? SessionId);

    private sealed record UdpProxySession(UdpClient Client, IPEndPoint ClientEndPoint, IPEndPoint TargetEndPoint);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _httpProxyHost = await DevwtYarpProxyHost.StartAsync(this, GetServerCertificate(), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    RefreshListeners();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    // State files are updated atomically by the control path. If a refresh
                    // catches a transient file lock or partial read window, keep the current
                    // listener set and try again on the next tick instead of stopping the host.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var listener in _tcpListeners.Values)
            {
                listener.Stop();
            }

            foreach (var listener in _udpListeners.Values)
            {
                listener.Dispose();
            }

            foreach (var session in _udpSessions.Values)
            {
                session.Client.Dispose();
            }

            _tcpListeners.Clear();
            _udpListeners.Clear();
            _udpSessions.Clear();
            if (_httpProxyHost is not null)
            {
                await _httpProxyHost.DisposeAsync();
                _httpProxyHost = null;
            }
            _serverCertificate?.Dispose();
            _serverCertificate = null;
        }
    }

    private void RefreshListeners()
    {
        _routes = snapshotBuilder.BuildRouteTable();
        var desiredTcpEndpoints = _routes.TcpEndpoints.Where(_endpointFilter).ToHashSet();
        foreach (var stale in _tcpListeners.Keys.Where(endpoint => !desiredTcpEndpoints.Contains(endpoint)).ToArray())
        {
            _tcpListeners[stale].Stop();
            _tcpListeners.Remove(stale);
        }

        foreach (var endpoint in desiredTcpEndpoints)
        {
            if (_tcpListeners.ContainsKey(endpoint))
            {
                continue;
            }

            try
            {
                var listener = new TcpListener(BindAddressFor(endpoint), endpoint.Port);
                listener.Start();
                _tcpListeners[endpoint] = listener;
                _ = Task.Run(() => AcceptLoopAsync(endpoint, listener));
            }
            catch (SocketException)
            {
                // Another host process already owns this endpoint; status UI reports it through missing gateway listener behavior.
            }
        }

        var desiredUdpEndpoints = _routes.UdpEndpoints.Where(_endpointFilter).ToHashSet();
        foreach (var stale in _udpListeners.Keys.Where(endpoint => !desiredUdpEndpoints.Contains(endpoint)).ToArray())
        {
            _udpListeners[stale].Dispose();
            _udpListeners.Remove(stale);
        }

        foreach (var endpoint in desiredUdpEndpoints)
        {
            if (_udpListeners.ContainsKey(endpoint))
            {
                continue;
            }

            try
            {
                var listener = new UdpClient(new IPEndPoint(BindAddressFor(endpoint), endpoint.Port));
                _udpListeners[endpoint] = listener;
                _ = Task.Run(() => ReceiveUdpLoopAsync(endpoint, listener));
            }
            catch (SocketException)
            {
            }
        }

        if (_requireEndpointOwnership
            && (desiredTcpEndpoints.Any(endpoint => !_tcpListeners.ContainsKey(endpoint))
                || desiredUdpEndpoints.Any(endpoint => !_udpListeners.ContainsKey(endpoint))))
        {
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }
    }

    private IPAddress BindAddressFor(GatewayListenEndpoint endpoint) =>
        _listenAddressOverride ?? IPAddress.Parse(endpoint.Ip);

    private IPAddress OutgoingBindAddressFor(GatewayListenEndpoint endpoint) =>
        _listenAddressOverride ?? IPAddress.Parse(endpoint.Ip);

    private static IPAddress TargetConnectAddressFor(GatewayRoute route)
    {
        var address = IPAddress.Parse(route.TargetIp);
        if (address.Equals(IPAddress.Any))
        {
            return IPAddress.Loopback;
        }

        if (address.Equals(IPAddress.IPv6Any))
        {
            return IPAddress.IPv6Loopback;
        }

        return address;
    }

    private async Task AcceptLoopAsync(GatewayListenEndpoint endpoint, TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(endpoint, client));
        }
    }

    private async Task ReceiveUdpLoopAsync(GatewayListenEndpoint endpoint, UdpClient listener)
    {
        while (true)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleUdpDatagramAsync(endpoint, listener, received));
        }
    }

    private async Task HandleUdpDatagramAsync(GatewayListenEndpoint endpoint, UdpClient listener, UdpReceiveResult received)
    {
        var decision = ResolveUdpRoute(endpoint, received.RemoteEndPoint);
        if (decision is null)
        {
            return;
        }

        var route = decision.Route;
        RecordUdpConnection(endpoint, received.RemoteEndPoint, decision);
        var targetEndPoint = new IPEndPoint(TargetConnectAddressFor(route), route.TargetPort);
        var key = $"{received.RemoteEndPoint.Address}:{received.RemoteEndPoint.Port}->{targetEndPoint.Address}:{targetEndPoint.Port}";
        var session = _udpSessions.GetOrAdd(key, _key =>
        {
            var client = new UdpClient(new IPEndPoint(OutgoingBindAddressFor(endpoint), 0));
            var created = new UdpProxySession(client, received.RemoteEndPoint, targetEndPoint);
            _ = Task.Run(() => ReceiveUdpResponsesAsync(listener, created));
            return created;
        });

        try
        {
            await session.Client.SendAsync(received.Buffer, received.Buffer.Length, targetEndPoint);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task ReceiveUdpResponsesAsync(UdpClient listener, UdpProxySession session)
    {
        while (true)
        {
            UdpReceiveResult received;
            try
            {
                received = await session.Client.ReceiveAsync();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            try
            {
                await listener.SendAsync(received.Buffer, received.Buffer.Length, session.ClientEndPoint);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task HandleClientAsync(GatewayListenEndpoint endpoint, TcpClient client)
    {
        using var clientScope = client;
        var handlingMode = _routes.TcpHandlingModeFor(endpoint.Ip, endpoint.Port);
        if (handlingMode == DevwtHttpsProxyMode.Raw)
        {
            await HandleRawTcpClientAsync(endpoint, client, initialBytes: null);
            return;
        }

        byte[]? initialBytes = null;
        if (await TryReadInitialBytesAsync(client) is { } bytes)
        {
            if (bytes.Length == 0)
            {
                return;
            }

            initialBytes = bytes;
            if (DevwtGatewayProtocol.IsTlsClientHello(bytes))
            {
                if (ShouldInspectTls(handlingMode))
                {
                    await HandleHttpProxyClientAsync(
                        endpoint,
                        client,
                        bytes,
                        useTls: true,
                        useHttp2PriorKnowledge: false);
                }
                else
                {
                    await HandleRawTcpClientAsync(endpoint, client, bytes);
                }

                return;
            }

            if (DevwtGatewayProtocol.IsHttp2ConnectionPreface(bytes))
            {
                await HandleHttpProxyClientAsync(
                    endpoint,
                    client,
                    bytes,
                    useTls: false,
                    useHttp2PriorKnowledge: true);
                return;
            }

            if (DevwtGatewayHttpHeaders.IsHttp1Request(bytes))
            {
                await HandleHttpProxyClientAsync(
                    endpoint,
                    client,
                    bytes,
                    useTls: false,
                    useHttp2PriorKnowledge: false);
                return;
            }
        }

        if (handlingMode == DevwtHttpsProxyMode.Inspect)
        {
            await HandleHttpProxyClientAsync(
                endpoint,
                client,
                initialBytes ?? [],
                useTls: false,
                useHttp2PriorKnowledge: false);
            return;
        }

        await HandleRawTcpClientAsync(endpoint, client, initialBytes);
    }

    private Task HandleHttpProxyClientAsync(
        GatewayListenEndpoint endpoint,
        TcpClient client,
        byte[] initialBytes,
        bool useTls,
        bool useHttp2PriorKnowledge)
    {
        if (_httpProxyHost is null)
        {
            return Task.CompletedTask;
        }

        var identity = new Lazy<ClientProcessIdentity>(
            () => ResolveClientProcessIdentity(client),
            LazyThreadSafetyMode.ExecutionAndPublication);
        return _httpProxyHost.ProxyConnectionAsync(
            endpoint,
            client,
            initialBytes,
            useTls,
            useHttp2PriorKnowledge,
            identity,
            client.Client.RemoteEndPoint?.ToString());
    }

    private async Task HandleRawTcpClientAsync(
        GatewayListenEndpoint endpoint,
        TcpClient client,
        byte[]? initialBytes)
    {
        var decision = ResolveRoute(endpoint, client, requestContextId: null, cookieContextId: null);
        if (decision is null)
        {
            return;
        }

        var route = decision.Route;
        using var target = new TcpClient();
        try
        {
            await target.ConnectAsync(TargetConnectAddressFor(route), route.TargetPort);
            RecordConnection(endpoint, client, decision);
            await using var clientStream = client.GetStream();
            await using var targetStream = target.GetStream();
            if (initialBytes is { Length: > 0 })
            {
                await targetStream.WriteAsync(initialBytes);
            }

            var upload = clientStream.CopyToAsync(targetStream);
            var download = targetStream.CopyToAsync(clientStream);
            await Task.WhenAny(upload, download);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private bool ShouldInspectTls(DevwtHttpsProxyMode handlingMode)
    {
        return handlingMode switch
        {
            DevwtHttpsProxyMode.Inspect => true,
            DevwtHttpsProxyMode.Tunnel => false,
            DevwtHttpsProxyMode.Raw => false,
            _ => IsMachineCertificateTrusted()
        };
    }

    private bool IsMachineCertificateTrusted()
    {
        if (_certificateStore is null)
        {
            return false;
        }

        lock (_certificateGate)
        {
            if (DateTimeOffset.UtcNow - _machineTrustCheckedAt < TimeSpan.FromSeconds(5))
            {
                return _machineCertificateTrusted;
            }

            try
            {
                _machineCertificateTrusted = _certificateStore.IsRootTrusted(StoreLocation.LocalMachine);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or CryptographicException or IOException)
            {
                _machineCertificateTrusted = false;
            }
            _machineTrustCheckedAt = DateTimeOffset.UtcNow;
            return _machineCertificateTrusted;
        }
    }

    private X509Certificate2? GetServerCertificate()
    {
        if (_certificateStore is null)
        {
            return null;
        }

        lock (_certificateGate)
        {
            return _serverCertificate ??= _certificateStore.GetOrCreateServerCertificate();
        }
    }

    private static bool ValidateUpstreamCertificate(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        bool allowUntrustedLocalChain)
    {
        if (certificate is null)
        {
            return false;
        }

        using var parsed = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var now = DateTimeOffset.UtcNow;
        if (now < parsed.NotBefore.ToUniversalTime() || now > parsed.NotAfter.ToUniversalTime())
        {
            return false;
        }

        var disallowedErrors = allowUntrustedLocalChain
            ? errors & ~SslPolicyErrors.RemoteCertificateChainErrors
            : errors;
        if (disallowedErrors != SslPolicyErrors.None)
        {
            return false;
        }

        var enhancedKeyUsage = parsed.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return enhancedKeyUsage is null
            || enhancedKeyUsage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));
    }

    private GatewayRouteDecision? ResolveRoute(
        GatewayListenEndpoint endpoint,
        TcpClient client,
        string? requestContextId,
        string? cookieContextId,
        bool allowRequestContextFallback = false,
        bool allowProcessLookup = true)
    {
        if (!allowProcessLookup
            && string.IsNullOrWhiteSpace(requestContextId)
            && ResolveSingleRoute(endpoint) is { } singleRoute)
        {
            return singleRoute;
        }

        var identity = allowProcessLookup
            ? ResolveClientProcessIdentity(client)
            : ClientProcessIdentity.Empty;

        return ResolveRoute(
            endpoint,
            identity,
            requestContextId,
            cookieContextId,
            allowRequestContextFallback,
            allowProcessLookup);
    }

    private GatewayRouteDecision? ResolveRoute(
        GatewayListenEndpoint endpoint,
        ClientProcessIdentity identity,
        string? requestContextId,
        string? cookieContextId,
        bool allowRequestContextFallback = false,
        bool allowProcessLookup = true)
    {
        var routes = _routes.WithRouting(store.LoadRouting());
        var usedRequestContextFallback = false;
        var requestContextFallbackPrefix = "browser-fallback";

        GatewayRouteDecision Remember(GatewayRoute route, string reason)
        {
            RememberProcessContext(identity.ProcessId, route.ContextId);
            return new GatewayRouteDecision(
                route,
                usedRequestContextFallback ? $"{requestContextFallbackPrefix}-{reason}" : reason,
                identity.ProcessId,
                identity.ProcessImagePath,
                identity.ApplicationKey,
                identity.SessionId);
        }

        if (!string.IsNullOrWhiteSpace(requestContextId))
        {
            if (routes.ResolveRequestContext(endpoint.Port, requestContextId, listenIp: endpoint.Ip) is { } requestRoute)
            {
                return Remember(requestRoute, "request-header");
            }

            if (!allowRequestContextFallback)
            {
                return null;
            }

            var runtimeSettings = store.LoadRuntimeSettings();
            var policy = runtimeSettings.BrowserMissingPortPolicies.FirstOrDefault(candidate =>
                candidate.ContextId == requestContextId
                && candidate.Port == endpoint.Port);
            if (policy?.Mode == DevwtBrowserMissingPortPolicyMode.Disabled)
            {
                return null;
            }
            if (policy?.Mode == DevwtBrowserMissingPortPolicyMode.Redirect)
            {
                var contexts = store.LoadContexts().Contexts;
                var activeContext = contexts.FirstOrDefault(context => context.Id == requestContextId);
                var targetContext = contexts.FirstOrDefault(context => context.Id == policy.TargetContextId);
                if (activeContext is null
                    || targetContext is null
                    || targetContext.Id == activeContext.Id
                    || targetContext.RepositoryId != activeContext.RepositoryId)
                {
                    return null;
                }

                return routes.ResolveRequestContext(
                    endpoint.Port,
                    targetContext.Id,
                    listenIp: endpoint.Ip) is { } redirectedRoute
                    ? Remember(redirectedRoute, "browser-worktree-redirect")
                    : null;
            }
            if (policy?.Mode != DevwtBrowserMissingPortPolicyMode.Automatic
                && !runtimeSettings.BrowserFallbackOnMissingPort)
            {
                return null;
            }

            requestContextFallbackPrefix = policy?.Mode == DevwtBrowserMissingPortPolicyMode.Automatic
                ? "browser-worktree-fallback"
                : "browser-fallback";
            usedRequestContextFallback = true;
        }

        if (routes.ResolveSingleTarget(endpoint.Port, GatewayRouteProtocol.Tcp, endpoint.Ip) is { } singleRoute)
        {
            return Remember(singleRoute, "single-target");
        }

        if (routes.ResolveBrowserActiveTarget(endpoint.Port, identity.ApplicationKey, listenIp: endpoint.Ip) is { } browserRoute)
        {
            return Remember(browserRoute, "browser-active");
        }

        if (routes.ResolveListenerProcessTarget(endpoint.Port, identity.ProcessId, listenIp: endpoint.Ip) is { } selfRoute)
        {
            return Remember(selfRoute, "self-process");
        }

        if (allowProcessLookup && identity.ProcessId is int processId)
        {
            var contexts = store.LoadContexts();
            var runtimeSettings = store.LoadRuntimeSettings();
            var processes = ReadProcessSnapshot(processId, runtimeSettings);
            var configuredContextId = ResolveConfiguredProcessTarget(processId, endpoint.Port, contexts, processes);
            if (routes.ResolveCallerContext(endpoint.Port, configuredContextId, cookieContextId: null, listenIp: endpoint.Ip) is { } configuredProcessRoute)
            {
                return Remember(configuredProcessRoute, "process-context");
            }

            if (routes.ResolveSessionTarget(endpoint.Port, identity.SessionId, listenIp: endpoint.Ip) is { } sessionTargetRoute)
            {
                return Remember(sessionTargetRoute, "session-default");
            }

            var naturalSessionContextId = ProcessSessionResolver.ResolveSessionContext(
                identity.SessionId,
                routes.CandidatesForPort(endpoint.Port, GatewayRouteProtocol.Tcp, endpoint.Ip),
                processes,
                runtimeSettings);
            if (routes.ResolveCallerContext(endpoint.Port, naturalSessionContextId, cookieContextId: null, listenIp: endpoint.Ip) is { } sessionRoute)
            {
                return Remember(sessionRoute, "session-context");
            }

            var processContexts = ProcessContextMatcher.ResolveProcessContexts(contexts, processes);
            var inferredContextId = processContexts.GetValueOrDefault(processId);
            if (routes.ResolveCallerContext(endpoint.Port, inferredContextId, cookieContextId: null, listenIp: endpoint.Ip) is { } processRoute)
            {
                return Remember(processRoute, "process-context");
            }
        }

        if (routes.ResolveCallerContext(endpoint.Port, callerContextId: null, cookieContextId, listenIp: endpoint.Ip) is { } cookieRoute)
        {
            return Remember(cookieRoute, "context-cookie");
        }

        if (routes.ResolveApplicationTarget(endpoint.Port, identity.ApplicationKey, listenIp: endpoint.Ip) is { } applicationRoute)
        {
            return Remember(applicationRoute, "app-default");
        }

        if (routes.ResolveGlobalActiveTarget(endpoint.Port, listenIp: endpoint.Ip) is { } globalRoute)
        {
            return Remember(globalRoute, "global-active");
        }

        if (allowProcessLookup && identity.ProcessId is int pid)
        {
            var lastCallerContextId = ResolveLastContextForProcessId(pid);
            if (routes.ResolveCallerContext(endpoint.Port, lastCallerContextId, cookieContextId, listenIp: endpoint.Ip) is { } lastProcessRoute)
            {
                return Remember(lastProcessRoute, "last-process");
            }
        }

        var newestRoute = routes.ResolveNewest(endpoint.Port, listenIp: endpoint.Ip);
        if (newestRoute is not null)
        {
            return Remember(newestRoute, "newest");
        }

        return null;
    }

    private GatewayRouteDecision? ResolveUdpRoute(GatewayListenEndpoint endpoint, IPEndPoint clientEndPoint)
    {
        if (ResolveSingleRoute(endpoint, GatewayRouteProtocol.Udp) is { } singleRoute)
        {
            return singleRoute;
        }

        var routes = _routes.WithRouting(store.LoadRouting());
        var processId = udpEndpointSource?.TryFindOwningProcess(clientEndPoint);
        var identity = ResolveProcessIdentity(processId);

        GatewayRouteDecision Remember(GatewayRoute route, string reason)
        {
            RememberProcessContext(identity.ProcessId, route.ContextId);
            return new GatewayRouteDecision(
                route,
                reason,
                identity.ProcessId,
                identity.ProcessImagePath,
                identity.ApplicationKey,
                identity.SessionId);
        }

        if (routes.ResolveListenerProcessTarget(endpoint.Port, identity.ProcessId, GatewayRouteProtocol.Udp, endpoint.Ip) is { } selfRoute)
        {
            return Remember(selfRoute, "self-process");
        }

        if (identity.ProcessId is int pid)
        {
            var contexts = store.LoadContexts();
            var runtimeSettings = store.LoadRuntimeSettings();
            var processes = ReadProcessSnapshot(pid, runtimeSettings);
            var configuredContextId = ResolveConfiguredProcessTarget(pid, endpoint.Port, contexts, processes);
            if (routes.ResolveCallerContext(endpoint.Port, configuredContextId, cookieContextId: null, protocol: GatewayRouteProtocol.Udp, listenIp: endpoint.Ip) is { } configuredProcessRoute)
            {
                return Remember(configuredProcessRoute, "process-context");
            }

            if (routes.ResolveSessionTarget(endpoint.Port, identity.SessionId, GatewayRouteProtocol.Udp, endpoint.Ip) is { } sessionTargetRoute)
            {
                return Remember(sessionTargetRoute, "session-default");
            }

            var naturalSessionContextId = ProcessSessionResolver.ResolveSessionContext(
                identity.SessionId,
                routes.CandidatesForPort(endpoint.Port, GatewayRouteProtocol.Udp, endpoint.Ip),
                processes,
                runtimeSettings);
            if (routes.ResolveCallerContext(endpoint.Port, naturalSessionContextId, cookieContextId: null, protocol: GatewayRouteProtocol.Udp, listenIp: endpoint.Ip) is { } sessionRoute)
            {
                return Remember(sessionRoute, "session-context");
            }

            var processContexts = ProcessContextMatcher.ResolveProcessContexts(contexts, processes);
            var inferredContextId = processContexts.GetValueOrDefault(pid);
            if (routes.ResolveCallerContext(endpoint.Port, inferredContextId, cookieContextId: null, protocol: GatewayRouteProtocol.Udp, listenIp: endpoint.Ip) is { } processRoute)
            {
                return Remember(processRoute, "process-context");
            }
        }

        if (routes.ResolveApplicationTarget(endpoint.Port, identity.ApplicationKey, GatewayRouteProtocol.Udp, endpoint.Ip) is { } applicationRoute)
        {
            return Remember(applicationRoute, "app-default");
        }

        if (routes.ResolveGlobalActiveTarget(endpoint.Port, GatewayRouteProtocol.Udp, listenIp: endpoint.Ip) is { } globalRoute)
        {
            return Remember(globalRoute, "global-active");
        }

        if (identity.ProcessId is int lastPid)
        {
            var lastCallerContextId = ResolveLastContextForProcessId(lastPid);
            if (routes.ResolveCallerContext(endpoint.Port, lastCallerContextId, cookieContextId: null, protocol: GatewayRouteProtocol.Udp, listenIp: endpoint.Ip) is { } lastProcessRoute)
            {
                return Remember(lastProcessRoute, "last-process");
            }
        }

        var newestRoute = routes.ResolveNewest(endpoint.Port, GatewayRouteProtocol.Udp, listenIp: endpoint.Ip);
        if (newestRoute is not null)
        {
            return Remember(newestRoute, "newest");
        }

        return null;
    }

    private GatewayRouteDecision? ResolveSingleRoute(
        GatewayListenEndpoint endpoint,
        GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp)
    {
        if (_routes.ResolveSingleTarget(endpoint.Port, protocol, endpoint.Ip) is not { } route)
        {
            return null;
        }

        return new GatewayRouteDecision(
            route,
            "single-target",
            ProcessId: null,
            ProcessImagePath: null,
            ApplicationKey: null,
            SessionId: null);
    }

    private ClientProcessIdentity ResolveClientProcessIdentity(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint clientEndPoint
            || client.Client.LocalEndPoint is not IPEndPoint gatewayEndPoint)
        {
            return ClientProcessIdentity.Empty;
        }

        var processId = connectionSource.TryFindOwningProcess(clientEndPoint, gatewayEndPoint);
        return ResolveProcessIdentity(processId);
    }

    private ClientProcessIdentity ResolveProcessIdentity(int? processId)
    {
        if (processId is not int pid)
        {
            return ClientProcessIdentity.Empty;
        }

        var now = DateTimeOffset.UtcNow;
        if (_processRoutingCache.TryGetIdentity(pid, now) is { } cached)
        {
            return new ClientProcessIdentity(pid, cached.ProcessImagePath, cached.ApplicationKey, cached.SessionId);
        }

        var runtimeSettings = store.LoadRuntimeSettings();
        var processes = ReadProcessSnapshot(pid, runtimeSettings);
        _processRoutingCache.Prune(processes.Select(item => item.ProcessId).ToHashSet(), now);
        var process = processes.FirstOrDefault(item => item.ProcessId == pid);
        var processImagePath = string.IsNullOrWhiteSpace(process?.ImagePath) ? null : process.ImagePath;
        var applicationKey = string.IsNullOrWhiteSpace(processImagePath)
            ? null
            : DevwtBrowserKey.Normalize(processImagePath);
        var sessionId = ProcessSessionResolver.ResolveSessionId(pid, processes, runtimeSettings);
        if (process is not null)
        {
            _processRoutingCache.SetIdentity(pid, new CachedProcessIdentity(processImagePath, applicationKey, sessionId), now);
        }
        return new ClientProcessIdentity(pid, processImagePath, applicationKey, sessionId);
    }

    private int? ResolveClientProcessId(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint clientEndPoint
            || client.Client.LocalEndPoint is not IPEndPoint gatewayEndPoint)
        {
            return null;
        }

        return connectionSource.TryFindOwningProcess(clientEndPoint, gatewayEndPoint);
    }

    private string? ResolveContextForProcessId(int processId)
    {
        return ResolveStrongContextForProcessId(processId) ?? ResolveLastContextForProcessId(processId);
    }

    private string? ResolveStrongContextForProcessId(int processId)
    {
        var contexts = store.LoadContexts();
        var runtimeSettings = store.LoadRuntimeSettings();
        var processes = ReadProcessSnapshot(processId, runtimeSettings);
        if (ResolveConfiguredProcessTarget(processId, 0, contexts, processes) is { } configuredContext)
        {
            return configuredContext;
        }

        if (ProcessSessionResolver.ResolveSessionContext(
                processId,
                _routes.Routes,
                processes,
                runtimeSettings) is { } sessionContext)
        {
            return sessionContext;
        }

        var map = ProcessContextMatcher.ResolveProcessContexts(contexts, processes);
        if (map.TryGetValue(processId, out var directContext))
        {
            return directContext;
        }

        return null;
    }

    private string? ResolveLastContextForProcessId(int processId)
    {
        var processes = ReadProcessSnapshot(processId, store.LoadRuntimeSettings());
        var byPid = processes
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.Last());
        var visited = new HashSet<int>();
        int? current = processId;
        var now = DateTimeOffset.UtcNow;
        while (current is int currentPid && visited.Add(currentPid))
        {
            if (_processRoutingCache.TryGetLastContext(currentPid, now) is { } lastContext)
            {
                return lastContext;
            }

            current = byPid.TryGetValue(currentPid, out var process)
                ? process.ParentProcessId
                : null;
        }

        return null;
    }

    private IReadOnlyList<ProcessObservation> ReadProcessSnapshot(
        int requiredProcessId,
        DevwtRuntimeSettings runtimeSettings)
    {
        var requiredProcessIds = _routes.Routes
            .Select(route => route.ListenerProcessId)
            .ToHashSet();
        requiredProcessIds.Add(requiredProcessId);
        var sessionRulesFingerprint = JsonSerializer.Serialize(runtimeSettings.SessionRules);
        var requiredProcessStartTimes = TryGetProcessStartTime(requiredProcessId) is { } processStartTime
            ? new Dictionary<int, DateTimeOffset> { [requiredProcessId] = processStartTime }
            : null;
        return _processSnapshotCache.GetOrRefresh(
            requiredProcessIds,
            sessionRulesFingerprint,
            processSource.Read,
            requiredProcessStartTimes);
    }

    private static DateTimeOffset? TryGetProcessStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private string? ResolveConfiguredProcessTarget(
        int processId,
        int port,
        DevwtContextState contexts,
        IReadOnlyList<ProcessObservation> processes)
    {
        return ProcessContextTargetResolver.ResolveConfiguredTarget(
            processId,
            port,
            contexts,
            processes,
            store.LoadRouting());
    }

    private void RememberClientContext(TcpClient client, string contextId)
    {
        var processId = ResolveClientProcessId(client);
        RememberProcessContext(processId, contextId);
    }

    private void RememberProcessContext(int? processId, string contextId)
    {
        if (processId is int pid)
        {
            _processRoutingCache.SetLastContext(pid, contextId, DateTimeOffset.UtcNow);
        }
    }

    private void RecordConnection(GatewayListenEndpoint endpoint, TcpClient client, GatewayRouteDecision decision)
    {
        RecordConnection(endpoint, client.Client.RemoteEndPoint?.ToString(), decision);
    }

    private void RecordConnection(GatewayListenEndpoint endpoint, string? clientEndpoint, GatewayRouteDecision decision)
    {
        if (_connectionHistory is null)
        {
            return;
        }

        var route = decision.Route;
        var contextName = store.LoadContexts().Contexts
            .FirstOrDefault(context => context.Id.Equals(route.ContextId, StringComparison.OrdinalIgnoreCase))
            ?.Name;
        _connectionHistory.Add(new DevwtConnectionHistoryEntry(
            DateTimeOffset.UtcNow,
            endpoint.Protocol,
            endpoint.Ip,
            endpoint.Port,
            route.TargetIp,
            route.TargetPort,
            route.ContextId,
            contextName,
            decision.RouteReason,
            decision.ProcessId,
            decision.ProcessImagePath,
            decision.ApplicationKey,
            clientEndpoint,
            decision.SessionId));
    }

    private void RecordUdpConnection(GatewayListenEndpoint endpoint, IPEndPoint clientEndPoint, GatewayRouteDecision decision)
    {
        if (_connectionHistory is null)
        {
            return;
        }

        var route = decision.Route;
        var contextName = store.LoadContexts().Contexts
            .FirstOrDefault(context => context.Id.Equals(route.ContextId, StringComparison.OrdinalIgnoreCase))
            ?.Name;
        _connectionHistory.Add(new DevwtConnectionHistoryEntry(
            DateTimeOffset.UtcNow,
            endpoint.Protocol,
            endpoint.Ip,
            endpoint.Port,
            route.TargetIp,
            route.TargetPort,
            route.ContextId,
            contextName,
            decision.RouteReason,
            decision.ProcessId,
            decision.ProcessImagePath,
            decision.ApplicationKey,
            clientEndPoint.ToString(),
            decision.SessionId));
    }

    private string? ResolveCallerContextFromAssignedAddress(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint clientEndPoint
            || client.Client.LocalEndPoint is not IPEndPoint)
        {
            return null;
        }

        var contexts = store.LoadContexts();
        var addressContext = contexts.Contexts.FirstOrDefault(context =>
            context.AssignedIp.Equals(clientEndPoint.Address.ToString(), StringComparison.OrdinalIgnoreCase));
        if (addressContext is not null)
        {
            return addressContext.Id;
        }

        return null;
    }

    private string? ResolveCallerContextFromProcess(TcpClient client)
    {
        var processId = ResolveClientProcessId(client);
        if (processId is null)
        {
            return null;
        }

        return ResolveContextForProcessId(processId.Value);
    }

    private static async Task<byte[]?> TryReadInitialBytesAsync(TcpClient client)
    {
        var stream = client.GetStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        try
        {
            while (memory.Length < 65536)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    break;
                }

                memory.Write(buffer, 0, read);
                var bytes = memory.GetBuffer().AsSpan(0, (int)memory.Length);
                if (DevwtGatewayProtocol.IsTlsClientHello(bytes))
                {
                    break;
                }

                if (DevwtGatewayProtocol.CouldBeHttp2ConnectionPreface(bytes))
                {
                    if (DevwtGatewayProtocol.IsHttp2ConnectionPreface(bytes))
                    {
                        break;
                    }

                    continue;
                }

                if (!DevwtGatewayHttpHeaders.LooksLikeHttpRequest(bytes)
                    || DevwtGatewayHttpHeaders.HasCompleteHeaderBlock(bytes))
                {
                    break;
                }
            }

            return memory.ToArray();
        }
        catch (OperationCanceledException)
        {
            return memory.Length == 0 ? null : memory.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
    }

}

public static class DevwtGatewayHttpHeaders
{
    private static ReadOnlySpan<byte> Get => "GET "u8;
    private static ReadOnlySpan<byte> Post => "POST "u8;
    private static ReadOnlySpan<byte> Put => "PUT "u8;
    private static ReadOnlySpan<byte> Patch => "PATCH "u8;
    private static ReadOnlySpan<byte> Delete => "DELETE "u8;
    private static ReadOnlySpan<byte> Head => "HEAD "u8;
    private static ReadOnlySpan<byte> Options => "OPTIONS "u8;
    private static ReadOnlySpan<byte> Connect => "CONNECT "u8;

    public static async Task<byte[]> ReadHttpRequestStartAsync(
        Stream stream,
        int maxBytes = 65536,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        while (memory.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
            if (FindHeaderEnd(memory.GetBuffer().AsSpan(0, (int)memory.Length)) is not null)
            {
                break;
            }
        }

        return memory.ToArray();
    }

    public static bool HasCompleteHeaderBlock(ReadOnlySpan<byte> bytes) => FindHeaderEnd(bytes) is not null;

    public static bool LooksLikeHttpRequest(ReadOnlySpan<byte> bytes)
    {
        return PrefixMatches(bytes, Get)
            || PrefixMatches(bytes, Post)
            || PrefixMatches(bytes, Put)
            || PrefixMatches(bytes, Patch)
            || PrefixMatches(bytes, Delete)
            || PrefixMatches(bytes, Head)
            || PrefixMatches(bytes, Options)
            || PrefixMatches(bytes, Connect);
    }

    public static bool IsHttp1Request(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(Get)
        || bytes.StartsWith(Post)
        || bytes.StartsWith(Put)
        || bytes.StartsWith(Patch)
        || bytes.StartsWith(Delete)
        || bytes.StartsWith(Head)
        || bytes.StartsWith(Options)
        || bytes.StartsWith(Connect);

    private static bool PrefixMatches(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix) =>
        bytes.Length <= prefix.Length ? prefix.StartsWith(bytes) : bytes.StartsWith(prefix);

    private static HeaderEnd? FindHeaderEnd(byte[] bytes) => FindHeaderEnd(bytes.AsSpan());

    private static HeaderEnd? FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index <= bytes.Length - 4; index++)
        {
            if (bytes[index] == '\r'
                && bytes[index + 1] == '\n'
                && bytes[index + 2] == '\r'
                && bytes[index + 3] == '\n')
            {
                return new HeaderEnd(index + 4, "\r\n");
            }
        }

        for (var index = 0; index <= bytes.Length - 2; index++)
        {
            if (bytes[index] == '\n' && bytes[index + 1] == '\n')
            {
                return new HeaderEnd(index + 2, "\n");
            }
        }

        return null;
    }

    private readonly record struct HeaderEnd(int End, string Newline);

}

public static class DevwtGatewayProtocol
{
    private static ReadOnlySpan<byte> Http2ConnectionPreface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

    public static bool IsTlsClientHello(byte[] bytes) => IsTlsClientHello(bytes.AsSpan());

    public static bool IsTlsClientHello(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3
        && bytes[0] == 0x16
        && bytes[1] == 0x03
        && bytes[2] is >= 0x01 and <= 0x04;

    public static bool IsHttp2ConnectionPreface(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(Http2ConnectionPreface);

    public static bool CouldBeHttp2ConnectionPreface(ReadOnlySpan<byte> bytes) =>
        bytes.Length <= Http2ConnectionPreface.Length
            ? Http2ConnectionPreface.StartsWith(bytes)
            : bytes.StartsWith(Http2ConnectionPreface);
}

public sealed class DevwtWebUiStatusProvider(
    DevwtStateStore store,
    IDevwtGatewayRouteTableSource? routeSnapshotBuilder = null,
    DevwtConnectionHistory? connectionHistory = null,
    Func<int, string?>? processNameResolver = null)
{
    public DevwtWebUiStatus Build() =>
        new(
            store.LoadRepositories().Repositories,
            store.LoadContexts().Contexts,
            store.LoadRouting(),
            ReadRoutes(),
            store.LoadRuntimeSettings(),
            connectionHistory?.Snapshot() ?? []);

    private IReadOnlyList<DevwtWebUiRoute> ReadRoutes()
    {
        if (routeSnapshotBuilder is null)
        {
            return [];
        }

        try
        {
            var routes = routeSnapshotBuilder.BuildRouteTable().Routes;
            var resolveProcessName = processNameResolver ?? ResolveProcessName;
            var processNames = routes
                .Select(route => route.ListenerProcessId)
                .Distinct()
                .ToDictionary(processId => processId, resolveProcessName);
            return routes
                .Select(route => DevwtWebUiRoute.FromGatewayRoute(
                    route,
                    processNames[route.ListenerProcessId]))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? ResolveProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName?.Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }
}

public sealed record DevwtWebUiRoute(
    string ContextId,
    string RepositoryId,
    string WorktreeRootPath,
    int Port,
    string TargetIp,
    int TargetPort,
    int ListenerProcessId,
    GatewayRouteProtocol Protocol,
    string ListenIp,
    string? ProcessName)
{
    public static DevwtWebUiRoute FromGatewayRoute(GatewayRoute route, string? processName) =>
        new(
            route.ContextId,
            route.RepositoryId,
            route.WorktreeRootPath,
            route.Port,
            route.TargetIp,
            route.TargetPort,
            route.ListenerProcessId,
            route.Protocol,
            route.ListenIp,
            processName);
}

public sealed record DevwtWebUiStatus(
    IReadOnlyList<DevwtRepository> Repositories,
    IReadOnlyList<DevwtContext> Contexts,
    DevwtRoutingState Routing,
    IReadOnlyList<DevwtWebUiRoute> Routes,
    DevwtRuntimeSettings RuntimeSettings,
    IReadOnlyList<DevwtConnectionHistoryEntry> ConnectionHistory);

public sealed record DevwtWebUiAction(
    string Action,
    string? RepositoryName = null,
    string? RepositoryId = null,
    string? WorktreePath = null,
    string? ContextId = null,
    int? Port = null,
    string? Scheme = null,
    bool BrowserScoped = false,
    string? SessionRuleName = null,
    string? SessionMatchKind = null,
    string? SessionMatchValue = null,
    string? SessionIdentityKind = null,
    string? SessionIdentityValue = null,
    string? SessionPrefix = null,
    string? ApplicationKey = null,
    string? ActiveTargetMode = null,
    int? ProcessId = null,
    string? SessionId = null,
    string? ListenIp = null,
    string? HttpsProxyMode = null,
    string? ContextDescription = null,
    bool ClearContextDescription = false,
    string? LinkedRepositoryName = null,
    string? SourceWorktreePath = null,
    string? TargetWorktreePath = null,
    string? IdeWatchName = null,
    string? IdeWatchSelectorKind = null,
    string? IdeWatchSelectorValue = null,
    bool? BrowserFallbackOnMissingPort = null,
    DevwtBrowserMissingPortPolicyMode? BrowserMissingPortPolicyMode = null,
    string? TargetContextId = null,
    string? Protocol = null,
    IReadOnlyList<LinkedRepositoryInput>? LinkedRepositories = null);

public static class DevwtWebUiActionMapper
{
    private static readonly HashSet<string> ManagementActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "add-repository",
        "pause-repository",
        "resume-repository",
        "describe-context",
        "find-port-processes",
        "check-port",
        "link-map",
        "add-ide-watch",
        "remove-ide-watch",
        "set-browser-fallback-on-missing-port",
        "set-browser-missing-port-policy",
        "clear-browser-missing-port-policy",
        "stop-proxy-child",
        "kill-proxy-child"
    };

    private static readonly HashSet<string> ScopedTargetActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "set-process-target",
        "clear-process-target",
        "set-process-port-target",
        "clear-process-port-target",
        "set-image-context-target",
        "clear-image-context-target",
        "set-application-target",
        "clear-application-target",
        "set-session-context-target",
        "clear-session-context-target",
        "set-session-port-target",
        "clear-session-port-target"
    };

    public static bool IsScopedTargetAction(string? action) =>
        action is not null && ScopedTargetActions.Contains(action);

    public static bool IsManagementAction(string? action) =>
        action is not null && ManagementActions.Contains(action);

    public static DevwtControlRequest MapManagement(DevwtWebUiAction action) =>
        action.Action.ToLowerInvariant() switch
        {
            "add-repository" => new DevwtControlRequest(
                DevwtControlOperation.AddRepository,
                AddRepository: new AddRepositoryRequest(
                    RequireAbsolutePath(action.WorktreePath, "worktreePath"),
                    TrimToNull(action.RepositoryName),
                    BuildLinkedRepositories(action.LinkedRepositories))),
            "pause-repository" => new DevwtControlRequest(
                DevwtControlOperation.Pause,
                RepositoryName: RequireText(action.RepositoryName, "repositoryName")),
            "resume-repository" => new DevwtControlRequest(
                DevwtControlOperation.Resume,
                RepositoryName: RequireText(action.RepositoryName, "repositoryName")),
            "describe-context" => new DevwtControlRequest(
                DevwtControlOperation.DescribeContext,
                WorktreePath: RequireAbsolutePath(action.WorktreePath, "worktreePath"),
                ContextDescription: action.ContextDescription,
                ClearContextDescription: action.ClearContextDescription),
            "find-port-processes" => new DevwtControlRequest(
                DevwtControlOperation.FindPortProcesses,
                PortQuery: BuildPortQuery(action)),
            "check-port" => new DevwtControlRequest(
                DevwtControlOperation.CheckPort,
                PortQuery: BuildPortQuery(action)),
            "link-map" => new DevwtControlRequest(
                DevwtControlOperation.LinkMap,
                LinkMap: new DevwtLinkMap(
                    RequireText(action.LinkedRepositoryName, "linkedRepositoryName"),
                    RequireAbsolutePath(action.SourceWorktreePath, "sourceWorktreePath"),
                    RequireAbsolutePath(action.TargetWorktreePath, "targetWorktreePath"))),
            "add-ide-watch" => new DevwtControlRequest(
                DevwtControlOperation.SetIdeWatch,
                IdeWatch: BuildIdeWatch(action)),
            "remove-ide-watch" => new DevwtControlRequest(
                DevwtControlOperation.RemoveIdeWatch,
                IdeWatchName: RequireText(action.IdeWatchName, "ideWatchName")),
            "set-browser-fallback-on-missing-port" => new DevwtControlRequest(
                DevwtControlOperation.SetBrowserFallbackOnMissingPort,
                BrowserFallbackOnMissingPort: action.BrowserFallbackOnMissingPort
                    ?? throw new ArgumentException("browserFallbackOnMissingPort is required.")),
            "set-browser-missing-port-policy" => new DevwtControlRequest(
                DevwtControlOperation.SetBrowserMissingPortPolicy,
                BrowserMissingPortPolicy: BuildBrowserMissingPortPolicy(action)),
            "clear-browser-missing-port-policy" => new DevwtControlRequest(
                DevwtControlOperation.SetBrowserMissingPortPolicy,
                BrowserMissingPortPolicy: new DevwtBrowserMissingPortPolicy(
                    RequireContextId(action),
                    RequirePort(action),
                    DevwtBrowserMissingPortPolicyMode.Disabled),
                ClearBrowserMissingPortPolicy: true),
            "stop-proxy-child" => new DevwtControlRequest(
                DevwtControlOperation.StopProxyChild,
                ProxyChildTarget: BuildProxyChildTarget(action, force: false)),
            "kill-proxy-child" => new DevwtControlRequest(
                DevwtControlOperation.StopProxyChild,
                ProxyChildTarget: BuildProxyChildTarget(action, force: true)),
            _ => throw new ArgumentException($"Unknown management action: {action.Action}")
        };

    public static DevwtControlRequest Map(DevwtWebUiAction action) =>
        action.Action.ToLowerInvariant() switch
        {
            "set-process-target" => new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessTarget: new DevwtProcessTarget(
                    RequireProcessId(action),
                    RequireContextId(action))),
            "clear-process-target" => new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessId: RequireProcessId(action),
                ClearProcessTarget: true),
            "set-process-port-target" => new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessPortTarget: new DevwtProcessPortTarget(
                    RequireProcessId(action),
                    RequireContextId(action),
                    RequirePort(action),
                    NormalizeScheme(action.Scheme))),
            "clear-process-port-target" => new DevwtControlRequest(
                DevwtControlOperation.SetProcessTarget,
                ProcessId: RequireProcessId(action),
                Port: RequirePort(action),
                ClearProcessPortTarget: true),
            "set-image-context-target" => new DevwtControlRequest(
                DevwtControlOperation.SetApplicationTarget,
                ApplicationContextTarget: new DevwtApplicationContextTarget(
                    RequireApplicationKey(action),
                    RequireContextId(action))),
            "clear-image-context-target" => new DevwtControlRequest(
                DevwtControlOperation.SetApplicationTarget,
                ApplicationTargetKey: RequireApplicationKey(action),
                ClearApplicationContextTarget: true),
            "set-application-target" => new DevwtControlRequest(
                DevwtControlOperation.SetApplicationTarget,
                ApplicationTarget: new DevwtApplicationTarget(
                    RequireApplicationKey(action),
                    RequireContextId(action),
                    RequirePort(action),
                    NormalizeScheme(action.Scheme))),
            "clear-application-target" => new DevwtControlRequest(
                DevwtControlOperation.SetApplicationTarget,
                ApplicationTargetKey: RequireApplicationKey(action),
                Port: RequirePort(action),
                ClearApplicationTarget: true),
            "set-session-context-target" => new DevwtControlRequest(
                DevwtControlOperation.SetSessionTarget,
                SessionContextTarget: new DevwtSessionContextTarget(
                    RequireSessionId(action),
                    RequireContextId(action))),
            "clear-session-context-target" => new DevwtControlRequest(
                DevwtControlOperation.SetSessionTarget,
                SessionId: RequireSessionId(action),
                ClearSessionContextTarget: true),
            "set-session-port-target" => new DevwtControlRequest(
                DevwtControlOperation.SetSessionTarget,
                SessionPortTarget: new DevwtSessionPortTarget(
                    RequireSessionId(action),
                    RequireContextId(action),
                    RequirePort(action),
                    NormalizeScheme(action.Scheme))),
            "clear-session-port-target" => new DevwtControlRequest(
                DevwtControlOperation.SetSessionTarget,
                SessionId: RequireSessionId(action),
                Port: RequirePort(action),
                ClearSessionPortTarget: true),
            _ => throw new ArgumentException($"Unknown scoped target action: {action.Action}")
        };

    private static int RequireProcessId(DevwtWebUiAction action) =>
        action.ProcessId is > 0
            ? action.ProcessId.Value
            : throw new ArgumentException("processId is required");

    private static int RequirePort(DevwtWebUiAction action) =>
        action.Port is > 0 and <= 65535
            ? action.Port.Value
            : throw new ArgumentException("port is required");

    private static DevwtBrowserMissingPortPolicy BuildBrowserMissingPortPolicy(
        DevwtWebUiAction action)
    {
        var mode = action.BrowserMissingPortPolicyMode
            ?? throw new ArgumentException("browserMissingPortPolicyMode is required.");
        var targetContextId = mode == DevwtBrowserMissingPortPolicyMode.Redirect
            ? RequireText(action.TargetContextId, "targetContextId")
            : null;
        return new DevwtBrowserMissingPortPolicy(
            RequireContextId(action),
            RequirePort(action),
            mode,
            targetContextId);
    }

    private static string RequireContextId(DevwtWebUiAction action) =>
        RequireText(action.ContextId, "contextId");

    private static string RequireApplicationKey(DevwtWebUiAction action) =>
        RequireText(action.ApplicationKey, "applicationKey");

    private static string RequireSessionId(DevwtWebUiAction action) =>
        RequireText(action.SessionId, "sessionId");

    private static DevwtPortQuery BuildPortQuery(DevwtWebUiAction action) =>
        new(
            RequirePort(action),
            RequireAbsolutePath(action.WorktreePath, "worktreePath"),
            TrimToNull(action.ContextId));

    private static IReadOnlyList<LinkedRepositoryInput> BuildLinkedRepositories(
        IReadOnlyList<LinkedRepositoryInput>? linkedRepositories) =>
        (linkedRepositories ?? [])
            .Select(link => new LinkedRepositoryInput(
                RequireText(link.Name, "linkedRepositories.name"),
                RequireAbsolutePath(link.Path, "linkedRepositories.path")))
            .ToArray();

    private static DevwtIdeWatch BuildIdeWatch(DevwtWebUiAction action)
    {
        var name = RequireText(action.IdeWatchName, "ideWatchName");
        var value = RequireText(action.IdeWatchSelectorValue, "ideWatchSelectorValue");
        return (action.IdeWatchSelectorKind ?? "").ToLowerInvariant() switch
        {
            "path" => new DevwtIdeWatch(name, ImagePath: RequireAbsolutePath(value, "ideWatchSelectorValue")),
            "app-id" => new DevwtIdeWatch(name, AppId: value),
            "package-family" => new DevwtIdeWatch(name, PackageFamilyName: value),
            _ => throw new ArgumentException("ideWatchSelectorKind must be path, app-id, or package-family")
        };
    }

    private static DevwtProxyChildTarget BuildProxyChildTarget(DevwtWebUiAction action, bool force) =>
        new(
            TrimToNull(action.ContextId),
            RequirePort(action),
            (action.Protocol ?? "").ToLowerInvariant() switch
            {
                "tcp" => GatewayRouteProtocol.Tcp,
                "udp" => GatewayRouteProtocol.Udp,
                _ => throw new ArgumentException("protocol must be tcp or udp")
            },
            force);

    private static string RequireText(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"{name} is required");

    private static string RequireAbsolutePath(string? value, string name)
    {
        value = RequireText(value, name);
        if (!Path.IsPathRooted(value))
        {
            throw new ArgumentException($"{name} must be an absolute path");
        }

        return Path.GetFullPath(value);
    }

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeScheme(string? scheme) =>
        string.IsNullOrWhiteSpace(scheme) ? "auto" : scheme.Trim().ToLowerInvariant();
}

public static class DevwtWebUiAssets
{
    public static string RenderShell() =>
        """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>DevWT</title>
              <link rel="icon" href="data:,">
              <style>
                :root {
                  color-scheme: light;
                  --bg: #f4f6f8;
                  --surface: #ffffff;
                  --surface-soft: #f8fafc;
                  --ink: #172026;
                  --muted: #667085;
                  --border: #d9e0e8;
                  --border-soft: #e8edf3;
                  --brand: #2563eb;
                  --brand-strong: #1d4ed8;
                  --teal: #0f766e;
                  --green: #15803d;
                  --amber: #b45309;
                  --red: #b42318;
                  --shadow: 0 16px 40px rgba(15, 23, 42, .07);
                  font-family: "Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, Arial, sans-serif;
                }
                * { box-sizing: border-box; }
                body {
                  margin: 0;
                  min-width: 320px;
                  background: var(--bg);
                  color: var(--ink);
                  font-size: 14px;
                  line-height: 1.45;
                }
                button, input, select { font: inherit; }
                button {
                  border: 1px solid var(--border);
                  background: var(--surface);
                  color: var(--ink);
                  min-height: 32px;
                  padding: 6px 10px;
                  border-radius: 7px;
                  cursor: pointer;
                  transition: background .15s ease, border-color .15s ease, color .15s ease, box-shadow .15s ease;
                }
                button:hover { border-color: #b7c3d0; background: #f8fafc; }
                button:focus-visible, input:focus-visible, a:focus-visible {
                  outline: 2px solid rgba(37, 99, 235, .35);
                  outline-offset: 2px;
                }
                button.primary {
                  background: var(--brand);
                  border-color: var(--brand);
                  color: white;
                  font-weight: 600;
                }
                button.primary:hover { background: var(--brand-strong); border-color: var(--brand-strong); }
                button.danger { color: var(--red); border-color: #f1b8b3; }
                a { color: var(--brand); text-decoration: none; font-weight: 600; }
                a:hover { text-decoration: underline; }
                code {
                  background: #eef2f7;
                  color: #1f2937;
                  padding: 2px 5px;
                  border-radius: 5px;
                  font-family: Consolas, "Cascadia Mono", monospace;
                  font-size: 12px;
                }
                .app-shell { min-height: 100vh; }
                .topbar {
                  position: sticky;
                  top: 0;
                  z-index: 10;
                  padding: 10px 24px 0;
                  background: rgba(255, 255, 255, .92);
                  border-bottom: 1px solid var(--border-soft);
                  backdrop-filter: blur(12px);
                }
                .topbar-main {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  gap: 16px;
                  padding-bottom: 10px;
                }
                .brand { display: flex; align-items: center; gap: 12px; width: 100%; max-width: 100%; min-width: 0; }
                .brand > div:not(.brand-mark) { flex: 1 1 auto; min-width: 0; }
                .brand-mark {
                  display: grid;
                  flex: 0 0 auto;
                  place-items: center;
                  width: 38px;
                  height: 38px;
                  border-radius: 9px;
                  background: #172026;
                  color: #fff;
                  font-weight: 750;
                  letter-spacing: 0;
                }
                h1, h2, h3, p { margin: 0; }
                h1 { font-size: 20px; line-height: 1.15; letter-spacing: 0; }
                .brand-subtitle { margin-top: 2px; color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }
                .top-actions, .row-actions, .section-actions, .method-list { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
                .tabs {
                  display: flex;
                  align-items: center;
                  gap: 18px;
                  overflow-x: auto;
                  scrollbar-width: none;
                }
                .tabs::-webkit-scrollbar { display: none; }
                .tabs button {
                  position: relative;
                  flex: 0 0 auto;
                  min-height: 38px;
                  padding: 6px 2px 10px;
                  border: 0;
                  border-radius: 0;
                  background: transparent;
                  color: var(--muted);
                  font-weight: 650;
                }
                .tabs button:hover { background: transparent; color: var(--ink); }
                .tabs button[aria-selected="true"] { color: var(--brand-strong); }
                .tabs button[aria-selected="true"]::after {
                  content: "";
                  position: absolute;
                  right: 0;
                  bottom: 0;
                  left: 0;
                  height: 2px;
                  background: var(--brand);
                }
                main {
                  width: min(1440px, 100%);
                  margin: 0 auto;
                  padding: 16px 24px 36px;
                }
                .status-strip {
                  display: flex;
                  align-items: center;
                  gap: 6px 18px;
                  min-height: 34px;
                  margin-bottom: 14px;
                  padding: 7px 12px;
                  border: 1px solid var(--border-soft);
                  border-radius: 7px;
                  background: var(--surface);
                  color: var(--muted);
                  font-size: 12px;
                  overflow-x: auto;
                  white-space: nowrap;
                }
                .status-strip strong { color: var(--ink); }
                .status-strip .live { color: var(--green); font-weight: 700; }
                .label {
                  display: block;
                  color: var(--muted);
                  font-size: 12px;
                  font-weight: 650;
                  margin-bottom: 5px;
                }
                .tab-panel[hidden] { display: none; }
                .tab-heading {
                  display: flex;
                  align-items: flex-end;
                  justify-content: space-between;
                  gap: 16px;
                  margin-bottom: 12px;
                }
                .tab-heading h2 { font-size: 18px; }
                .surface {
                  background: var(--surface);
                  border: 1px solid var(--border-soft);
                  border-radius: 8px;
                  box-shadow: var(--shadow);
                  overflow: hidden;
                }
                .surface + .surface { margin-top: 16px; }
                .section-header {
                  display: flex;
                  justify-content: space-between;
                  align-items: flex-start;
                  gap: 16px;
                  padding: 16px 18px;
                  border-bottom: 1px solid var(--border-soft);
                  background: #fff;
                }
                h2 { font-size: 15px; line-height: 1.25; }
                .section-note { color: var(--muted); font-size: 12px; margin-top: 3px; }
                .target-summary {
                  display: grid;
                  grid-template-columns: repeat(2, minmax(0, 1fr));
                  gap: 10px;
                  padding: 16px 18px;
                }
                .target-summary > div {
                  min-width: 0;
                  padding: 12px;
                  background: var(--surface-soft);
                  border: 1px solid var(--border-soft);
                  border-radius: 8px;
                }
                .target-summary strong {
                  display: block;
                  font-size: 16px;
                  white-space: nowrap;
                  overflow: hidden;
                  text-overflow: ellipsis;
                }
                .muted { color: var(--muted); }
                .ok { color: var(--green); font-weight: 700; }
                .paused { color: var(--amber); font-weight: 700; }
                .status-pill, .port-pill {
                  display: inline-flex;
                  align-items: center;
                  gap: 6px;
                  min-height: 24px;
                  border-radius: 999px;
                  padding: 3px 9px;
                  font-size: 12px;
                  font-weight: 700;
                  border: 1px solid transparent;
                  white-space: nowrap;
                }
                .status-pill.active { background: #ecfdf3; color: var(--green); border-color: #bbf7d0; }
                .status-pill.paused { background: #fff7ed; color: var(--amber); border-color: #fed7aa; }
                .port-pill { background: #eff6ff; color: var(--brand-strong); border-color: #bfdbfe; }
                .port-stack { display: grid; gap: 8px; min-width: 220px; }
                .port-line {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  gap: 8px;
                  padding: 8px;
                  border: 1px solid var(--border-soft);
                  border-radius: 8px;
                  background: #fbfdff;
                }
                .port-line.active { border-color: #93c5fd; box-shadow: inset 3px 0 0 var(--brand); }
                .candidate-list {
                  display: grid;
                  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
                  gap: 10px;
                  padding: 16px 18px;
                }
                .routing-toolbar {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  gap: 16px;
                  padding: 14px 18px;
                  border-bottom: 1px solid var(--border-soft);
                  background: #fbfcfe;
                }
                .routing-toolbar select, .row-actions select {
                  min-height: 34px;
                  min-width: 220px;
                  max-width: 420px;
                  border: 1px solid var(--border);
                  border-radius: 7px;
                  padding: 6px 9px;
                  background: #fff;
                  color: var(--ink);
                }
                .global-routing {
                  display: grid;
                  grid-template-columns: minmax(220px, .65fr) minmax(320px, 1.35fr);
                  gap: 14px;
                  padding: 16px 18px;
                }
                .global-routing[hidden], .port-groups[hidden] { display: none; }
                .routing-summary {
                  min-width: 0;
                  padding: 12px;
                  border-left: 3px solid var(--brand);
                  background: var(--surface-soft);
                }
                .port-groups { display: grid; gap: 10px; padding: 14px 18px 18px; }
                .port-group {
                  border: 1px solid var(--border-soft);
                  border-radius: 8px;
                  background: #f8fafc;
                  overflow: hidden;
                }
                .port-group-head {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  gap: 12px;
                  padding: 12px;
                  border-bottom: 1px solid var(--border-soft);
                  background: #fff;
                }
                .port-group-control {
                  display: flex;
                  align-items: center;
                  gap: 8px;
                  flex-wrap: wrap;
                }
                .compact-field {
                  display: inline-flex;
                  align-items: center;
                  gap: 6px;
                  font-size: 12px;
                  color: var(--muted);
                  white-space: nowrap;
                }
                .port-route-list { display: grid; }
                .port-route-row {
                  display: grid;
                  grid-template-columns: minmax(180px, .8fr) minmax(240px, 1.2fr) auto;
                  gap: 12px;
                  align-items: center;
                  min-width: 0;
                  padding: 10px 12px;
                  border-top: 1px solid var(--border-soft);
                  background: #fff;
                }
                .port-route-row:first-child { border-top: 0; }
                .candidate {
                  border: 1px solid var(--border-soft);
                  background: #fff;
                  border-radius: 8px;
                  padding: 12px;
                  min-width: 0;
                }
                .candidate.active { border-color: #93c5fd; box-shadow: inset 3px 0 0 var(--brand); }
                .candidate-head {
                  display: flex;
                  justify-content: space-between;
                  align-items: flex-start;
                  gap: 10px;
                  margin-bottom: 8px;
                }
                .method-list { margin-top: 10px; }
                .method-list a, .method-list span {
                  border: 1px solid var(--border);
                  border-radius: 7px;
                  padding: 5px 8px;
                  min-height: 30px;
                  display: inline-flex;
                  align-items: center;
                  background: #fff;
                  font-size: 12px;
                }
                .table-toolbar {
                  display: flex;
                  justify-content: space-between;
                  gap: 12px;
                  padding: 12px 18px;
                  border-bottom: 1px solid var(--border-soft);
                  background: #fbfcfe;
                }
                .search {
                  width: min(420px, 100%);
                  min-height: 34px;
                  border: 1px solid var(--border);
                  border-radius: 8px;
                  padding: 7px 10px;
                  background: #fff;
                }
                .field-grid {
                  display: grid;
                  grid-template-columns: repeat(6, minmax(140px, 1fr));
                  gap: 10px;
                  padding: 16px 18px;
                  border-bottom: 1px solid var(--border-soft);
                  background: #fbfcfe;
                }
                .field-grid label {
                  display: grid;
                  gap: 5px;
                  color: var(--muted);
                  font-size: 12px;
                  font-weight: 650;
                }
                .field-grid input, .field-grid select {
                  min-height: 34px;
                  border: 1px solid var(--border);
                  border-radius: 8px;
                  padding: 7px 9px;
                  background: #fff;
                  color: var(--ink);
                  min-width: 0;
                }
                .field-grid button {
                  align-self: end;
                }
                .segmented {
                  display: inline-flex;
                  gap: 2px;
                  padding: 3px;
                  border: 1px solid var(--border);
                  border-radius: 8px;
                  background: #eef2f7;
                }
                .segmented button {
                  border: 0;
                  background: transparent;
                  min-height: 28px;
                  padding: 4px 9px;
                  color: var(--muted);
                }
                .segmented button.selected {
                  background: #fff;
                  color: var(--ink);
                  box-shadow: 0 1px 2px rgba(15, 23, 42, .08);
                }
                .activity-toolbar { align-items: center; }
                .activity-filters {
                  display: flex;
                  align-items: center;
                  gap: 8px;
                  flex: 1 1 auto;
                  min-width: 0;
                }
                .activity-filters select {
                  min-height: 34px;
                  border: 1px solid var(--border);
                  border-radius: 7px;
                  padding: 6px 9px;
                  background: #fff;
                  color: var(--ink);
                }
                .activity-view[hidden] { display: none; }
                .activity-groups { background: #fff; }
                .activity-scope {
                  border: 0;
                  border-bottom: 1px solid var(--border-soft);
                  background: #fff;
                }
                .activity-scope:last-child { border-bottom: 0; }
                .activity-scope.process-scope {
                  margin-left: 18px;
                  border-left: 2px solid #dbeafe;
                }
                .activity-scope.session-scope {
                  margin-left: 18px;
                  border-left: 2px solid #ccfbf1;
                }
                .activity-scope > summary {
                  display: flex;
                  align-items: center;
                  gap: 10px;
                  min-width: 0;
                  padding: 11px 16px;
                  cursor: pointer;
                  list-style: none;
                }
                .activity-scope > summary::-webkit-details-marker { display: none; }
                .activity-scope > summary::before {
                  content: ">";
                  flex: 0 0 auto;
                  color: var(--muted);
                  font-size: 18px;
                  line-height: 1;
                  transform: rotate(0deg);
                  transition: transform .12s ease;
                }
                .activity-scope[open] > summary::before { transform: rotate(90deg); }
                .activity-scope > summary:hover { background: #f8fafc; }
                .activity-scope-title {
                  flex: 1 1 auto;
                  min-width: 0;
                }
                .activity-scope-title strong,
                .activity-scope-title code {
                  display: inline-block;
                  max-width: 100%;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  vertical-align: bottom;
                  white-space: nowrap;
                }
                .activity-count {
                  flex: 0 0 auto;
                  max-width: 46%;
                  color: var(--muted);
                  font-size: 12px;
                  overflow-wrap: anywhere;
                  text-align: right;
                }
                .scope-target-editor {
                  display: grid;
                  gap: 8px;
                  padding: 10px 16px 12px 44px;
                  border-top: 1px solid var(--border-soft);
                  background: #fbfcfe;
                }
                .scope-target-row {
                  display: grid;
                  grid-template-columns: minmax(110px, 160px) minmax(220px, 420px) 1fr;
                  align-items: center;
                  gap: 10px;
                  min-width: 0;
                }
                .scope-target-row select {
                  width: 100%;
                  min-width: 0;
                  min-height: 32px;
                  border: 1px solid var(--border);
                  border-radius: 7px;
                  padding: 5px 8px;
                  background: #fff;
                }
                .scope-port-targets {
                  border-top: 1px solid var(--border-soft);
                  padding-top: 8px;
                }
                .scope-port-targets > summary {
                  color: var(--muted);
                  cursor: pointer;
                  font-size: 12px;
                  font-weight: 650;
                }
                .scope-port-list { display: grid; gap: 7px; padding-top: 8px; }
                .activity-children { border-top: 1px solid var(--border-soft); }
                .activity-entry {
                  display: grid;
                  grid-template-columns: 90px minmax(210px, 1fr) minmax(180px, .8fr) minmax(110px, auto);
                  gap: 12px;
                  align-items: start;
                  padding: 9px 16px 9px 62px;
                  border-top: 1px solid var(--border-soft);
                  background: #fff;
                  font-size: 12px;
                }
                .activity-entry:first-child { border-top: 0; }
                .activity-entry code { overflow-wrap: anywhere; }
                .table-wrap { overflow: auto; }
                table { border-collapse: collapse; width: 100%; min-width: 960px; }
                th, td {
                  border-bottom: 1px solid var(--border-soft);
                  padding: 11px 10px;
                  text-align: left;
                  vertical-align: top;
                  font-size: 13px;
                }
                th {
                  background: #f8fafc;
                  color: #475467;
                  font-size: 12px;
                  font-weight: 750;
                }
                tbody tr:hover td { background: #fbfdff; }
                .context-name { font-weight: 700; }
                .path-cell {
                  max-width: 460px;
                  white-space: nowrap;
                  overflow: hidden;
                  text-overflow: ellipsis;
                }
                .empty-state {
                  padding: 18px;
                  color: var(--muted);
                  background: #fbfcfe;
                  border: 1px dashed #cbd5e1;
                  border-radius: 8px;
                }
                #message {
                  min-height: 24px;
                  color: var(--muted);
                  font-size: 12px;
                }
                #message.has-message {
                  color: var(--teal);
                  font-weight: 650;
                }
                @media (max-width: 980px) {
                  .topbar-main, .section-header, .table-toolbar, .routing-toolbar, .tab-heading { align-items: stretch; flex-direction: column; }
                  .field-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                  .global-routing { grid-template-columns: 1fr; }
                  .port-route-row { grid-template-columns: 1fr; }
                  .activity-toolbar, .activity-filters { align-items: stretch; flex-direction: column; }
                  .activity-entry { grid-template-columns: 80px minmax(0, 1fr); }
                }
                @media (max-width: 560px) {
                  main { padding: 16px 12px 28px; }
                  .topbar { padding: 10px 12px 0; }
                  .brand-subtitle { display: none; }
                  .status-strip {
                    display: grid;
                    grid-template-columns: repeat(2, minmax(0, 1fr));
                    white-space: normal;
                    overflow: visible;
                  }
                  .field-grid { grid-template-columns: 1fr; }
                  .top-actions, .section-actions, .table-toolbar { width: 100%; }
                  .top-actions button, .section-actions button, .search, .routing-toolbar select { width: 100%; max-width: none; }
                  .activity-filters select, .activity-toolbar .segmented { width: 100%; }
                  .activity-toolbar .segmented button { flex: 1 1 0; }
                  .activity-scope > summary {
                    display: grid;
                    grid-template-columns: 18px minmax(0, 1fr);
                    align-items: start;
                  }
                  .activity-scope > summary::before { grid-column: 1; grid-row: 1 / span 2; }
                  .activity-scope-title { grid-column: 2; }
                  .activity-count { grid-column: 2; max-width: none; text-align: left; }
                  .activity-scope.process-scope, .activity-scope.session-scope { margin-left: 8px; }
                  .scope-target-editor { padding-left: 24px; }
                  .scope-target-row { grid-template-columns: 1fr; gap: 5px; }
                  .activity-entry { grid-template-columns: 1fr; padding-left: 28px; }
                }

                /* Console workspace layout */
                :root {
                  --bg: #eef1f4;
                  --surface-soft: #f7f9fb;
                  --border: #cfd7e1;
                  --border-soft: #e1e6ec;
                  --ink: #18212b;
                  --muted: #687383;
                  --brand: #1769e0;
                  --brand-strong: #0d56c5;
                  --teal: #08776c;
                  --shadow: none;
                }
                body { font-size: 13px; }
                button { min-height: 30px; padding: 5px 9px; border-radius: 6px; }
                select, input { border-radius: 6px !important; }
                code { border-radius: 4px; }
                .topbar { padding: 8px 20px 0; }
                .topbar-main { min-height: 44px; padding-bottom: 7px; }
                .brand-mark {
                  width: 32px;
                  height: 32px;
                  border-radius: 6px;
                  font-size: 12px;
                }
                h1 { font-size: 18px; }
                .brand-subtitle { margin-top: 0; }
                .tabs { gap: 20px; }
                .tabs button { min-height: 34px; padding-bottom: 8px; }
                main {
                  width: min(1560px, 100%);
                  padding: 12px 20px 28px;
                }
                .status-strip {
                  min-height: 30px;
                  margin-bottom: 12px;
                  padding: 5px 10px;
                  border-radius: 6px;
                }
                .tab-heading {
                  align-items: center;
                  min-height: 30px;
                  margin-bottom: 8px;
                }
                .tab-heading h2 { font-size: 17px; }
                .surface {
                  border-color: var(--border);
                  border-radius: 6px;
                }
                .section-header { padding: 12px 14px; }
                .surface + .surface { margin-top: 12px; }
                .routing-toolbar,
                .table-toolbar {
                  min-height: 52px;
                  padding: 9px 12px;
                }
                .segmented {
                  display: inline-flex;
                  align-items: center;
                  padding: 2px;
                  border: 1px solid var(--border);
                  border-radius: 7px;
                  background: #eef2f6;
                }
                .segmented button {
                  min-height: 28px;
                  border: 0;
                  border-radius: 5px;
                  background: transparent;
                  color: var(--muted);
                }
                .segmented button:hover { background: rgba(255,255,255,.7); }
                .segmented button.selected {
                  background: #fff;
                  color: var(--ink);
                  box-shadow: 0 1px 2px rgba(15, 23, 42, .1);
                }
                .global-routing {
                  grid-template-columns: minmax(260px, 360px) minmax(0, 1fr);
                  min-height: 180px;
                  padding: 20px;
                }
                .global-routing > label { align-self: start; }
                .global-routing select {
                  width: 100%;
                  min-height: 36px;
                  border: 1px solid var(--border);
                  padding: 6px 9px;
                  background: #fff;
                }
                .routing-summary {
                  min-height: 86px;
                  padding: 14px;
                  border-left-width: 2px;
                }
                .port-groups {
                  display: grid;
                  grid-template-columns: 260px minmax(0, 1fr);
                  gap: 0;
                  min-height: 520px;
                  padding: 0;
                }
                .routing-port-nav {
                  min-width: 0;
                  border-right: 1px solid var(--border-soft);
                  background: var(--surface-soft);
                }
                .pane-heading {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  gap: 8px;
                  min-height: 43px;
                  padding: 9px 12px;
                  border-bottom: 1px solid var(--border-soft);
                  color: var(--muted);
                  font-size: 11px;
                  font-weight: 750;
                  text-transform: uppercase;
                }
                .pane-heading strong {
                  color: var(--ink);
                  font-size: 12px;
                  text-transform: none;
                }
                .routing-port-list {
                  max-height: 620px;
                  overflow: auto;
                }
                .routing-port-button,
                .activity-option {
                  display: grid;
                  width: 100%;
                  min-width: 0;
                  border: 0;
                  border-bottom: 1px solid var(--border-soft);
                  border-radius: 0;
                  background: transparent;
                  text-align: left;
                }
                .routing-port-button {
                  grid-template-columns: minmax(0, 1fr) auto;
                  gap: 4px 8px;
                  padding: 11px 12px;
                }
                .routing-port-button:hover,
                .activity-option:hover { background: #f0f5fb; }
                .routing-port-button.selected,
                .activity-option.selected {
                  background: #e9f2ff;
                  box-shadow: inset 3px 0 0 var(--brand);
                }
                .routing-port-button strong { font-size: 14px; }
                .routing-port-button small,
                .activity-option small {
                  min-width: 0;
                  color: var(--muted);
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                }
                .routing-port-button .route-target {
                  grid-column: 1 / -1;
                  color: var(--teal);
                  font-size: 11px;
                }
                .routing-detail { min-width: 0; background: #fff; }
                .routing-detail-head {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  gap: 14px;
                  min-height: 64px;
                  padding: 12px 14px;
                  border-bottom: 1px solid var(--border-soft);
                }
                .routing-detail-head h3 { font-size: 16px; }
                .routing-detail-controls {
                  display: flex;
                  align-items: end;
                  justify-content: flex-end;
                  gap: 8px;
                  flex-wrap: wrap;
                }
                .routing-detail-controls label {
                  display: grid;
                  gap: 3px;
                  color: var(--muted);
                  font-size: 11px;
                  font-weight: 650;
                }
                .routing-detail-controls select {
                  min-width: 200px;
                  min-height: 32px;
                  border: 1px solid var(--border);
                  padding: 5px 8px;
                  background: #fff;
                  color: var(--ink);
                }
                .protocol-controls {
                  display: flex;
                  align-items: center;
                  gap: 10px;
                  min-height: 48px;
                  padding: 8px 14px;
                  border-bottom: 1px solid var(--border-soft);
                  background: var(--surface-soft);
                  overflow-x: auto;
                }
                .protocol-controls .compact-field {
                  padding-right: 10px;
                  border-right: 1px solid var(--border);
                }
                .protocol-controls select {
                  min-height: 30px;
                  border: 1px solid var(--border);
                  padding: 4px 7px;
                  background: #fff;
                }
                .route-context-head,
                .port-route-row {
                  display: grid;
                  grid-template-columns: minmax(180px, .8fr) minmax(300px, 1.5fr) minmax(210px, .8fr);
                  gap: 12px;
                  align-items: center;
                  min-width: 0;
                  padding: 9px 14px;
                }
                .route-context-head {
                  min-height: 36px;
                  border-bottom: 1px solid var(--border-soft);
                  background: #f8fafc;
                  color: var(--muted);
                  font-size: 11px;
                  font-weight: 750;
                  text-transform: uppercase;
                }
                .port-route-row {
                  border-top: 0;
                  border-bottom: 1px solid var(--border-soft);
                }
                .port-route-row.active { background: #f2f8ff; box-shadow: inset 3px 0 0 var(--brand); }
                .method-list,
                .row-actions { gap: 6px; }
                .method-list a,
                .method-list span { min-height: 28px; padding: 4px 7px; }
                .compact-port-list { display: flex; flex-wrap: wrap; gap: 5px; min-width: 190px; }
                .compact-port-list button {
                  display: inline-flex;
                  align-items: center;
                  gap: 5px;
                  min-height: 26px;
                  padding: 3px 7px;
                  color: var(--brand-strong);
                  font-size: 11px;
                }
                .compact-port-list button.active {
                  border-color: #7eb0f5;
                  background: #eaf3ff;
                  font-weight: 700;
                }
                .table-wrap table { min-width: 1040px; }
                .context-table-wrap,
                #activity-timeline {
                  max-height: calc(100vh - 290px);
                  min-height: 320px;
                  overflow: auto;
                }
                .context-table-wrap th,
                #activity-timeline th {
                  position: sticky;
                  top: 0;
                  z-index: 1;
                }
                th, td { padding: 9px 10px; }
                .activity-toolbar { align-items: center; }
                .activity-filters {
                  display: flex;
                  align-items: center;
                  gap: 8px;
                  min-width: 0;
                  flex: 1 1 auto;
                }
                .activity-filters select {
                  min-width: 190px;
                  min-height: 34px;
                  border: 1px solid var(--border);
                  padding: 6px 8px;
                  background: #fff;
                }
                .activity-explorer {
                  display: grid;
                  grid-template-columns: minmax(190px, .72fr) minmax(150px, .52fr) minmax(220px, .82fr) minmax(420px, 1.75fr);
                  min-height: 590px;
                }
                .activity-explorer[hidden] { display: none; }
                .activity-pane {
                  min-width: 0;
                  border-right: 1px solid var(--border-soft);
                  background: var(--surface-soft);
                }
                .activity-pane:nth-child(2),
                .activity-pane:nth-child(3) { background: #fff; }
                .activity-list {
                  max-height: 660px;
                  overflow: auto;
                }
                .activity-option {
                  gap: 3px;
                  min-height: 58px;
                  padding: 9px 11px;
                }
                .activity-option strong {
                  min-width: 0;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                }
                .activity-option-meta {
                  display: flex;
                  justify-content: space-between;
                  gap: 8px;
                  color: var(--muted);
                  font-size: 11px;
                }
                .activity-inspector {
                  min-width: 0;
                  background: #fff;
                }
                .inspector-heading {
                  display: flex;
                  align-items: flex-start;
                  justify-content: space-between;
                  gap: 12px;
                  min-height: 64px;
                  padding: 11px 14px;
                  border-bottom: 1px solid var(--border-soft);
                }
                .inspector-heading strong {
                  display: block;
                  max-width: 100%;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                }
                .inspector-heading > div {
                  flex: 1 1 auto;
                  min-width: 0;
                }
                .inspector-heading > .port-pill { flex: 0 0 auto; }
                .inspector-heading code {
                  display: inline-block;
                  max-width: 100%;
                  margin-top: 3px;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                }
                .scope-matrix-head,
                .scope-target-editor {
                  display: grid;
                  grid-template-columns: minmax(92px, 120px) minmax(180px, 1fr);
                  gap: 8px 12px;
                  align-items: start;
                  padding: 9px 14px;
                  border-bottom: 1px solid var(--border-soft);
                }
                .scope-matrix-head {
                  min-height: 36px;
                  background: #f8fafc;
                  color: var(--muted);
                  font-size: 11px;
                  font-weight: 750;
                  text-transform: uppercase;
                }
                .scope-identity { min-width: 0; padding-top: 5px; }
                .scope-identity strong,
                .scope-identity code {
                  display: block;
                  max-width: 100%;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                }
                .scope-target-controls { min-width: 0; }
                .scope-target-row {
                  display: grid;
                  grid-template-columns: minmax(84px, 110px) minmax(160px, 1fr);
                  gap: 7px;
                  align-items: center;
                }
                .scope-target-row + .scope-target-row { margin-top: 6px; }
                .scope-target-row select {
                  width: 100%;
                  min-width: 0;
                  min-height: 30px;
                  border: 1px solid var(--border);
                  padding: 4px 7px;
                  background: #fff;
                }
                .scope-port-label {
                  color: var(--muted);
                  font-size: 11px;
                  white-space: nowrap;
                }
                .activity-log-head,
                .activity-entry {
                  display: grid;
                  grid-template-columns: 74px minmax(160px, 1.1fr) minmax(140px, .9fr) minmax(92px, auto);
                  gap: 9px;
                  align-items: start;
                  padding: 8px 14px;
                }
                .activity-log-head {
                  border-bottom: 1px solid var(--border-soft);
                  background: #f8fafc;
                  color: var(--muted);
                  font-size: 11px;
                  font-weight: 750;
                  text-transform: uppercase;
                }
                .activity-entry {
                  border-top: 0;
                  border-bottom: 1px solid var(--border-soft);
                  font-size: 11px;
                }
                .activity-entry .port-pill {
                  max-width: 100%;
                  overflow: hidden;
                  text-overflow: ellipsis;
                }
                .settings-list {
                  display: grid;
                  min-height: 58px;
                }
                .settings-row {
                  display: grid;
                  grid-template-columns: minmax(200px, .8fr) minmax(300px, 1.4fr) auto;
                  gap: 12px;
                  align-items: center;
                  min-height: 58px;
                  padding: 10px 14px;
                }
                .settings-table { min-width: 780px !important; }
                .field-grid { padding: 12px 14px; }
                .app-shell {
                  display: grid;
                  grid-template-columns: 244px minmax(0, 1fr);
                  min-height: 100vh;
                }
                .topbar {
                  position: fixed;
                  inset: 0 auto 0 0;
                  z-index: 20;
                  display: flex;
                  flex-direction: column;
                  width: 244px;
                  padding: 20px 14px;
                  overflow-y: auto;
                  border: 0;
                  border-right: 1px solid #17233a;
                  background:
                    radial-gradient(circle at 18% 4%, rgba(51, 105, 255, .22), transparent 30%),
                    #0b1220;
                  color: #e8eef8;
                  backdrop-filter: none;
                }
                .topbar-main {
                  display: block;
                  min-height: 0;
                  padding: 0 6px;
                }
                .brand { align-items: center; }
                .brand-mark {
                  width: 38px;
                  height: 38px;
                  border: 1px solid rgba(255, 255, 255, .16);
                  border-radius: 10px;
                  background: linear-gradient(145deg, #3b82f6, #1559d6);
                  box-shadow: 0 9px 24px rgba(26, 99, 224, .34);
                  font-size: 12px;
                }
                .brand h1 { color: #fff; font-size: 17px; }
                .brand-subtitle { color: #92a3bb; font-size: 11px; }
                .top-actions {
                  position: fixed;
                  top: 16px;
                  right: 22px;
                  z-index: 30;
                }
                .top-actions .refresh-button {
                  min-height: 36px;
                  padding-inline: 13px;
                  border-color: #cbd5e1;
                  background: rgba(255, 255, 255, .92);
                  box-shadow: 0 6px 20px rgba(15, 23, 42, .08);
                  color: #27364a;
                }
                .tabs {
                  display: grid;
                  gap: 4px;
                  margin-top: 30px;
                  overflow: visible;
                }
                .tabs button {
                  display: flex;
                  align-items: center;
                  width: 100%;
                  min-height: 40px;
                  padding: 8px 11px;
                  border: 0;
                  border-radius: 7px;
                  background: transparent;
                  color: #98a8bd;
                  font-size: 13px;
                  font-weight: 600;
                  text-align: left;
                }
                .tabs button:hover { background: rgba(255,255,255,.065); color: #eef4ff; }
                .tabs button[aria-selected="true"] {
                  background: linear-gradient(90deg, rgba(41, 112, 238, .28), rgba(41, 112, 238, .12));
                  color: #fff;
                  box-shadow: inset 3px 0 0 #5b9aff;
                }
                .tabs button[aria-selected="true"]::after { display: none; }
                .nav-icon {
                  display: inline-grid;
                  place-items: center;
                  width: 21px;
                  margin-right: 9px;
                  color: #7facf7;
                  font-size: 15px;
                }
                .nav-status {
                  display: grid;
                  gap: 8px;
                  margin-top: auto;
                  padding: 14px 8px 0;
                  border-top: 1px solid rgba(255,255,255,.09);
                  color: #90a1b8;
                  font-size: 11px;
                }
                .nav-status strong { color: #e7eef9; font-size: 12px; }
                .live-dot {
                  display: inline-block;
                  width: 7px;
                  height: 7px;
                  margin-right: 6px;
                  border-radius: 50%;
                  background: #34d399;
                  box-shadow: 0 0 0 4px rgba(52, 211, 153, .12);
                }
                main {
                  grid-column: 2;
                  width: 100%;
                  max-width: 1720px;
                  padding: 74px 28px 40px;
                }
                .status-strip {
                  margin-bottom: 18px;
                  border-color: #dfe5ed;
                  background: rgba(255,255,255,.78);
                }
                .tab-heading {
                  align-items: flex-start;
                  margin-bottom: 14px;
                }
                .tab-heading h2 { font-size: 24px; letter-spacing: -.025em; }
                .tab-heading p { max-width: 680px; margin-top: 4px; color: var(--muted); }
                .surface {
                  border-color: #dce3ec;
                  border-radius: 10px;
                  background: rgba(255,255,255,.96);
                  box-shadow: 0 8px 28px rgba(25, 39, 64, .055);
                }
                .section-header { padding: 15px 17px; }
                .section-header h2 { font-size: 14px; }
                .metric-grid {
                  display: grid;
                  grid-template-columns: repeat(4, minmax(0, 1fr));
                  gap: 12px;
                  margin-bottom: 14px;
                }
                .metric-card {
                  position: relative;
                  min-width: 0;
                  padding: 17px;
                  overflow: hidden;
                  border: 1px solid #dce3ec;
                  border-radius: 10px;
                  background: rgba(255,255,255,.96);
                  box-shadow: 0 8px 24px rgba(25,39,64,.045);
                }
                .metric-card::after {
                  content: "";
                  position: absolute;
                  top: -24px;
                  right: -22px;
                  width: 72px;
                  height: 72px;
                  border-radius: 50%;
                  background: rgba(35, 105, 224, .07);
                }
                .metric-label {
                  display: block;
                  margin-bottom: 8px;
                  color: var(--muted);
                  font-size: 11px;
                  font-weight: 700;
                  letter-spacing: .04em;
                  text-transform: uppercase;
                }
                .metric-value { display: block; font-size: 27px; font-weight: 750; line-height: 1; letter-spacing: -.04em; }
                .metric-detail { display: block; margin-top: 8px; color: var(--muted); font-size: 11px; }
                .overview-grid {
                  display: grid;
                  grid-template-columns: minmax(0, 1.25fr) minmax(300px, .75fr);
                  gap: 14px;
                }
                .quick-grid {
                  display: grid;
                  grid-template-columns: repeat(2, minmax(0, 1fr));
                  gap: 9px;
                  padding: 13px;
                }
                .quick-action-card {
                  display: block;
                  min-height: 95px;
                  padding: 13px;
                  border-color: #e0e6ee;
                  background: #f8fafc;
                  text-align: left;
                }
                .quick-action-card:hover {
                  border-color: #a9c7f4;
                  background: #f2f7ff;
                  box-shadow: 0 5px 15px rgba(23,105,224,.08);
                }
                .quick-action-card strong { display: block; margin-bottom: 5px; color: #1d2a3c; }
                .quick-action-card span { color: var(--muted); font-size: 12px; line-height: 1.35; }
                .overview-contexts { display: grid; }
                .overview-context-row {
                  display: grid;
                  grid-template-columns: minmax(0, 1fr) auto;
                  gap: 10px;
                  align-items: center;
                  min-height: 55px;
                  padding: 9px 14px;
                  border-top: 1px solid var(--border-soft);
                }
                .overview-context-row:first-child { border-top: 0; }
                .overview-context-row strong,
                .overview-context-row small {
                  display: block;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                }
                .overview-context-row small { margin-top: 2px; color: var(--muted); }
                .admin-form {
                  display: grid;
                  gap: 12px;
                  padding: 15px 17px;
                  border-bottom: 1px solid var(--border-soft);
                  background: #fafbfd;
                }
                .form-grid {
                  display: grid;
                  grid-template-columns: repeat(3, minmax(0, 1fr));
                  gap: 10px;
                }
                .form-grid.two { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                .admin-form label,
                .dialog-form label {
                  display: grid;
                  gap: 5px;
                  min-width: 0;
                  color: var(--muted);
                  font-size: 11px;
                  font-weight: 700;
                }
                .admin-form input,
                .admin-form select,
                .dialog-form input,
                .dialog-form textarea,
                .dialog-form select,
                .diagnostic-form input,
                .diagnostic-form select {
                  width: 100%;
                  min-width: 0;
                  min-height: 36px;
                  border: 1px solid var(--border);
                  padding: 7px 9px;
                  background: #fff;
                  color: var(--ink);
                }
                .admin-form .form-actions { display: flex; justify-content: flex-end; gap: 8px; }
                .linked-repo-builder {
                  display: grid;
                  gap: 9px;
                  padding: 11px;
                  border: 1px solid var(--border-soft);
                  border-radius: 7px;
                  background: #fff;
                }
                .linked-repo-builder-head {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  gap: 12px;
                }
                .linked-repo-inputs { display: grid; gap: 8px; }
                .linked-repo-input-row {
                  display: grid;
                  grid-template-columns: minmax(130px, .5fr) minmax(240px, 1.5fr) auto;
                  gap: 8px;
                  align-items: end;
                }
                .tools-grid {
                  display: grid;
                  grid-template-columns: repeat(2, minmax(0, 1fr));
                  gap: 14px;
                }
                .tools-grid .wide { grid-column: 1 / -1; }
                .diagnostic-form {
                  display: grid;
                  grid-template-columns: minmax(0, 1fr) minmax(110px, .35fr);
                  gap: 10px;
                  padding: 15px 17px;
                }
                .diagnostic-form .form-actions {
                  display: flex;
                  grid-column: 1 / -1;
                  gap: 8px;
                  flex-wrap: wrap;
                }
                .terminal-output {
                  min-height: 90px;
                  margin: 0 17px 17px;
                  padding: 13px;
                  overflow: auto;
                  border: 1px solid #26354b;
                  border-radius: 7px;
                  background: #101827;
                  color: #cfdbeb;
                  font: 12px/1.55 Consolas, "Cascadia Mono", monospace;
                  white-space: pre-wrap;
                }
                .listener-list { display: grid; }
                .listener-row {
                  display: grid;
                  grid-template-columns: minmax(190px, .8fr) minmax(280px, 1.4fr) auto;
                  gap: 12px;
                  align-items: center;
                  min-height: 62px;
                  padding: 10px 16px;
                  border-top: 1px solid var(--border-soft);
                }
                .listener-row:first-child { border-top: 0; }
                .command-grid {
                  display: grid;
                  grid-template-columns: repeat(2, minmax(0, 1fr));
                  gap: 9px;
                  padding: 14px 16px 17px;
                }
                .command-card {
                  display: grid;
                  grid-template-columns: minmax(0, 1fr) auto;
                  gap: 8px;
                  align-items: center;
                  min-width: 0;
                  padding: 10px 11px;
                  border: 1px solid var(--border-soft);
                  border-radius: 7px;
                  background: #fafbfd;
                }
                .command-card code { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .description-button {
                  max-width: 220px;
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                }
                dialog {
                  width: min(540px, calc(100% - 28px));
                  padding: 0;
                  border: 1px solid #d8e0ea;
                  border-radius: 12px;
                  background: #fff;
                  color: var(--ink);
                  box-shadow: 0 25px 80px rgba(15,23,42,.24);
                }
                dialog::backdrop { background: rgba(12, 20, 34, .55); backdrop-filter: blur(3px); }
                .dialog-head {
                  display: flex;
                  align-items: flex-start;
                  justify-content: space-between;
                  gap: 16px;
                  padding: 17px 18px;
                  border-bottom: 1px solid var(--border-soft);
                }
                .dialog-head h2 { font-size: 17px; }
                .dialog-form { display: grid; gap: 13px; padding: 18px; }
                .dialog-form textarea { min-height: 92px; resize: vertical; }
                .dialog-actions { display: flex; justify-content: flex-end; gap: 8px; }
                #message {
                  position: fixed;
                  top: 16px;
                  right: 82px;
                  z-index: 50;
                  max-width: min(620px, calc(100vw - 120px));
                  min-height: 36px;
                  padding: 9px 13px;
                  border: 1px solid #bfe3d9;
                  border-radius: 8px;
                  background: #edfcf6;
                  box-shadow: 0 8px 28px rgba(15,23,42,.12);
                  color: #08776c;
                  font-size: 12px;
                  opacity: 0;
                  pointer-events: none;
                  transform: translateY(-8px);
                  transition: opacity .15s ease, transform .15s ease;
                }
                #message.has-message { opacity: 1; transform: translateY(0); }
                #message.is-error { border-color: #fecaca; background: #fff1f2; color: var(--red); }
                body.is-busy [data-action] { cursor: progress; opacity: .62; pointer-events: none; }

                @media (max-width: 1200px) {
                  .metric-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                  .overview-grid { grid-template-columns: 1fr; }
                  .activity-explorer {
                    grid-template-columns: minmax(190px, .8fr) minmax(150px, .6fr) minmax(220px, 1fr);
                  }
                  .activity-inspector { grid-column: 1 / -1; border-top: 1px solid var(--border); }
                }
                @media (max-width: 900px) {
                  .app-shell { display: block; }
                  .topbar {
                    position: sticky;
                    inset: auto;
                    display: block;
                    width: auto;
                    padding: 8px 12px 0;
                    overflow: visible;
                    border-right: 0;
                    border-bottom: 1px solid #17233a;
                  }
                  .topbar-main { display: flex; align-items: center; padding: 0; }
                  .brand-subtitle { display: none; }
                  .tabs {
                    display: flex;
                    gap: 4px;
                    margin-top: 8px;
                    overflow-x: auto;
                  }
                  .tabs button { width: auto; min-height: 36px; padding: 6px 10px; white-space: nowrap; }
                  .tabs button[aria-selected="true"] { box-shadow: inset 0 -2px 0 #5b9aff; }
                  .nav-icon { display: none; }
                  .nav-status { display: none; }
                  .top-actions { top: 10px; right: 12px; }
                  main { width: 100%; padding: 20px 16px 32px; }
                  #message { top: 58px; right: 12px; max-width: calc(100vw - 24px); }
                  .port-groups { grid-template-columns: 210px minmax(0, 1fr); }
                  .routing-detail-head { align-items: stretch; flex-direction: column; }
                  .routing-detail-controls { justify-content: flex-start; }
                  .route-context-head { display: none; }
                  .port-route-row { grid-template-columns: 1fr; }
                  .activity-explorer { grid-template-columns: repeat(3, minmax(180px, 1fr)); overflow-x: auto; }
                  .activity-pane { min-width: 180px; }
                }
                @media (max-width: 640px) {
                  .topbar { padding: 7px 10px 0; }
                  .topbar-main {
                    align-items: center;
                    flex-direction: row;
                  }
                  .top-actions {
                    flex: 0 0 auto;
                    width: auto;
                  }
                  .top-actions button { width: auto; }
                  main { padding: 10px 10px 22px; }
                  .status-strip { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                  .section-note { display: none; }
                  .port-groups { grid-template-columns: 1fr; }
                  .routing-port-nav { border-right: 0; border-bottom: 1px solid var(--border); }
                  .routing-port-list {
                    display: flex;
                    max-height: none;
                    overflow-x: auto;
                  }
                  .routing-port-button {
                    flex: 0 0 180px;
                    border-right: 1px solid var(--border-soft);
                  }
                  .routing-detail-controls select { min-width: 0; width: 100%; }
                  .activity-toolbar,
                  .activity-filters { align-items: stretch; flex-direction: column; }
                  .activity-filters select,
                  .activity-filters .search { width: 100%; max-width: none; }
                  .activity-explorer { display: block; overflow: visible; }
                  .activity-pane { border-right: 0; border-bottom: 1px solid var(--border); }
                  .activity-list { display: flex; max-height: none; overflow-x: auto; }
                  .activity-option { flex: 0 0 220px; border-right: 1px solid var(--border-soft); }
                  .scope-target-editor,
                  .scope-matrix-head { grid-template-columns: minmax(72px, 84px) minmax(0, 1fr); }
                  .activity-log-head { display: none; }
                  .activity-entry { grid-template-columns: 68px minmax(0, 1fr); }
                  .activity-entry > :nth-child(1) { grid-column: 1; grid-row: 1; }
                  .activity-entry > :nth-child(2) { grid-column: 2; grid-row: 1; }
                  .activity-entry > :nth-child(3) { grid-column: 2; grid-row: 2; }
                  .activity-entry > :nth-child(4) { grid-column: 1; grid-row: 2; }
                  .metric-grid,
                  .quick-grid,
                  .tools-grid,
                  .form-grid,
                  .form-grid.two,
                  .command-grid { grid-template-columns: 1fr; }
                  .tools-grid .wide { grid-column: auto; }
                  .listener-row { grid-template-columns: 1fr; }
                  .linked-repo-input-row { grid-template-columns: 1fr; }
                  .diagnostic-form { grid-template-columns: 1fr; }
                  .tab-heading h2 { font-size: 21px; }
                  .top-actions .refresh-button { width: 36px; padding: 0; font-size: 0; }
                  .top-actions .refresh-button::before { content: "↻"; font-size: 18px; }
                }
              </style>
            </head>
            <body>
              <div class="app-shell">
                <header class="topbar">
                  <div class="topbar-main">
                    <div class="brand">
                      <div class="brand-mark">DW</div>
                      <div>
                        <h1>DevWT</h1>
                        <p class="brand-subtitle">Worktree control center</p>
                      </div>
                    </div>
                    <div class="top-actions">
                      <button class="refresh-button" onclick="refresh()" title="Refresh status">Refresh</button>
                      <span id="message" aria-live="polite"></span>
                    </div>
                  </div>
                  <nav class="tabs" role="tablist" aria-label="DevWT sections" onkeydown="handleTabKey(event)">
                    <button role="tab" data-tab="overview" aria-controls="panel-overview" aria-selected="true" onclick="selectTab('overview')"><span class="nav-icon">⌂</span>Overview</button>
                    <button role="tab" data-tab="contexts" aria-controls="panel-contexts" aria-selected="false" tabindex="-1" onclick="selectTab('contexts')"><span class="nav-icon">◇</span>Contexts</button>
                    <button role="tab" data-tab="routing" aria-controls="panel-routing" aria-selected="false" tabindex="-1" onclick="selectTab('routing')"><span class="nav-icon">⇄</span>Routing</button>
                    <button role="tab" data-tab="activity" aria-controls="panel-activity" aria-selected="false" tabindex="-1" onclick="selectTab('activity')"><span class="nav-icon">◌</span>Activity</button>
                    <button role="tab" data-tab="tools" aria-controls="panel-tools" aria-selected="false" tabindex="-1" onclick="selectTab('tools')"><span class="nav-icon">⌁</span>Tools</button>
                    <button role="tab" data-tab="settings" aria-controls="panel-settings" aria-selected="false" tabindex="-1" onclick="selectTab('settings')"><span class="nav-icon">⚙</span>Settings</button>
                  </nav>
                  <div id="nav-status" class="nav-status">
                    <strong><span class="live-dot"></span>Console connected</strong>
                    <span>Waiting for runtime status</span>
                  </div>
                </header>
                <main>
                  <div id="status-strip" class="status-strip" aria-label="DevWT status"></div>

                  <section id="panel-overview" class="tab-panel" role="tabpanel" aria-label="Overview">
                    <div class="tab-heading">
                      <div>
                        <h2>Overview</h2>
                        <p>See the state of local worktree routing and jump straight to the task you need.</p>
                      </div>
                    </div>
                    <div id="overview-metrics" class="metric-grid"></div>
                    <div class="overview-grid">
                      <section class="surface">
                        <div class="section-header">
                          <div><h2>Contexts at a glance</h2><p class="section-note">Active worktrees and their observed listeners</p></div>
                          <button onclick="selectTab('contexts')">View all</button>
                        </div>
                        <div id="overview-contexts" class="overview-contexts"></div>
                      </section>
                      <section class="surface">
                        <div class="section-header">
                          <div><h2>Quick actions</h2><p class="section-note">Common management tasks</p></div>
                        </div>
                        <div class="quick-grid">
                          <button class="quick-action-card" onclick="focusAddRepository()"><strong>Register repository</strong><span>Discover its worktrees and add them to DevWT.</span></button>
                          <button class="quick-action-card" onclick="selectTab('routing')"><strong>Choose routing</strong><span>Set one context or a separate target per port.</span></button>
                          <button class="quick-action-card" onclick="selectTab('tools')"><strong>Check a port</strong><span>Find the real backend process behind a virtual port.</span></button>
                          <button class="quick-action-card" onclick="focusIdeWatch()"><strong>Add IDE watch</strong><span>Route applications launched by an IDE or Store app.</span></button>
                        </div>
                      </section>
                    </div>
                  </section>

                  <section id="panel-routing" class="tab-panel" role="tabpanel" aria-label="Routing" hidden>
                    <div class="tab-heading">
                      <div><h2>Routing</h2><p>Control which worktree receives each localhost request.</p></div>
                    </div>
                    <div class="surface">
                      <div class="routing-toolbar">
                        <div class="segmented" role="group" aria-label="Routing mode">
                          <button id="routing-mode-global" onclick="setRoutingMode('global-context')">One context</button>
                          <button id="routing-mode-per-port" onclick="setRoutingMode('per-port')">Per port</button>
                        </div>
                        <span id="routing-mode-state" class="muted"></span>
                      </div>
                      <div id="global-routing" class="global-routing">
                        <label>
                          <span class="label">Context</span>
                          <select id="global-context-select" onchange="setGlobalContext(this.value)"></select>
                        </label>
                        <div id="global-routing-summary" class="routing-summary"></div>
                      </div>
                      <div id="port-routing-groups" class="port-groups"></div>
                    </div>
                  </section>

                  <section id="panel-contexts" class="tab-panel" role="tabpanel" aria-label="Contexts" hidden>
                    <div class="tab-heading">
                      <div><h2>Contexts</h2><p>Manage registered worktrees, task labels, runtime state, and open ports.</p></div>
                      <button class="primary" onclick="focusAddRepository()">Register repository</button>
                    </div>
                    <div class="surface">
                      <div class="table-toolbar">
                        <input id="context-search" class="search" placeholder="Search context, path, runtime, or port" oninput="renderContextsView()">
                        <div class="segmented" role="group" aria-label="Context filter">
                          <button id="filter-all" class="selected" onclick="setContextFilter('all')">All</button>
                          <button id="filter-active" onclick="setContextFilter('active')">Active</button>
                          <button id="filter-paused" onclick="setContextFilter('paused')">Paused</button>
                          <button id="filter-ports" onclick="setContextFilter('ports')">With ports</button>
                        </div>
                      </div>
                      <div class="table-wrap context-table-wrap">
                        <table>
                          <thead><tr><th>Name</th><th>Status</th><th>IP</th><th>Runtime</th><th>Worktree</th><th>Open ports</th><th>Actions</th></tr></thead>
                          <tbody id="contexts"></tbody>
                        </table>
                      </div>
                    </div>
                  </section>

                  <section id="panel-activity" class="tab-panel" role="tabpanel" aria-label="Activity" hidden>
                    <div class="tab-heading">
                      <div><h2>Activity</h2><p>Inspect how browser, process, image, and session signals selected a context.</p></div>
                    </div>
                    <div class="surface">
                      <div class="table-toolbar activity-toolbar">
                        <div class="activity-filters">
                          <input id="activity-search" class="search" placeholder="Search image, process, session, endpoint, or context" oninput="renderActivityView()">
                          <select id="activity-reason-filter" onchange="renderActivityView()">
                            <option value="">All decisions</option>
                            <option value="self-process">Self process</option>
                            <option value="process-context">Process context</option>
                            <option value="session-default">Session default</option>
                            <option value="session-context">Session affinity</option>
                            <option value="app-default">Image default</option>
                            <option value="browser-active">Browser target</option>
                            <option value="context-cookie">Context cookie</option>
                            <option value="global-active">Configured fallback</option>
                            <option value="last-process">Last process</option>
                            <option value="newest">Newest listener</option>
                          </select>
                        </div>
                        <div class="segmented" role="group" aria-label="Activity view">
                          <button id="activity-view-grouped" class="selected" onclick="setActivityView('grouped')">Callers</button>
                          <button id="activity-view-timeline" onclick="setActivityView('timeline')">Timeline</button>
                        </div>
                      </div>
                      <div id="activity-grouped" class="activity-view activity-explorer"></div>
                      <div id="activity-timeline" class="activity-view table-wrap" hidden>
                        <table>
                          <thead><tr><th>Time</th><th>Application</th><th>Session</th><th>Endpoint</th><th>Target context</th><th>Reason</th></tr></thead>
                          <tbody id="connection-history"></tbody>
                        </table>
                      </div>
                    </div>
                  </section>

                  <section id="panel-tools" class="tab-panel" role="tabpanel" aria-label="Tools" hidden>
                    <div class="tab-heading">
                      <div><h2>Tools</h2><p>Diagnose virtual ports, manage backend listeners, and prepare interactive launch commands.</p></div>
                    </div>
                    <div class="tools-grid">
                      <section class="surface">
                        <div class="section-header">
                          <div><h2>Port diagnostics</h2><p class="section-note">Equivalent to <code>devwt port check/process</code></p></div>
                        </div>
                        <form class="diagnostic-form" onsubmit="runPortDiagnostic(event, 'find-port-processes')">
                          <label>Context
                            <select id="diagnostic-context" required>
                              <option value="" selected disabled>Select a context</option>
                            </select>
                          </label>
                          <label>Original port
                            <input id="diagnostic-port" type="number" min="1" max="65535" inputmode="numeric" placeholder="44334" required>
                          </label>
                          <div class="form-actions">
                            <button data-action="check-port" type="button" onclick="runPortDiagnostic(event, 'check-port')">Check listener</button>
                            <button class="primary" data-action="find-port-processes" type="submit">Find processes</button>
                          </div>
                        </form>
                        <pre id="diagnostic-output" class="terminal-output">Choose a context and port to begin.</pre>
                      </section>

                      <section class="surface">
                        <div class="section-header">
                          <div><h2>Interactive launchers</h2><p class="section-note">Copy commands that must open in your desktop session</p></div>
                        </div>
                        <div class="command-grid">
                          <div class="command-card"><code>devwt run -- &lt;program&gt;</code><button onclick="copyCommand('devwt run -- &lt;program&gt; [args...]')">Copy</button></div>
                          <div class="command-card"><code>devwt exec -- &lt;program&gt;</code><button onclick="copyCommand('devwt exec -- &lt;program&gt; [args...]')">Copy</button></div>
                          <div class="command-card"><code>devwt terminal</code><button onclick="copyCommand('devwt terminal')">Copy</button></div>
                          <div class="command-card"><code>devwt shortcut list --taskbar</code><button onclick="copyCommand('devwt shortcut list --taskbar')">Copy</button></div>
                          <div class="command-card"><code>devwt shell status</code><button onclick="copyCommand('devwt shell status')">Copy</button></div>
                          <div class="command-card"><code>devwt gateway-cert status</code><button onclick="copyCommand('devwt gateway-cert status')">Copy</button></div>
                        </div>
                      </section>

                      <section class="surface wide">
                        <div class="section-header">
                          <div><h2>Backend listeners</h2><p class="section-note">Stop only the application behind a selected virtual port; force kill always asks for confirmation.</p></div>
                        </div>
                        <div id="listener-list" class="listener-list"></div>
                      </section>
                    </div>
                  </section>

                  <section id="panel-settings" class="tab-panel" role="tabpanel" aria-label="Settings" hidden>
                    <div class="tab-heading">
                      <div><h2>Settings</h2><p>Configure repositories, linked worktrees, IDE discovery, and session identity.</p></div>
                    </div>

                    <section class="surface">
                      <div class="section-header">
                        <div><h2>Register Repository</h2><p class="section-note">Equivalent to <code>devwt add</code>; the path must belong to a Git repository.</p></div>
                      </div>
                      <form id="add-repository-form" class="admin-form" onsubmit="addRepository(event)">
                        <div class="form-grid two">
                          <label>Repository or worktree path
                            <input id="repository-path" autocomplete="off" placeholder="D:\GitHub\my-app" required>
                          </label>
                          <label>Display name <span class="muted">(optional)</span>
                            <input id="repository-name" autocomplete="off" placeholder="my-app">
                          </label>
                        </div>
                        <div class="linked-repo-builder">
                          <div class="linked-repo-builder-head">
                            <div><strong>Linked repositories</strong><p class="section-note">Optional; add each repository that should follow matching worktrees.</p></div>
                            <button type="button" onclick="addLinkedRepositoryInput()">Add linked repository</button>
                          </div>
                          <div id="linked-repository-inputs" class="linked-repo-inputs"></div>
                        </div>
                        <div class="form-actions"><button class="primary" data-action="add-repository" type="submit">Register and discover worktrees</button></div>
                      </form>
                    </section>

                    <section class="surface">
                      <div class="section-header"><h2>Runtime Backends</h2></div>
                      <div id="runtime-backends" class="settings-list"></div>
                    </section>

                    <section class="surface">
                      <div class="section-header">
                        <div>
                          <h2>Browser Missing-Port Fallback</h2>
                          <p class="section-note">Default for worktree/port pairs without an extension policy. Automatic, redirect, or fail-closed policies selected on a worktree card override this default across every extension tab using that worktree.</p>
                        </div>
                      </div>
                      <div class="routing-toolbar">
                        <div class="segmented" role="group" aria-label="Browser missing-port fallback">
                          <button id="browser-fallback-off" data-action="set-browser-fallback-on-missing-port" onclick="setBrowserFallbackOnMissingPort(false)">Off · return 502</button>
                          <button id="browser-fallback-on" data-action="set-browser-fallback-on-missing-port" onclick="setBrowserFallbackOnMissingPort(true)">On · decide automatically</button>
                        </div>
                        <span id="browser-fallback-state" class="muted"></span>
                      </div>
                    </section>

                    <section class="surface">
                      <div class="section-header">
                        <div><h2>IDE Watches</h2><p class="section-note">Persistently identify launchers by executable path, Store App ID, or package family.</p></div>
                      </div>
                      <form id="ide-watch-form" class="admin-form" onsubmit="addIdeWatch(event)">
                        <div class="form-grid">
                          <label>Name
                            <input id="ide-watch-name" autocomplete="off" placeholder="Rider" required>
                          </label>
                          <label>Selector type
                            <select id="ide-watch-selector-kind" required>
                              <option value="" selected disabled>Choose selector</option>
                              <option value="path">Executable path</option>
                              <option value="app-id">Store App ID</option>
                              <option value="package-family">Package family</option>
                            </select>
                          </label>
                          <label>Selector value
                            <input id="ide-watch-selector-value" autocomplete="off" placeholder="C:\Program Files\...\rider64.exe" required>
                          </label>
                        </div>
                        <div class="form-actions"><button class="primary" data-action="add-ide-watch" type="submit">Add IDE watch</button></div>
                      </form>
                      <div id="ide-watches" class="settings-list"></div>
                    </section>

                    <section class="surface">
                      <div class="section-header"><h2>Session Rules</h2></div>
                      <form id="session-rule-form" class="field-grid" onsubmit="addSessionRule(event)">
                        <label>Name
                          <input id="session-rule-name" autocomplete="off" placeholder="Codex">
                        </label>
                        <label>Match
                          <select id="session-match-kind" name="sessionMatchKind" required>
                            <option value="" selected disabled>Session selector</option>
                            <option value="env">Environment variable</option>
                            <option value="process-name">Process name</option>
                            <option value="image-path">Image path contains</option>
                            <option value="command-line">Command line contains</option>
                          </select>
                        </label>
                        <label>Match value
                          <input id="session-match-value" autocomplete="off" required>
                        </label>
                        <label>Identity
                          <select id="session-identity-kind" name="sessionIdentityKind" required>
                            <option value="" selected disabled>Choose identity</option>
                            <option value="env">Environment variable</option>
                            <option value="root-process">Root process</option>
                            <option value="process">Process</option>
                            <option value="command-line-regex">Command line regex</option>
                          </select>
                        </label>
                        <label>Identity value
                          <input id="session-identity-value" autocomplete="off" placeholder="CODEX_THREAD_ID">
                        </label>
                        <label>Prefix
                          <input id="session-prefix" autocomplete="off" placeholder="codex:">
                        </label>
                        <button class="primary" data-action="add-session-rule" type="submit">Add rule</button>
                      </form>
                      <div class="table-wrap">
                        <table class="settings-table">
                          <thead><tr><th>Name</th><th>Match</th><th>Identity</th><th>Prefix</th><th>Actions</th></tr></thead>
                          <tbody id="session-rules"></tbody>
                        </table>
                      </div>
                    </section>

                    <section class="surface">
                      <div class="section-header">
                        <div><h2>Linked Worktree Map</h2><p class="section-note">Connect a source worktree to the matching worktree of a linked repository.</p></div>
                      </div>
                      <form id="link-map-form" class="admin-form" onsubmit="addLinkMap(event)">
                        <div class="form-grid">
                          <label>Linked repository name
                            <input id="link-map-repository" autocomplete="off" placeholder="abp" required>
                          </label>
                          <label>Source worktree
                            <select id="link-map-source" required>
                              <option value="" selected disabled>Select source worktree</option>
                            </select>
                          </label>
                          <label>Target worktree
                            <input id="link-map-target" autocomplete="off" placeholder="D:\worktrees\feature\abp" required>
                          </label>
                        </div>
                        <div class="form-actions"><button class="primary" data-action="link-map" type="submit">Save worktree map</button></div>
                      </form>
                    </section>

                    <section class="surface">
                      <div class="section-header"><h2>Repositories</h2></div>
                      <div class="table-wrap">
                        <table>
                          <thead><tr><th>Name</th><th>Root</th><th>Linked repos</th><th>Actions</th></tr></thead>
                          <tbody id="repos"></tbody>
                        </table>
                      </div>
                    </section>
                  </section>

                  <dialog id="context-description-dialog">
                    <div class="dialog-head">
                      <div><h2>Context description</h2><p id="context-dialog-path" class="section-note"></p></div>
                      <button type="button" onclick="closeContextDescription()" aria-label="Close">×</button>
                    </div>
                    <form class="dialog-form" onsubmit="saveContextDescription(event)">
                      <input id="context-dialog-worktree" type="hidden">
                      <label>Short task label
                        <textarea id="context-dialog-description" maxlength="160" placeholder="Review driver code" required></textarea>
                      </label>
                      <p class="section-note">Shown in the Console, browser extension, and <code>X-DevWT-Description</code> response header.</p>
                      <div class="dialog-actions">
                        <button id="clear-context-description" class="danger" data-action="describe-context" type="button" onclick="clearContextDescription()">Clear label</button>
                        <button type="button" onclick="closeContextDescription()">Cancel</button>
                        <button class="primary" data-action="describe-context" type="submit">Save label</button>
                      </div>
                    </form>
                  </dialog>
                </main>
              </div>
              <script>
                let lastStatus = null;
                let contextFilter = 'all';
                let fallbackRefreshTimer = null;
                let socketReconnectTimer = null;
                let messageTimer = null;
                const activityViewNames = ['grouped', 'timeline'];
                let activityView = 'grouped';
                let selectedRoutingPort = null;
                let selectedActivityImageKey = '';
                let selectedActivityProcessKey = '';
                let selectedActivitySessionKey = '';
                let linkedRepositoryInputIndex = 0;
                const tabNames = ['overview', 'contexts', 'routing', 'activity', 'tools', 'settings'];
                let activeTab = 'overview';
                try {
                  const storedTab = localStorage.getItem('devwt.activeTab');
                  if (tabNames.includes(storedTab)) activeTab = storedTab;
                  const storedActivityView = localStorage.getItem('devwt.activityView');
                  if (activityViewNames.includes(storedActivityView)) activityView = storedActivityView;
                } catch { }
                const signalRRecordSeparator = String.fromCharCode(30);
                const html = value => String(value ?? '').replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
                const jsArg = value => JSON.stringify(String(value ?? '')).replace(/</g, '\\u003c');
                function applyStatus(status, options = {}) {
                  lastStatus = status;
                  if (!options.preserveMessage) setMessage('');
                  render(lastStatus);
                }
                async function refresh(options = {}) {
                  try {
                    const response = await fetch('/api/status');
                    if (!response.ok) throw new Error(`HTTP ${response.status}`);
                    applyStatus(await response.json(), options);
                  } catch (error) {
                    setMessage(`Status unavailable: ${error.message}`, true);
                  }
                }
                function connectStatusSocket() {
                  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
                  const socket = new WebSocket(`${scheme}://${location.host}/hubs/status`);
                  let buffer = '';
                  socket.onopen = () => {
                    socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + signalRRecordSeparator);
                    stopFallbackRefresh();
                    setMessage('');
                  };
                  socket.onmessage = event => {
                    buffer += String(event.data || '');
                    let index = buffer.indexOf(signalRRecordSeparator);
                    while (index >= 0) {
                      const frame = buffer.slice(0, index);
                      buffer = buffer.slice(index + 1);
                      if (frame) handleStatusFrame(frame);
                      index = buffer.indexOf(signalRRecordSeparator);
                    }
                  };
                  socket.onerror = () => socket.close();
                  socket.onclose = () => {
                    startFallbackRefresh();
                    scheduleStatusSocketReconnect();
                  };
                }
                function handleStatusFrame(frame) {
                  try {
                    const message = JSON.parse(frame);
                    if (message.type === 1 && message.target === 'status' && message.arguments && message.arguments[0]) {
                      applyStatus(message.arguments[0]);
                    }
                  } catch (error) {
                    setMessage(`Realtime status parse failed: ${error.message}`, true);
                  }
                }
                function scheduleStatusSocketReconnect() {
                  if (socketReconnectTimer) return;
                  socketReconnectTimer = setTimeout(() => {
                    socketReconnectTimer = null;
                    connectStatusSocket();
                  }, 1500);
                }
                function startFallbackRefresh() {
                  if (fallbackRefreshTimer) return;
                  refresh({ preserveMessage: true });
                  fallbackRefreshTimer = setInterval(() => refresh({ preserveMessage: true }), 5000);
                }
                function stopFallbackRefresh() {
                  if (!fallbackRefreshTimer) return;
                  clearInterval(fallbackRefreshTimer);
                  fallbackRefreshTimer = null;
                }
                function setMessage(text, isError = false) {
                  const node = document.getElementById('message');
                  node.textContent = text || '';
                  node.classList.toggle('has-message', Boolean(text));
                  node.classList.toggle('is-error', Boolean(text) && isError);
                  if (messageTimer) clearTimeout(messageTimer);
                  if (text) {
                    messageTimer = setTimeout(() => {
                      node.classList.remove('has-message', 'is-error');
                      messageTimer = null;
                    }, isError ? 8000 : 4500);
                  }
                }
                function rerender() {
                  if (lastStatus) renderActivePanel(lastStatus, uniqueRoutes(lastStatus));
                }
                function renderContextsView() {
                  if (!lastStatus) return;
                  const routes = uniqueRoutes(lastStatus);
                  renderContexts(lastStatus.contexts, routes, lastStatus.routing);
                  updateFilterButtons();
                }
                function renderActivityView() {
                  if (!lastStatus) return;
                  renderActivity(lastStatus.connectionHistory || [], uniqueRoutes(lastStatus), lastStatus);
                }
                function setActivityView(name) {
                  activityView = activityViewNames.includes(name) ? name : 'grouped';
                  document.getElementById('activity-view-grouped').classList.toggle('selected', activityView === 'grouped');
                  document.getElementById('activity-view-timeline').classList.toggle('selected', activityView === 'timeline');
                  document.getElementById('activity-grouped').hidden = activityView !== 'grouped';
                  document.getElementById('activity-timeline').hidden = activityView !== 'timeline';
                  try { localStorage.setItem('devwt.activityView', activityView); } catch { }
                }
                function selectTab(name) {
                  if (!tabNames.includes(name)) name = 'overview';
                  activeTab = name;
                  document.querySelectorAll('[role="tab"][data-tab]').forEach(tab => {
                    const selected = tab.dataset.tab === name;
                    tab.setAttribute('aria-selected', selected ? 'true' : 'false');
                    tab.tabIndex = selected ? 0 : -1;
                  });
                  for (const tabName of tabNames) {
                    document.getElementById(`panel-${tabName}`).hidden = tabName !== name;
                  }
                  try { localStorage.setItem('devwt.activeTab', name); } catch { }
                  if (lastStatus) renderActivePanel(lastStatus, uniqueRoutes(lastStatus));
                }
                function handleTabKey(event) {
                  if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
                  event.preventDefault();
                  const direction = event.key === 'ArrowRight' ? 1 : -1;
                  const nextIndex = (tabNames.indexOf(activeTab) + direction + tabNames.length) % tabNames.length;
                  selectTab(tabNames[nextIndex]);
                  document.querySelector(`[role="tab"][data-tab="${tabNames[nextIndex]}"]`)?.focus();
                }
                function render(status) {
                  const routes = uniqueRoutes(status);
                  renderStatusStrip(status, routes);
                  renderActivePanel(status, routes);
                }
                function renderActivePanel(status, routes) {
                  if (activeTab === 'overview') {
                    renderOverview(status, routes);
                    return;
                  }
                  if (activeTab === 'routing') {
                    renderRouting(status, routes);
                    return;
                  }
                  if (activeTab === 'contexts') {
                    renderContexts(status.contexts, routes, status.routing);
                    updateFilterButtons();
                    return;
                  }
                  if (activeTab === 'activity') {
                    renderActivity(status.connectionHistory || [], routes, status);
                    return;
                  }
                  if (activeTab === 'tools') {
                    renderTools(status, routes);
                    return;
                  }
                  renderRuntimeBackends();
                  renderBrowserFallbackOnMissingPort(status.runtimeSettings?.browserFallbackOnMissingPort === true);
                  renderIdeWatches(status.runtimeSettings?.ideWatches || []);
                  renderSessionRules(status.runtimeSettings?.sessionRules || []);
                  renderRepositories(status.repositories);
                  renderSettingsOptions(status);
                }
                function renderStatusStrip(status, routes) {
                  const activeContexts = status.contexts.filter(context => context.status === 'Active').length;
                  const pausedContexts = status.contexts.length - activeContexts;
                  const ports = [...new Set(routes.map(route => Number(route.port)))].length;
                  const mode = status.routing.activeTargetMode === 'GlobalContext' ? 'One context' : 'Per port';
                  document.getElementById('status-strip').innerHTML = `
                    <span><strong>${html(status.repositories.length)}</strong> repositories</span>
                    <span><strong>${html(activeContexts)}</strong> active contexts</span>
                    <span><strong>${html(pausedContexts)}</strong> paused</span>
                    <span><strong>${html(ports)}</strong> open ports</span>
                    <span><strong>${html(mode)}</strong> routing</span>
                    <span class="live">Live</span><span>SignalR status stream</span>
                  `;
                  document.getElementById('nav-status').innerHTML = `
                    <strong><span class="live-dot"></span>Console connected</strong>
                    <span>${html(activeContexts)} active contexts · ${html(ports)} observed ports</span>
                  `;
                }
                function renderOverview(status, routes) {
                  const activeContexts = status.contexts.filter(context => context.status === 'Active');
                  const pausedContexts = status.contexts.filter(context => context.status !== 'Active');
                  const ports = [...new Set(routes.map(route => Number(route.port)))];
                  const recentRequests = (status.connectionHistory || []).length;
                  document.getElementById('overview-metrics').innerHTML = `
                    <article class="metric-card"><span class="metric-label">Repositories</span><strong class="metric-value">${html(status.repositories.length)}</strong><span class="metric-detail">${html(status.contexts.length)} discovered contexts</span></article>
                    <article class="metric-card"><span class="metric-label">Active contexts</span><strong class="metric-value">${html(activeContexts.length)}</strong><span class="metric-detail">${html(pausedContexts.length)} paused</span></article>
                    <article class="metric-card"><span class="metric-label">Observed ports</span><strong class="metric-value">${html(ports.length)}</strong><span class="metric-detail">${ports.length ? html(ports.slice(0, 4).map(port => `:${port}`).join(', ')) : 'No listeners yet'}</span></article>
                    <article class="metric-card"><span class="metric-label">Recent decisions</span><strong class="metric-value">${html(recentRequests)}</strong><span class="metric-detail">${html(status.routing.activeTargetMode === 'GlobalContext' ? 'One-context routing' : 'Per-port routing')}</span></article>
                  `;
                  const ranked = [...status.contexts]
                    .sort((left, right) => {
                      const leftRoutes = routes.filter(route => route.contextId === left.id).length;
                      const rightRoutes = routes.filter(route => route.contextId === right.id).length;
                      return rightRoutes - leftRoutes || String(left.name).localeCompare(String(right.name));
                    })
                    .slice(0, 7);
                  document.getElementById('overview-contexts').innerHTML = ranked.length
                    ? ranked.map(context => {
                        const contextRoutes = routes.filter(route => route.contextId === context.id);
                        const portCount = new Set(contextRoutes.map(route => Number(route.port))).size;
                        return `<div class="overview-context-row">
                          <div><strong>${html(contextDisplayName(context))}</strong><small title="${html(context.worktreeRootPath)}">${html(context.worktreeRootPath)}</small></div>
                          <div class="row-actions">${statusBadge(context.status)}<span class="port-pill">${html(portCount)} port${portCount === 1 ? '' : 's'}</span></div>
                        </div>`;
                      }).join('')
                    : '<div class="empty-state">No contexts registered. Register a repository to get started.</div>';
                }
                function renderRuntimeBackends() {
                  document.getElementById('runtime-backends').innerHTML = `
                    <div class="settings-row">
                      <strong>Hook-core runtime</strong>
                      <span class="muted">devwt run, runtime shims, IDE integration, and gateway routing</span>
                      <span class="status-pill active">Enabled</span>
                    </div>
                  `;
                }
                function renderIdeWatches(watches) {
                  document.getElementById('ide-watches').innerHTML = watches.length
                    ? watches.map(watch => {
                        const selector = watch.imagePath
                          ? watch.imagePath
                          : (watch.appId ? `App ID · ${watch.appId}` : `Package family · ${watch.packageFamilyName || ''}`);
                        return `<div class="settings-row">
                          <strong>${html(watch.name)}</strong>
                          <code title="${html(selector)}">${html(selector)}</code>
                          <button class="danger" data-action="remove-ide-watch" onclick='removeIdeWatch(${jsArg(watch.name)})'>Remove</button>
                        </div>`;
                      }).join('')
                    : '<div class="empty-state">No IDE watches configured.</div>';
                }
                function renderSettingsOptions(status) {
                  const source = document.getElementById('link-map-source');
                  const selectedSource = source.value;
                  source.innerHTML = `<option value="" disabled ${selectedSource ? '' : 'selected'}>Select source worktree</option>
                    ${status.contexts.map(context => `<option value="${html(context.worktreeRootPath)}" ${context.worktreeRootPath === selectedSource ? 'selected' : ''}>${html(contextDisplayName(context))} · ${html(context.worktreeRootPath)}</option>`).join('')}`;
                }
                function renderBrowserFallbackOnMissingPort(enabled) {
                  document.getElementById('browser-fallback-off').classList.toggle('selected', !enabled);
                  document.getElementById('browser-fallback-on').classList.toggle('selected', enabled);
                  document.getElementById('browser-fallback-state').textContent = enabled
                    ? 'Default: decide automatically'
                    : 'Default: fail closed';
                }
                function renderSessionRules(rules) {
                  document.getElementById('session-rules').innerHTML = rules.length ? rules.map(rule => `
                    <tr>
                      <td><strong>${html(rule.name)}</strong></td>
                      <td>${html(describeSessionMatch(rule.match))}</td>
                      <td><span class="port-pill">${html(describeSessionIdentity(rule.identity))}</span></td>
                      <td><code>${html(rule.identity?.prefix || '-')}</code></td>
                      <td><button class="danger" data-action="remove-session-rule" onclick='removeSessionRule(${jsArg(rule.name)})'>Remove</button></td>
                    </tr>
                  `).join('') : '<tr><td colspan="5"><div class="empty-state">No session rules configured.</div></td></tr>';
                }
                function describeSessionMatch(match) {
                  if (!match) return 'match unavailable';
                  if (match.environmentVariable) return `env ${match.environmentVariable}`;
                  if (match.processName) return `process ${match.processName}`;
                  if (match.imagePathContains) return `image path contains ${match.imagePathContains}`;
                  if (match.commandLineContains) return `command line contains ${match.commandLineContains}`;
                  return 'match unavailable';
                }
                function describeSessionIdentity(identity) {
                  if (!identity) return 'identity unavailable';
                  if (identity.kind === 'EnvironmentVariable') return `env ${identity.value || ''}`.trim();
                  if (identity.kind === 'RootProcess') return 'root process';
                  if (identity.kind === 'Process') return 'process';
                  if (identity.kind === 'CommandLineRegex') return `regex ${identity.value || ''}`.trim();
                  return identity.kind || 'identity unavailable';
                }
                function renderRepositories(repositories) {
                  document.getElementById('repos').innerHTML = repositories.length ? repositories.map(repo => `
                    <tr>
                      <td><span class="context-name">${html(repo.name)}</span><br><span class="muted">${html(repo.id)}</span></td>
                      <td><code>${html(repo.rootPath)}</code></td>
                      <td>${repo.linkedRepositories.map(link => `${html(link.name)}: <code>${html(link.path)}</code>`).join('<br>') || '<span class="muted">none</span>'}</td>
                      <td><div class="row-actions">
                        <button data-action="pause-repository" onclick='pauseRepository(${jsArg(repo.id)})'>Pause</button>
                        <button data-action="resume-repository" onclick='resumeRepository(${jsArg(repo.id)})'>Resume</button>
                        <button class="danger" data-action="remove-repo" onclick='removeRepo(${jsArg(repo.id)}, ${jsArg(repo.name)})'>Remove</button>
                      </div></td>
                    </tr>`).join('') : '<tr><td colspan="4"><div class="empty-state">No repositories registered yet. Run <code>devwt add</code> in a git repository.</div></td></tr>';
                }
                function renderContexts(contexts, routes, routing) {
                  const visibleContexts = filterContexts(contexts, routes);
                  document.getElementById('contexts').innerHTML = visibleContexts.length ? visibleContexts.map(context => {
                    const contextRoutes = routes.filter(route => route.contextId === context.id);
                    return `<tr>
                      <td><span class="context-name">${html(contextDisplayName(context))}</span><br><span class="muted">${html(context.description ? `${context.name} · ${context.id}` : context.id)}</span></td>
                      <td>${statusBadge(context.status)}</td>
                      <td><code>${html(context.assignedIp)}</code></td>
                      <td><code>${html(context.runtimeName)}</code></td>
                      <td><code class="path-cell" title="${html(context.worktreeRootPath)}">${html(context.worktreeRootPath)}</code></td>
                      <td>${renderPortChips(contextRoutes, routing)}</td>
                      <td><div class="row-actions">
                        <button class="description-button" data-action="describe-context" onclick='openContextDescription(${jsArg(context.worktreeRootPath)}, ${jsArg(context.description || '')})'>${context.description ? 'Edit label' : 'Add label'}</button>
                        <button data-action="pause" onclick='pauseContext(${jsArg(context.worktreeRootPath)})'>Pause</button>
                        <button data-action="resume" onclick='resumeContext(${jsArg(context.worktreeRootPath)})'>Resume</button>
                      </div></td>
                    </tr>`;
                  }).join('') : '<tr><td colspan="7"><div class="empty-state">No contexts match the current filter.</div></td></tr>';
                }
                function filterContexts(contexts, routes) {
                  const search = document.getElementById('context-search')?.value.trim().toLowerCase() || '';
                  return contexts.filter(context => {
                    const contextRoutes = routes.filter(route => route.contextId === context.id);
                    const matchesFilter =
                      contextFilter === 'all'
                      || (contextFilter === 'active' && context.status === 'Active')
                      || (contextFilter === 'paused' && context.status !== 'Active')
                      || (contextFilter === 'ports' && contextRoutes.length > 0);
                    if (!matchesFilter) return false;
                    if (!search) return true;
                    const haystack = [
                      context.description,
                      context.name,
                      context.id,
                      context.gitRef,
                      context.status,
                      context.assignedIp,
                      context.runtimeName,
                      context.worktreeRootPath,
                      ...contextRoutes.map(route => `${endpointLabel(route)} ${route.protocol || 'Tcp'} ${route.targetIp}:${route.targetPort} ${route.listenerProcessId}`)
                    ].join(' ').toLowerCase();
                    return haystack.includes(search);
                  });
                }
                function setContextFilter(value) {
                  contextFilter = value;
                  renderContextsView();
                }
                function updateFilterButtons() {
                  ['all', 'active', 'paused', 'ports'].forEach(value => {
                    document.getElementById(`filter-${value}`).classList.toggle('selected', contextFilter === value);
                  });
                }
                function renderTools(status, routes) {
                  const select = document.getElementById('diagnostic-context');
                  const selectedContext = select.value;
                  select.innerHTML = `<option value="" disabled ${selectedContext ? '' : 'selected'}>Select a context</option>
                    ${status.contexts.map(context => `<option value="${html(context.id)}" data-worktree="${html(context.worktreeRootPath)}" ${context.id === selectedContext ? 'selected' : ''}>${html(contextDisplayName(context))}</option>`).join('')}`;
                  document.getElementById('listener-list').innerHTML = routes.length
                    ? routes.map(route => {
                        const context = status.contexts.find(item => item.id === route.contextId);
                        const protocol = String(route.protocol || 'Tcp').toLowerCase();
                        return `<div class="listener-row">
                          <div><strong>${html(context ? contextDisplayName(context) : route.contextId)}</strong><br><span class="muted">${html(protocol.toUpperCase())} ${html(endpointLabel(route))}</span></div>
                          <div><code>${html(route.targetIp)}:${html(route.targetPort)}</code> <span class="muted">PID ${html(route.listenerProcessId)}</span></div>
                          <div class="row-actions">
                            <button data-action="stop-proxy-child" onclick='stopListener(${jsArg(route.contextId)}, ${Number(route.port)}, ${jsArg(protocol)}, false)'>Stop</button>
                            <button class="danger" data-action="kill-proxy-child" onclick='stopListener(${jsArg(route.contextId)}, ${Number(route.port)}, ${jsArg(protocol)}, true)'>Force kill</button>
                          </div>
                        </div>`;
                      }).join('')
                    : '<div class="empty-state">No DevWT backend listeners are currently observed.</div>';
                }
                function statusBadge(status) {
                  const active = status === 'Active';
                  return `<span class="status-pill ${active ? 'active' : 'paused'}">${html(status)}</span>`;
                }
                function uniqueRoutes(status) {
                  const seen = new Set();
                  return (status.routes || [])
                    .filter(route => {
                      const key = `${route.contextId}:${route.protocol || 'Tcp'}:${route.listenIp || '127.0.0.1'}:${route.port}`;
                      if (seen.has(key)) return false;
                      seen.add(key);
                      return true;
                    })
                    .sort((a, b) => String(a.contextId).localeCompare(String(b.contextId)) || String(a.listenIp || '').localeCompare(String(b.listenIp || '')) || a.port - b.port);
                }
                function endpointLabel(route) {
                  return `${route.listenIp || '127.0.0.1'}:${route.port}`;
                }
                function endpointUrlHost(route) {
                  const host = route.listenIp || '127.0.0.1';
                  return host === '0.0.0.0' || host === '::' ? 'localhost' : host;
                }
                function renderPortChips(routes, routing) {
                  if (!routes.length) {
                    return '<span class="muted">No open ports</span>';
                  }

                  return `<div class="compact-port-list">${routes.map(route => {
                    const configuredContextId = routing.activeTargetMode === 'GlobalContext'
                      ? routing.globalActiveContextId
                      : (routing.portActiveTargets || []).find(target => Number(target.port) === Number(route.port))?.contextId;
                    const active = configuredContextId === route.contextId;
                    return `<button class="${active ? 'active' : ''}" data-action="set-active-target" title="${active ? 'Active target' : 'Set as target'} for ${html(endpointLabel(route))}" onclick='setActivePort(${jsArg(route.contextId)}, ${Number(route.port)})'>
                      ${html(route.protocol || 'Tcp')} :${html(route.port)}${active ? ' active' : ''}
                    </button>`;
                  }).join('')}</div>`;
                }
                function connectionMethods(route) {
                  const host = endpointUrlHost(route);
                  if ((route.protocol || 'Tcp') === 'Udp') {
                    return `<div class="row-actions"><span>UDP <code>${html(endpointLabel(route))}</code></span></div>`;
                  }
                  return `<div class="row-actions">
                    <a href="http://${html(host)}:${html(route.port)}/" target="_blank" rel="noreferrer">HTTP</a>
                    <a href="https://${html(host)}:${html(route.port)}/" target="_blank" rel="noreferrer">HTTPS</a>
                    <span>TCP <code>${html(endpointLabel(route))}</code></span>
                  </div>`;
                }
                function renderRouting(status, routes) {
                  const routing = status.routing || {};
                  const globalMode = routing.activeTargetMode === 'GlobalContext';
                  document.getElementById('routing-mode-global').classList.toggle('selected', globalMode);
                  document.getElementById('routing-mode-per-port').classList.toggle('selected', !globalMode);
                  document.getElementById('routing-mode-state').textContent = globalMode
                    ? 'One context for every port'
                    : `${(routing.portActiveTargets || []).length} configured port${(routing.portActiveTargets || []).length === 1 ? '' : 's'}`;

                  const globalPanel = document.getElementById('global-routing');
                  const portPanel = document.getElementById('port-routing-groups');
                  globalPanel.hidden = !globalMode;
                  portPanel.hidden = globalMode;

                  const globalContextId = routing.globalActiveContextId || '';
                  const globalSelect = document.getElementById('global-context-select');
                  globalSelect.innerHTML = `
                    <option value="">Select context</option>
                    ${status.contexts.map(context => `<option value="${html(context.id)}" ${context.id === globalContextId ? 'selected' : ''}>${html(contextDisplayName(context))}</option>`).join('')}
                  `;
                  const globalContext = status.contexts.find(context => context.id === globalContextId);
                  const globalRoutes = routes.filter(route => route.contextId === globalContextId);
                  document.getElementById('global-routing-summary').innerHTML = globalContext
                    ? `<strong>${html(contextDisplayName(globalContext))}</strong><br>
                       <code title="${html(globalContext.worktreeRootPath)}">${html(globalContext.worktreeRootPath)}</code><br>
                       <span class="muted">${html([...new Set(globalRoutes.map(route => route.port))].length)} available ports</span>
                       <div class="row-actions"><button data-action="clear-active-target" onclick="clearConfiguredTarget()">Clear</button></div>`
                    : '<strong>No context selected</strong>';

                  const groups = groupRoutesByPort(routes);
                  if (!groups.some(group => group.port === selectedRoutingPort)) {
                    selectedRoutingPort = groups[0]?.port ?? null;
                  }
                  const selectedGroup = groups.find(group => group.port === selectedRoutingPort);
                  portPanel.innerHTML = groups.length
                    ? `<aside class="routing-port-nav">
                         <div class="pane-heading"><span>Ports</span><strong>${html(groups.length)}</strong></div>
                         <div class="routing-port-list">${groups.map(group => renderRoutingPortButton(status, group, routing)).join('')}</div>
                       </aside>
                       ${selectedGroup ? renderPortGroup(status, selectedGroup, routing) : ''}`
                    : '<div class="empty-state">No open ports observed.</div>';
                }
                function selectRoutingPort(port) {
                  selectedRoutingPort = Number(port);
                  if (lastStatus) renderRouting(lastStatus, uniqueRoutes(lastStatus));
                }
                function renderRoutingPortButton(status, group, routing) {
                  const portTarget = (routing.portActiveTargets || []).find(target => Number(target.port) === group.port);
                  const contextIds = [...new Set(group.routes.map(route => route.contextId))];
                  const targetLabel = portTarget
                    ? contextName(status, portTarget.contextId)
                    : (contextIds.length === 1 ? `${contextName(status, contextIds[0])} (only target)` : 'Automatic');
                  return `<button class="routing-port-button ${selectedRoutingPort === group.port ? 'selected' : ''}" onclick="selectRoutingPort(${group.port})">
                    <strong>:${html(group.port)}</strong>
                    <span>${group.protocols.map(protocol => html(protocol)).join(' / ')}</span>
                    <small>${html(group.endpoints.length)} endpoint${group.endpoints.length === 1 ? '' : 's'} / ${html(contextIds.length)} context${contextIds.length === 1 ? '' : 's'}</small>
                    <span class="route-target">${html(targetLabel)}</span>
                  </button>`;
                }
                function renderPortGroup(status, group, routing) {
                  const portTarget = (routing.portActiveTargets || []).find(target => Number(target.port) === group.port);
                  const contextIds = [...new Set(group.routes.map(route => route.contextId))];
                  const tcpIps = [...new Set(group.routes
                    .filter(route => (route.protocol || 'Tcp') === 'Tcp')
                    .map(route => route.listenIp || '127.0.0.1'))].sort();
                  return `<section class="routing-detail" aria-label="Port ${html(group.port)} routing">
                    <div class="routing-detail-head">
                      <div>
                        <h3>Port ${html(group.port)}</h3>
                        <span class="muted">${html(group.endpoints.join(', '))}</span>
                      </div>
                      <div class="routing-detail-controls">
                        <label>Target context
                          <select aria-label="Target context for port ${html(group.port)}" onchange="this.value ? setActivePort(this.value, ${group.port}) : clearActivePort(${group.port})">
                            <option value="">Automatic</option>
                            ${contextIds.map(contextId => {
                              const context = status.contexts.find(item => item.id === contextId);
                              return `<option value="${html(contextId)}" ${portTarget?.contextId === contextId ? 'selected' : ''}>${html(context ? contextDisplayName(context) : contextId)}</option>`;
                            }).join('')}
                          </select>
                        </label>
                        ${portTarget ? `<button data-action="clear-active-target" onclick="clearActivePort(${group.port})">Clear</button>` : ''}
                      </div>
                    </div>
                    <div class="protocol-controls">
                      ${group.protocols.map(protocol => `<span class="port-pill">${html(protocol)}</span>`).join('')}
                      ${tcpIps.map(ip => {
                        const mode = httpsProxyModeFor(routing, ip, group.port);
                        return `<label class="compact-field">TCP ${html(ip)}
                          <select aria-label="TCP handling mode for ${html(ip)}:${html(group.port)}" onchange='setHttpsProxyMode(${jsArg(ip)}, ${group.port}, this.value)'>
                            <option value="auto" ${mode === 'Auto' ? 'selected' : ''}>Auto</option>
                            <option value="inspect" ${mode === 'Inspect' ? 'selected' : ''}>HTTP Inspect</option>
                            <option value="tunnel" ${mode === 'Tunnel' ? 'selected' : ''}>TLS Tunnel</option>
                            <option value="raw" ${mode === 'Raw' ? 'selected' : ''}>Raw</option>
                          </select>
                        </label>`;
                      }).join('')}
                    </div>
                    <div class="route-context-head"><span>Context</span><span>Listener to backend</span><span>Open</span></div>
                    <div class="port-route-list">
                      ${contextIds.map(contextId => renderPortRouteRow(status, group.routes.filter(route => route.contextId === contextId), portTarget)).join('')}
                    </div>
                  </section>`;
                }
                function httpsProxyModeFor(routing, ip, port) {
                  return (routing.httpsProxyEndpoints || []).find(endpoint =>
                    String(endpoint.ip).toLowerCase() === String(ip).toLowerCase()
                    && Number(endpoint.port) === Number(port))?.mode || 'Auto';
                }
                function renderPortRouteRow(status, routes, portTarget) {
                  const route = routes.find(item => (item.protocol || 'Tcp') === 'Tcp') || routes[0];
                  const context = status.contexts.find(item => item.id === route.contextId);
                  const active = portTarget?.contextId === route.contextId;
                  return `<div class="port-route-row ${active ? 'active' : ''}">
                    <div>
                      <span class="context-name">${html(context ? contextDisplayName(context) : route.contextId)}</span><br>
                      <span class="muted">PID ${html(route.listenerProcessId)}</span>
                    </div>
                    <div>
                      ${routes.map(item => `<code>${html(item.protocol || 'Tcp')} ${html(endpointLabel(item))} to ${html(item.targetIp)}:${html(item.targetPort)}</code>`).join('<br>')}
                    </div>
                    ${connectionMethods(route)}
                  </div>`;
                }
                function renderActivity(history, routes, status) {
                  const filtered = filterActivityHistory(history).slice(0, 200);
                  const groups = groupActivityHistory(filtered);
                  syncActivitySelection(groups);
                  renderGroupedActivity(groups, routes, status);
                  renderTimelineActivity(filtered);
                  setActivityView(activityView);
                }
                function filterActivityHistory(history) {
                  const search = document.getElementById('activity-search')?.value.trim().toLowerCase() || '';
                  const reason = document.getElementById('activity-reason-filter')?.value || '';
                  return history.filter(entry => {
                    if (reason && entry.routeReason !== reason) return false;
                    if (!search) return true;
                    return [
                      processLabel(entry),
                      entry.processId,
                      entry.processImagePath,
                      entry.applicationKey,
                      entry.listenIp,
                      entry.port,
                      entry.clientEndPoint,
                      entry.targetIp,
                      entry.targetPort,
                      entry.contextName,
                      entry.contextId,
                      entry.routeReason,
                      entry.sessionId
                    ].join(' ').toLowerCase().includes(search);
                  });
                }
                function groupActivityHistory(history) {
                  const images = new Map();
                  for (const entry of history) {
                    const imageValue = entry.applicationKey || entry.processImagePath || 'Unknown image';
                    const imageKey = String(imageValue).toLowerCase();
                    if (!images.has(imageKey)) {
                      images.set(imageKey, { key: imageKey, value: imageValue, entries: [], processes: new Map() });
                    }

                    const image = images.get(imageKey);
                    image.entries.push(entry);
                    const processId = Number(entry.processId) > 0 ? Number(entry.processId) : null;
                    const processKey = processId ? String(processId) : 'unknown';
                    if (!image.processes.has(processKey)) {
                      image.processes.set(processKey, { key: processKey, processId, entries: [], sessions: new Map() });
                    }

                    const process = image.processes.get(processKey);
                    process.entries.push(entry);
                    const sessionId = entry.sessionId || null;
                    const sessionKey = sessionId ? String(sessionId).toLowerCase() : 'no-session';
                    if (!process.sessions.has(sessionKey)) {
                      process.sessions.set(sessionKey, { key: sessionKey, sessionId, entries: [] });
                    }
                    process.sessions.get(sessionKey).entries.push(entry);
                  }

                  return [...images.values()].map(image => ({
                    ...image,
                    processes: [...image.processes.values()].map(process => ({
                      ...process,
                      sessions: [...process.sessions.values()]
                    }))
                  }));
                }
                function syncActivitySelection(groups) {
                  let image = groups.find(item => item.key === selectedActivityImageKey);
                  if (!image) {
                    image = groups[0] || null;
                    selectedActivityImageKey = image?.key || '';
                  }
                  let process = image?.processes.find(item => item.key === selectedActivityProcessKey);
                  if (!process) {
                    process = image?.processes[0] || null;
                    selectedActivityProcessKey = process?.key || '';
                  }
                  let session = process?.sessions.find(item => item.key === selectedActivitySessionKey);
                  if (!session) {
                    session = process?.sessions[0] || null;
                    selectedActivitySessionKey = session?.key || '';
                  }
                }
                function selectActivityScope(scopeType, key) {
                  if (scopeType === 'image') {
                    selectedActivityImageKey = key;
                    selectedActivityProcessKey = '';
                    selectedActivitySessionKey = '';
                  } else if (scopeType === 'process') {
                    selectedActivityProcessKey = key;
                    selectedActivitySessionKey = '';
                  } else {
                    selectedActivitySessionKey = key;
                  }
                  renderActivityView();
                }
                function renderGroupedActivity(groups, routes, status) {
                  const container = document.getElementById('activity-grouped');
                  if (!groups.length) {
                    container.innerHTML = '<div class="empty-state">No matching gateway activity.</div>';
                    return;
                  }

                  const image = groups.find(item => item.key === selectedActivityImageKey) || groups[0];
                  const process = image.processes.find(item => item.key === selectedActivityProcessKey) || image.processes[0];
                  const session = process?.sessions.find(item => item.key === selectedActivitySessionKey) || process?.sessions[0];
                  container.innerHTML = `
                    <section class="activity-pane" aria-label="Applications">
                      <div class="pane-heading"><span>Images</span><strong>${html(groups.length)}</strong></div>
                      <div class="activity-list">${groups.map(item => renderActivityImageOption(item)).join('')}</div>
                    </section>
                    <section class="activity-pane" aria-label="Processes">
                      <div class="pane-heading"><span>Processes</span><strong>${html(image.processes.length)}</strong></div>
                      <div class="activity-list">${image.processes.map(item => renderActivityProcessOption(item, status)).join('')}</div>
                    </section>
                    <section class="activity-pane" aria-label="Sessions">
                      <div class="pane-heading"><span>Sessions</span><strong>${html(process?.sessions.length || 0)}</strong></div>
                      <div class="activity-list">${process ? process.sessions.map(item => renderActivitySessionOption(item)).join('') : ''}</div>
                    </section>
                    ${renderActivityInspector(status, routes, image, process, session)}
                  `;
                }
                function renderActivityImageOption(image) {
                  const latest = image.entries[0];
                  return `<button class="activity-option ${image.key === selectedActivityImageKey ? 'selected' : ''}" data-scope-key="${html(image.key)}" onclick='selectActivityScope("image", ${jsArg(image.key)})'>
                    <strong>${html(processLabel(latest))}</strong>
                    <small title="${html(image.value)}">${html(image.value)}</small>
                    <span class="activity-option-meta"><span>${html(image.processes.length)} processes</span><span>${html(image.entries.length)} requests</span></span>
                  </button>`;
                }
                function renderActivityProcessOption(process, status) {
                  return `<button class="activity-option ${process.key === selectedActivityProcessKey ? 'selected' : ''}" data-scope-key="${html(process.key)}" onclick='selectActivityScope("process", ${jsArg(process.key)})'>
                    <strong>${process.processId ? `PID ${html(process.processId)}` : 'Unknown process'}</strong>
                    <small>${process.processId ? html(scopeTargetSummary(status, 'process', process.processId)) : 'No process identity'}</small>
                    <span class="activity-option-meta"><span>${html(process.sessions.length)} sessions</span><span>${html(process.entries.length)} requests</span></span>
                  </button>`;
                }
                function renderActivitySessionOption(session) {
                  const latest = session.entries[0];
                  return `<button class="activity-option ${session.key === selectedActivitySessionKey ? 'selected' : ''}" data-scope-key="${html(session.key)}" onclick='selectActivityScope("session", ${jsArg(session.key)})'>
                    <strong title="${html(session.sessionId || '')}">${html(session.sessionId || 'No session identity')}</strong>
                    <small>${html(observedPortLabel(session.entries))}</small>
                    <span class="activity-option-meta"><span>${html(latest?.routeReason || '-')}</span><span>${html(session.entries.length)} requests</span></span>
                  </button>`;
                }
                function renderActivityInspector(status, routes, image, process, session) {
                  const entries = session?.entries || process?.entries || image.entries;
                  return `<section class="activity-inspector" aria-label="Caller routing details">
                    <div class="inspector-heading">
                      <div>
                        <strong>${html(session?.sessionId || (process?.processId ? `PID ${process.processId}` : processLabel(image.entries[0])))}</strong>
                        <code title="${html(image.value)}">${html(image.value)}</code>
                      </div>
                      <span class="port-pill">${html(requestCountLabel(entries.length))}</span>
                    </div>
                    <div class="scope-matrix-head"><span>Scope</span><span>Target context</span></div>
                    ${image.value === 'Unknown image' ? '' : renderScopeTargetEditor(status, routes, 'image', image.value, entries, 'Image')}
                    ${process?.processId ? renderScopeTargetEditor(status, routes, 'process', process.processId, entries, 'Process') : ''}
                    ${session?.sessionId ? renderScopeTargetEditor(status, routes, 'session', session.sessionId, entries, 'Session') : ''}
                    <div class="activity-log-head"><span>Time</span><span>Endpoint</span><span>Target</span><span>Reason</span></div>
                    <div class="activity-entries">${entries.slice(0, 100).map(renderGroupedActivityEntry).join('')}</div>
                  </section>`;
                }
                function renderGroupedActivityEntry(entry) {
                  return `<div class="activity-entry">
                    <span>${html(formatHistoryTime(entry.timestamp))}</span>
                    <span><code>${html(entry.protocol || 'Tcp')} ${html(entry.listenIp || '127.0.0.1')}:${html(entry.port)}</code><br><span class="muted">client ${html(entry.clientEndPoint || '-')}</span></span>
                    <span><strong>${html(entry.contextName || entry.contextId)}</strong><br><span class="muted">${html(entry.targetIp)}:${html(entry.targetPort)}</span></span>
                    <span class="port-pill">${html(entry.routeReason || '-')}</span>
                  </div>`;
                }
                function requestCountLabel(count) {
                  return `${count} request${count === 1 ? '' : 's'}`;
                }
                function observedPortLabel(entries) {
                  const ports = [...new Set(entries.map(entry => Number(entry.port)).filter(port => port > 0))].sort((a, b) => a - b);
                  return ports.length ? ports.map(port => `:${port}`).join(', ') : 'No observed ports';
                }
                function scopeTargetSummary(status, scopeType, scopeValue) {
                  const routing = status.routing || {};
                  const wideTarget = findScopeTarget(routing, scopeType, scopeValue);
                  const portCount = scopeType === 'process'
                    ? (routing.processPortTargets || []).filter(target => Number(target.processId) === Number(scopeValue)).length
                    : scopeType === 'image'
                      ? (routing.applicationTargets || []).filter(target => equalsIgnoreCase(target.applicationKey, scopeValue)).length
                      : (routing.sessionPortTargets || []).filter(target => equalsIgnoreCase(target.sessionId, scopeValue)).length;
                  const parts = [wideTarget ? contextName(status, wideTarget.contextId) : 'Automatic'];
                  if (portCount) parts.push(`${portCount} port override${portCount === 1 ? '' : 's'}`);
                  return parts.join(' / ');
                }
                function renderScopeTargetEditor(status, routes, scopeType, scopeValue, entries, scopeLabel) {
                  const routing = status.routing || {};
                  const wideTarget = findScopeTarget(routing, scopeType, scopeValue);
                  const allContextIds = status.contexts.filter(context => context.status === 'Active').map(context => context.id);
                  if (wideTarget?.contextId && !allContextIds.includes(wideTarget.contextId)) allContextIds.push(wideTarget.contextId);
                  const ports = [...new Set(entries.map(entry => Number(entry.port)).filter(port => port > 0))].sort((a, b) => a - b);
                  return `<div class="scope-target-editor">
                    <div class="scope-identity">
                      <strong>${html(scopeLabel)}</strong>
                      <code title="${html(scopeValue)}">${html(scopeValue)}</code>
                    </div>
                    <div class="scope-target-controls">
                      <div class="scope-target-row">
                        <span class="scope-port-label">All ports</span>
                        ${renderScopeContextSelect(status, scopeType, scopeValue, allContextIds, wideTarget?.contextId || '')}
                      </div>
                      ${ports.map(port => {
                         const target = findScopePortTarget(routing, scopeType, scopeValue, port);
                         const portEntries = entries.filter(entry => Number(entry.port) === port);
                         const protocols = [...new Set(portEntries.map(entry => entry.protocol || 'Tcp'))];
                        const contextIds = [...new Set(routes
                          .filter(route => portEntries.some(entry => historyRouteMatches(route, entry)))
                          .map(route => route.contextId))];
                         if (target?.contextId && !contextIds.includes(target.contextId)) contextIds.push(target.contextId);
                         return `<div class="scope-target-row">
                           <span class="scope-port-label">${html(protocols.join('/'))} :${html(port)}</span>
                           ${renderScopePortSelect(status, scopeType, scopeValue, port, contextIds, target?.contextId || '')}
                         </div>`;
                       }).join('')}
                    </div>
                  </div>`;
                }
                function findScopeTarget(routing, scopeType, scopeValue) {
                  if (scopeType === 'process') {
                    return (routing.processTargets || []).find(target => Number(target.processId) === Number(scopeValue));
                  }
                  if (scopeType === 'image') {
                    return (routing.applicationContextTargets || []).find(target => equalsIgnoreCase(target.applicationKey, scopeValue));
                  }
                  return (routing.sessionContextTargets || []).find(target => equalsIgnoreCase(target.sessionId, scopeValue));
                }
                function findScopePortTarget(routing, scopeType, scopeValue, port) {
                  if (scopeType === 'process') {
                    return (routing.processPortTargets || []).find(target => Number(target.processId) === Number(scopeValue) && Number(target.port) === Number(port));
                  }
                  if (scopeType === 'image') {
                    return (routing.applicationTargets || []).find(target => equalsIgnoreCase(target.applicationKey, scopeValue) && Number(target.port) === Number(port));
                  }
                  return (routing.sessionPortTargets || []).find(target => equalsIgnoreCase(target.sessionId, scopeValue) && Number(target.port) === Number(port));
                }
                function equalsIgnoreCase(left, right) {
                  return String(left || '').toLowerCase() === String(right || '').toLowerCase();
                }
                function contextDisplayName(context) {
                  return context?.description || context?.gitRef || context?.name || context?.id || '';
                }
                function contextName(status, contextId) {
                  const context = status.contexts.find(context => context.id === contextId);
                  return context ? contextDisplayName(context) : contextId;
                }
                function renderContextOptions(status, contextIds, selectedContextId) {
                  const options = contextIds
                    .map(contextId => status.contexts.find(context => context.id === contextId) || { id: contextId, name: contextId })
                    .sort((left, right) => contextDisplayName(left).localeCompare(contextDisplayName(right)));
                  return `<option value="">Automatic</option>${options.map(context =>
                    `<option value="${html(context.id)}" ${context.id === selectedContextId ? 'selected' : ''}>${html(contextDisplayName(context))}</option>`).join('')}`;
                }
                function renderScopeContextSelect(status, scopeType, scopeValue, contextIds, selectedContextId) {
                  const attributes = `data-scope-type="${html(scopeType)}" data-scope-value="${html(scopeValue)}" onchange="setScopeContextTarget(this.dataset.scopeType, this.dataset.scopeValue, this.value)" ${contextIds.length ? '' : 'disabled'}`;
                  const options = renderContextOptions(status, contextIds, selectedContextId);
                  if (scopeType === 'process') return `<select data-action="set-process-target" ${attributes}>${options}</select>`;
                  if (scopeType === 'image') return `<select data-action="set-image-context-target" ${attributes}>${options}</select>`;
                  return `<select data-action="set-session-context-target" ${attributes}>${options}</select>`;
                }
                function renderScopePortSelect(status, scopeType, scopeValue, port, contextIds, selectedContextId) {
                  const attributes = `data-scope-type="${html(scopeType)}" data-scope-value="${html(scopeValue)}" data-port="${html(port)}" onchange="setScopePortTarget(this.dataset.scopeType, this.dataset.scopeValue, Number(this.dataset.port), this.value)" ${contextIds.length ? '' : 'disabled'}`;
                  const options = renderContextOptions(status, contextIds, selectedContextId);
                  if (scopeType === 'process') return `<select data-action="set-process-port-target" ${attributes}>${options}</select>`;
                  if (scopeType === 'image') return `<select data-action="set-application-target" ${attributes}>${options}</select>`;
                  return `<select data-action="set-session-port-target" ${attributes}>${options}</select>`;
                }
                function renderTimelineActivity(history) {
                  const body = document.getElementById('connection-history');
                  body.innerHTML = history.length ? history.map(entry => `<tr>
                      <td>${html(formatHistoryTime(entry.timestamp))}</td>
                      <td>
                        <span class="context-name">${html(processLabel(entry))}</span><br>
                        <span class="muted">PID ${html(entry.processId || '-')}</span><br>
                        <code class="path-cell" title="${html(entry.applicationKey || entry.processImagePath || '-')}">${html(entry.applicationKey || entry.processImagePath || '-')}</code>
                      </td>
                      <td><code>${html(entry.sessionId || '-')}</code></td>
                      <td>
                        <code>${html(entry.protocol || 'Tcp')} ${html(entry.listenIp || '127.0.0.1')}:${html(entry.port)}</code><br>
                        <span class="muted">client ${html(entry.clientEndPoint || '-')}</span><br>
                        <span class="muted">backend ${html(entry.targetIp)}:${html(entry.targetPort)}</span>
                      </td>
                      <td><strong>${html(entry.contextName || entry.contextId)}</strong><br><span class="muted">${html(entry.contextId)}</span></td>
                      <td><span class="port-pill">${html(entry.routeReason || '-')}</span></td>
                    </tr>`).join('') : '<tr><td colspan="6"><div class="empty-state">No matching gateway activity.</div></td></tr>';
                }
                function historyRouteMatches(route, entry) {
                  return Number(route.port) === Number(entry.port)
                    && String(route.protocol || 'Tcp') === String(entry.protocol || 'Tcp')
                    && String(route.listenIp || '127.0.0.1') === String(entry.listenIp || '127.0.0.1');
                }
                function processLabel(entry) {
                  const image = entry.processImagePath || entry.applicationKey;
                  return image ? image.split(/[\\/]/).pop() : 'Unknown process';
                }
                function formatHistoryTime(value) {
                  if (!value) return '-';
                  const date = new Date(value);
                  return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleTimeString();
                }
                function groupRoutesByPort(routes) {
                  const groups = new Map();
                  for (const route of routes) {
                    const port = Number(route.port);
                    if (!groups.has(port)) {
                      groups.set(port, { port, routes: [], protocols: new Set(), endpoints: new Set() });
                    }

                    const group = groups.get(port);
                    group.routes.push(route);
                    group.protocols.add(route.protocol || 'Tcp');
                    group.endpoints.add(endpointLabel(route));
                  }

                  return [...groups.values()]
                    .map(group => ({
                      port: group.port,
                      routes: group.routes.sort((a, b) => String(a.contextId).localeCompare(String(b.contextId))),
                      protocols: [...group.protocols].sort(),
                      endpoints: [...group.endpoints].sort()
                    }))
                    .sort((a, b) => a.port - b.port);
                }
                async function action(payload) {
                  document.body.classList.add('is-busy');
                  try {
                    const response = await fetch('/api/action', {
                      method: 'POST',
                      headers: { 'content-type': 'application/json' },
                      body: JSON.stringify(payload)
                    });
                    if (!response.ok) throw new Error(`HTTP ${response.status}`);
                    const result = await response.json();
                    await refresh({ preserveMessage: true });
                    setMessage((result.output || 'Action completed').trim(), Number(result.exitCode) !== 0);
                    return result;
                  } catch (error) {
                    setMessage(`Action failed: ${error.message}`, true);
                    return { output: error.message, exitCode: 2 };
                  } finally {
                    document.body.classList.remove('is-busy');
                  }
                }
                function removeRepo(repositoryName, displayName) {
                  if (!confirm(`Remove repository "${displayName}" from DevWT? Its contexts and routing state will also be removed.`)) return;
                  action({ action: 'remove-repo', repositoryName });
                }
                function pauseContext(worktreePath) { action({ action: 'pause', worktreePath }); }
                function resumeContext(worktreePath) { action({ action: 'resume', worktreePath }); }
                function pauseRepository(repositoryName) { action({ action: 'pause-repository', repositoryName }); }
                function resumeRepository(repositoryName) { action({ action: 'resume-repository', repositoryName }); }
                function focusAddRepository() {
                  selectTab('settings');
                  requestAnimationFrame(() => document.getElementById('repository-path')?.focus());
                }
                function focusIdeWatch() {
                  selectTab('settings');
                  requestAnimationFrame(() => document.getElementById('ide-watch-name')?.focus());
                }
                async function addRepository(event) {
                  event.preventDefault();
                  const worktreePath = document.getElementById('repository-path').value.trim();
                  const repositoryName = document.getElementById('repository-name').value.trim();
                  const linkedRepositories = [...document.querySelectorAll('.linked-repo-input-row')].map(row => ({
                    name: row.querySelector('.linked-repository-name').value.trim(),
                    path: row.querySelector('.linked-repository-path').value.trim()
                  }));
                  const result = await action({
                    action: 'add-repository',
                    worktreePath,
                    repositoryName: repositoryName || null,
                    linkedRepositories
                  });
                  if (Number(result.exitCode) === 0) {
                    event.target.reset();
                    document.getElementById('linked-repository-inputs').innerHTML = '';
                  }
                }
                function addLinkedRepositoryInput() {
                  const index = ++linkedRepositoryInputIndex;
                  const row = document.createElement('div');
                  row.className = 'linked-repo-input-row';
                  row.dataset.index = String(index);
                  row.innerHTML = `
                    <label>Name
                      <input class="linked-repository-name" autocomplete="off" placeholder="abp" required>
                    </label>
                    <label>Repository path
                      <input class="linked-repository-path" autocomplete="off" placeholder="D:\\GitHub\\abp" required>
                    </label>
                    <button class="danger" type="button" aria-label="Remove linked repository" onclick="this.closest('.linked-repo-input-row').remove()">Remove</button>
                  `;
                  document.getElementById('linked-repository-inputs').appendChild(row);
                  row.querySelector('input').focus();
                }
                async function addIdeWatch(event) {
                  event.preventDefault();
                  const ideWatchName = document.getElementById('ide-watch-name').value.trim();
                  const ideWatchSelectorKind = document.getElementById('ide-watch-selector-kind').value;
                  const ideWatchSelectorValue = document.getElementById('ide-watch-selector-value').value.trim();
                  const result = await action({ action: 'add-ide-watch', ideWatchName, ideWatchSelectorKind, ideWatchSelectorValue });
                  if (Number(result.exitCode) === 0) event.target.reset();
                }
                function removeIdeWatch(ideWatchName) {
                  if (!confirm(`Remove the persistent IDE watch "${ideWatchName}"?`)) return;
                  action({ action: 'remove-ide-watch', ideWatchName });
                }
                async function addLinkMap(event) {
                  event.preventDefault();
                  const linkedRepositoryName = document.getElementById('link-map-repository').value.trim();
                  const sourceWorktreePath = document.getElementById('link-map-source').value;
                  const targetWorktreePath = document.getElementById('link-map-target').value.trim();
                  const result = await action({ action: 'link-map', linkedRepositoryName, sourceWorktreePath, targetWorktreePath });
                  if (Number(result.exitCode) === 0) event.target.reset();
                }
                async function runPortDiagnostic(event, operation) {
                  event.preventDefault();
                  const select = document.getElementById('diagnostic-context');
                  if (!select.reportValidity()) return;
                  const portInput = document.getElementById('diagnostic-port');
                  if (!portInput.reportValidity()) return;
                  const option = select.selectedOptions[0];
                  const payload = {
                    action: operation,
                    contextId: select.value,
                    worktreePath: option.dataset.worktree,
                    port: Number(portInput.value)
                  };
                  const output = document.getElementById('diagnostic-output');
                  output.textContent = 'Running diagnostic...';
                  const result = await action(payload);
                  output.textContent = (result.output || 'No output').trim();
                }
                async function copyCommand(command) {
                  try {
                    await navigator.clipboard.writeText(command);
                    setMessage('Command copied to the clipboard.');
                  } catch {
                    setMessage('Clipboard access was blocked by the browser.', true);
                  }
                }
                function openContextDescription(worktreePath, description) {
                  document.getElementById('context-dialog-worktree').value = worktreePath;
                  document.getElementById('context-dialog-description').value = description;
                  document.getElementById('context-dialog-path').textContent = worktreePath;
                  document.getElementById('clear-context-description').hidden = !description;
                  document.getElementById('context-description-dialog').showModal();
                  requestAnimationFrame(() => document.getElementById('context-dialog-description').focus());
                }
                function closeContextDescription() {
                  document.getElementById('context-description-dialog').close();
                }
                async function saveContextDescription(event) {
                  event.preventDefault();
                  const worktreePath = document.getElementById('context-dialog-worktree').value;
                  const contextDescription = document.getElementById('context-dialog-description').value.trim();
                  const result = await action({ action: 'describe-context', worktreePath, contextDescription });
                  if (Number(result.exitCode) === 0) closeContextDescription();
                }
                async function clearContextDescription() {
                  const worktreePath = document.getElementById('context-dialog-worktree').value;
                  const result = await action({ action: 'describe-context', worktreePath, clearContextDescription: true });
                  if (Number(result.exitCode) === 0) closeContextDescription();
                }
                function stopListener(contextId, port, protocol, force) {
                  const verb = force ? 'Force kill' : 'Stop';
                  const warning = force
                    ? `${verb} the backend process for ${contextId} on ${protocol.toUpperCase()} port ${port}? Unsaved application work can be lost.`
                    : `${verb} the backend process for ${contextId} on ${protocol.toUpperCase()} port ${port}?`;
                  if (!confirm(warning)) return;
                  action({ action: force ? 'kill-proxy-child' : 'stop-proxy-child', contextId, port: Number(port), protocol });
                }
                function setRoutingMode(activeTargetMode) {
                  action({ action: 'set-active-target-mode', activeTargetMode });
                }
                function setGlobalContext(contextId) {
                  if (!contextId) return clearConfiguredTarget();
                  action({ action: 'set-global-active-context', contextId });
                }
                function clearConfiguredTarget() { action({ action: 'clear-active-target' }); }
                function clearActivePort(port) { action({ action: 'clear-active-target', port: Number(port) }); }
                function setActivePort(contextId, port) {
                  if (!contextId) return clearActivePort(port);
                  action({
                    action: 'set-active-target',
                    contextId,
                    port: Number(port),
                    scheme: 'auto'
                  });
                }
                function setHttpsProxyMode(listenIp, port, httpsProxyMode) {
                  action({
                    action: 'set-https-proxy-mode',
                    listenIp,
                    port: Number(port),
                    httpsProxyMode
                  });
                }
                function setScopeContextTarget(scopeType, scopeValue, contextId) {
                  if (scopeType === 'process') {
                    return action(contextId
                      ? { action: 'set-process-target', processId: Number(scopeValue), contextId }
                      : { action: 'clear-process-target', processId: Number(scopeValue) });
                  }
                  if (scopeType === 'image') {
                    return action(contextId
                      ? { action: 'set-image-context-target', applicationKey: scopeValue, contextId }
                      : { action: 'clear-image-context-target', applicationKey: scopeValue });
                  }
                  return action(contextId
                    ? { action: 'set-session-context-target', sessionId: scopeValue, contextId }
                    : { action: 'clear-session-context-target', sessionId: scopeValue });
                }
                function setScopePortTarget(scopeType, scopeValue, port, contextId) {
                  if (scopeType === 'process') {
                    return action(contextId
                      ? { action: 'set-process-port-target', processId: Number(scopeValue), port: Number(port), contextId, scheme: 'auto' }
                      : { action: 'clear-process-port-target', processId: Number(scopeValue), port: Number(port) });
                  }
                  if (scopeType === 'image') {
                    return contextId
                      ? setApplicationTarget(scopeValue, port, contextId)
                      : clearApplicationTarget(scopeValue, port);
                  }
                  return action(contextId
                    ? { action: 'set-session-port-target', sessionId: scopeValue, port: Number(port), contextId, scheme: 'auto' }
                    : { action: 'clear-session-port-target', sessionId: scopeValue, port: Number(port) });
                }
                function setApplicationTarget(applicationKey, port, contextId) {
                  action({
                    action: 'set-application-target',
                    applicationKey,
                    contextId,
                    port: Number(port),
                    scheme: 'auto'
                  });
                }
                function clearApplicationTarget(applicationKey, port) {
                  action({
                    action: 'clear-application-target',
                    applicationKey,
                    port: Number(port)
                  });
                }
                function addSessionRule(event) {
                  event.preventDefault();
                  const name = document.getElementById('session-rule-name').value.trim();
                  const matchKind = document.getElementById('session-match-kind').value;
                  const matchValue = document.getElementById('session-match-value').value.trim();
                  const identityKind = document.getElementById('session-identity-kind').value;
                  const identityValue = document.getElementById('session-identity-value').value.trim();
                  const prefix = document.getElementById('session-prefix').value.trim();
                  action({
                    action: 'add-session-rule',
                    sessionRuleName: name,
                    sessionMatchKind: matchKind,
                    sessionMatchValue: matchValue,
                    sessionIdentityKind: identityKind,
                    sessionIdentityValue: identityValue,
                    sessionPrefix: prefix
                  });
                }
                function removeSessionRule(name) {
                  action({
                    action: 'remove-session-rule',
                    sessionRuleName: name
                  });
                }
                function setBrowserFallbackOnMissingPort(enabled) {
                  action({
                    action: 'set-browser-fallback-on-missing-port',
                    browserFallbackOnMissingPort: enabled === true
                  });
                }
                selectTab(activeTab);
                connectStatusSocket();
                setTimeout(() => { if (!lastStatus) refresh({ preserveMessage: true }); }, 1500);
              </script>
            </body>
            </html>
            """;
}
