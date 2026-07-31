using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Yarp.ReverseProxy.Forwarder;

namespace Devwt.Service;

public sealed partial class DevwtGatewayServer
{
    private string? ResolveContextDescription(string contextId) =>
        _routes.DescriptionForContext(contextId);

    private sealed class DevwtYarpProxyHost : IAsyncDisposable
    {
        private static readonly IPAddress Http2InternalAddress = IPAddress.Parse("127.0.0.2");
        private static readonly ForwarderRequestConfig HttpRequestConfig = new()
        {
            ActivityTimeout = Timeout.InfiniteTimeSpan,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        private static readonly ForwarderRequestConfig HttpsRequestConfig = new()
        {
            ActivityTimeout = Timeout.InfiniteTimeSpan,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        private static readonly ForwarderRequestConfig Http2PriorKnowledgeRequestConfig = new()
        {
            ActivityTimeout = Timeout.InfiniteTimeSpan,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        private readonly DevwtGatewayServer _owner;
        private readonly WebApplication _application;
        private readonly ConcurrentDictionary<ProxyConnectionKey, ProxyConnection> _connections = [];
        private readonly ConcurrentDictionary<BackendClientKey, HttpMessageInvoker> _clients = [];
        private readonly ConcurrentDictionary<BackendTransportKey, Lazy<Task<BackendTransport>>> _backendTransports = [];
        private int _httpPort;
        private int _http2Port;
        private int? _httpsPort;

        private DevwtYarpProxyHost(DevwtGatewayServer owner, WebApplication application)
        {
            _owner = owner;
            _application = application;
        }

        public static async Task<DevwtYarpProxyHost> StartAsync(
            DevwtGatewayServer owner,
            X509Certificate2? serverCertificate,
            CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(DevwtGatewayServer).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();
            builder.Services.AddHttpForwarder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
                options.Listen(Http2InternalAddress, 0, listen => listen.Protocols = HttpProtocols.Http2);
                if (serverCertificate is not null)
                {
                    options.Listen(IPAddress.Loopback, 0, listen =>
                    {
                        listen.Protocols = HttpProtocols.Http1AndHttp2;
                        listen.UseHttps(serverCertificate);
                    });
                }
            });

            var application = builder.Build();
            var host = new DevwtYarpProxyHost(owner, application);
            application.Run(host.HandleRequestAsync);
            try
            {
                await application.StartAsync(cancellationToken);
                host.ReadBoundPorts();
                return host;
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }

        public async Task ProxyConnectionAsync(
            GatewayListenEndpoint endpoint,
            TcpClient client,
            byte[] initialBytes,
            bool useTls,
            bool useHttp2PriorKnowledge,
            Lazy<ClientProcessIdentity> identity,
            string? originalRemoteEndpoint)
        {
            var destinationPort = useTls
                ? _httpsPort
                : useHttp2PriorKnowledge
                    ? _http2Port
                    : _httpPort;
            if (destinationPort is null or 0)
            {
                return;
            }

            using var target = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                var destinationAddress = useHttp2PriorKnowledge ? Http2InternalAddress : IPAddress.Loopback;
                await target.ConnectAsync(destinationAddress, destinationPort.Value);
                var localEndpoint = (IPEndPoint)target.Client.LocalEndPoint!;
                var targetEndpoint = (IPEndPoint)target.Client.RemoteEndPoint!;
                var connectionKey = ProxyConnectionKey.FromSocket(localEndpoint, targetEndpoint);
                var connection = new ProxyConnection(
                    endpoint,
                    identity,
                    originalRemoteEndpoint,
                    useHttp2PriorKnowledge);
                if (!_connections.TryAdd(connectionKey, connection))
                {
                    return;
                }

                try
                {
                    await using var clientStream = client.GetStream();
                    await using var targetStream = target.GetStream();
                    await targetStream.WriteAsync(initialBytes);
                    var upload = clientStream.CopyToAsync(targetStream);
                    var download = targetStream.CopyToAsync(clientStream);
                    await Task.WhenAny(upload, download);
                }
                finally
                {
                    _connections.TryRemove(connectionKey, out _);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _connections.Clear();
            _backendTransports.Clear();
            await _application.StopAsync();
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
            await _application.DisposeAsync();
        }

        private HttpMessageInvoker GetHttpClient(BackendClientKey key)
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = CreateHttpClient(key);
            var selected = _clients.GetOrAdd(key, created);
            if (!ReferenceEquals(selected, created))
            {
                created.Dispose();
            }

            return selected;
        }

        private async Task HandleRequestAsync(HttpContext context)
        {
            var connectionKey = ProxyConnectionKey.FromHttpContext(context);
            if (!_connections.TryGetValue(connectionKey, out var connection))
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            var requestContextId = context.Request.Headers["X-DevWT-Context"].FirstOrDefault();
            var allowRequestContextFallback = context.Request.Headers["X-DevWT-Allow-Fallback"]
                .Any(value => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
            var cookieContextId = context.Request.Cookies.TryGetValue("devwt-context", out var cookieValue)
                ? cookieValue
                : null;
            var decision = _owner.ResolveRoute(
                connection.Endpoint,
                connection.Identity.Value,
                requestContextId,
                cookieContextId,
                allowRequestContextFallback);
            if (decision is null)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            _owner.RecordConnection(connection.Endpoint, connection.OriginalRemoteEndpoint, decision);
            var route = decision.Route;
            var targetAddress = TargetConnectAddressFor(route);
            var tlsDestinationHost = ResolveTlsDestinationHost(connection.Endpoint, context.Request.Host.Host);
            var useBackendTls = context.Request.IsHttps
                && await UsesBackendTlsAsync(
                    route,
                    targetAddress,
                    tlsDestinationHost,
                    context.RequestAborted);
            var destinationHost = useBackendTls
                ? tlsDestinationHost
                : targetAddress.ToString();
            var destinationPrefix = new UriBuilder(
                useBackendTls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                destinationHost,
                route.TargetPort,
                "/").Uri.AbsoluteUri;
            var clientKey = new BackendClientKey(
                targetAddress,
                route.TargetPort,
                useBackendTls);
            var httpClient = GetHttpClient(clientKey);
            var transformer = new DevwtYarpTransformer(
                context.Request.Host.Value ?? "",
                decision.Route.ContextId,
                decision.RouteReason,
                _owner.ResolveContextDescription(decision.Route.ContextId));

            var forwarder = context.RequestServices.GetRequiredService<IHttpForwarder>();
            var requestConfig = useBackendTls
                ? HttpsRequestConfig
                : connection.UseHttp2PriorKnowledge
                    ? Http2PriorKnowledgeRequestConfig
                    : HttpRequestConfig;
            var error = await forwarder.SendAsync(
                context,
                destinationPrefix,
                httpClient,
                requestConfig,
                transformer,
                context.RequestAborted);
            if (error != ForwarderError.None && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.Headers["X-DevWT-Proxy-Error"] = error.ToString();
            }
        }

        private async Task<bool> UsesBackendTlsAsync(
            GatewayRoute route,
            IPAddress targetAddress,
            string targetHost,
            CancellationToken cancellationToken)
        {
            var key = new BackendTransportKey(targetAddress, route.TargetPort, route.ListenerProcessId);
            var probe = _backendTransports.GetOrAdd(
                key,
                _ => new Lazy<Task<BackendTransport>>(
                    () => ProbeBackendTlsAsync(targetAddress, route.TargetPort, targetHost),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            var transport = await probe.Value.WaitAsync(cancellationToken);
            if (transport == BackendTransport.Unknown)
            {
                _backendTransports.TryRemove(key, out _);
            }
            TrimBackendTransportCache();
            return transport == BackendTransport.Tls;
        }

        private static async Task<BackendTransport> ProbeBackendTlsAsync(
            IPAddress targetAddress,
            int targetPort,
            string targetHost)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var socket = new Socket(targetAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(targetAddress, targetPort), timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return BackendTransport.Unknown;
            }

            try
            {
                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var tls = new SslStream(
                    stream,
                    leaveInnerStreamOpen: false,
                    (_, certificate, _, _) => certificate is not null);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
                }, timeout.Token);
                return BackendTransport.Tls;
            }
            catch (Exception ex) when (ex is AuthenticationException
                or IOException
                or SocketException
                or OperationCanceledException)
            {
                return BackendTransport.Plaintext;
            }
        }

        private void TrimBackendTransportCache()
        {
            const int maximumEntries = 256;
            if (_backendTransports.Count <= maximumEntries)
            {
                return;
            }

            var active = _owner._routes.Routes
                .Select(route => new BackendTransportKey(
                    TargetConnectAddressFor(route),
                    route.TargetPort,
                    route.ListenerProcessId))
                .ToHashSet();
            foreach (var key in _backendTransports.Keys)
            {
                if (!active.Contains(key))
                {
                    _backendTransports.TryRemove(key, out _);
                }
            }

            foreach (var key in _backendTransports.Keys.Take(Math.Max(0, _backendTransports.Count - maximumEntries)))
            {
                _backendTransports.TryRemove(key, out _);
            }
        }

        private HttpMessageInvoker CreateHttpClient(BackendClientKey key)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = Timeout.InfiniteTimeSpan,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(key.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(key.Address, context.DnsEndPoint.Port),
                            cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            if (key.UseTls)
            {
                handler.SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                        ValidateUpstreamCertificate(certificate, errors, IsLocalAddress(key.Address))
                };
            }

            return new HttpMessageInvoker(handler, disposeHandler: true);
        }

        private void ReadBoundPorts()
        {
            var server = _application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("Kestrel did not publish its bound proxy addresses.");
            foreach (var address in addresses.Select(value => new Uri(value)))
            {
                if (address.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                {
                    if (IPAddress.TryParse(address.Host, out var listenAddress)
                        && listenAddress.Equals(Http2InternalAddress))
                    {
                        _http2Port = address.Port;
                    }
                    else
                    {
                        _httpPort = address.Port;
                    }
                }
                else if (address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    _httpsPort = address.Port;
                }
            }

            if (_httpPort == 0 || _http2Port == 0)
            {
                throw new InvalidOperationException("Kestrel did not bind the internal HTTP/1 and HTTP/2 proxy endpoints.");
            }
        }

        private static string ResolveTlsDestinationHost(GatewayListenEndpoint endpoint, string requestHost)
        {
            return string.IsNullOrWhiteSpace(requestHost) ? endpoint.Ip : requestHost;
        }

        private sealed record ProxyConnection(
            GatewayListenEndpoint Endpoint,
            Lazy<ClientProcessIdentity> Identity,
            string? OriginalRemoteEndpoint,
            bool UseHttp2PriorKnowledge);

        private sealed record ProxyConnectionKey(
            IPAddress ClientAddress,
            int ClientPort,
            IPAddress ProxyAddress,
            int ProxyPort)
        {
            public static ProxyConnectionKey FromSocket(IPEndPoint client, IPEndPoint proxy) =>
                new(client.Address, client.Port, proxy.Address, proxy.Port);

            public static ProxyConnectionKey FromHttpContext(HttpContext context) =>
                new(
                    context.Connection.RemoteIpAddress ?? IPAddress.None,
                    context.Connection.RemotePort,
                    context.Connection.LocalIpAddress ?? IPAddress.None,
                    context.Connection.LocalPort);
        }

        private sealed record BackendClientKey(
            IPAddress Address,
            int Port,
            bool UseTls);

        private sealed record BackendTransportKey(
            IPAddress Address,
            int Port,
            int ListenerProcessId);

        private enum BackendTransport
        {
            Unknown,
            Plaintext,
            Tls
        }
    }

    private sealed class DevwtYarpTransformer(
        string originalHost,
        string contextId,
        string routeReason,
        string? contextDescription) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.Headers.Remove("X-DevWT-Context");
            proxyRequest.Headers.Remove("X-DevWT-Allow-Fallback");
            proxyRequest.Headers.Remove("X-DevWT-Tab");
            proxyRequest.Headers.Remove("X-DevWT-Token");
            proxyRequest.Headers.Remove("Cookie");
            var applicationCookies = httpContext.Request.GetTypedHeaders().Cookie
                .Where(cookie => !cookie.Name.Equals("devwt-context", StringComparison.OrdinalIgnoreCase))
                .Select(cookie => cookie.ToString())
                .ToArray();
            if (applicationCookies.Length > 0)
            {
                proxyRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", applicationCookies));
            }
            proxyRequest.Headers.Host = originalHost;
        }

        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext,
            HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            var copyResponse = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            httpContext.Response.Headers["X-DevWT-Context"] = contextId;
            httpContext.Response.Headers["X-DevWT-Route-Reason"] = routeReason;
            if (ToResponseHeaderValue(contextDescription) is { } description)
            {
                httpContext.Response.Headers["X-DevWT-Description"] = description;
            }
            return copyResponse;
        }

        private static string? ToResponseHeaderValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Any(character => char.IsControl(character)))
            {
                return null;
            }

            return trimmed.All(character => character is >= ' ' and <= '~')
                ? trimmed
                : Uri.EscapeDataString(trimmed);
        }
    }
}
