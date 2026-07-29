using System.Text.Json;
using Devwt.Core;
using Devwt.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class DevwtWebUiAspNetHost(
    DevwtStateStore store,
    DevwtControlHandler handler,
    Uri listenUri,
    DevwtRouteSnapshotBuilder? routeSnapshotBuilder = null,
    IActiveTcpConnectionSource? connectionSource = null,
    IProcessObservationSource? processSource = null,
    DevwtConnectionHistory? connectionHistory = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = []
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning);
        builder.WebHost.UseUrls(listenUri.ToString());
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(handler);
        builder.Services.AddSingleton(new DevwtWebUiStatusProvider(store, routeSnapshotBuilder, connectionHistory));
        builder.Services
            .AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            });
        builder.Services.AddHostedService<DevwtStatusPublisher>();

        var app = builder.Build();
        app.MapGet("/", () => Results.Content(DevwtWebUiAssets.RenderShell(), "text/html; charset=utf-8"));
        app.MapGet("/api/status", (DevwtWebUiStatusProvider provider) => Results.Json(provider.Build(), JsonOptions));
        app.MapPost("/api/action", HandleActionAsync);
        app.MapHub<DevwtStatusHub>("/hubs/status");

        await app.RunAsync(cancellationToken);
    }

    private async Task<IResult> HandleActionAsync(
        HttpContext context,
        DevwtControlHandler handler,
        DevwtWebUiStatusProvider statusProvider,
        IHubContext<DevwtStatusHub> hubContext)
    {
        var action = await JsonSerializer.DeserializeAsync<DevwtWebUiAction>(
            context.Request.Body,
            JsonOptions,
            context.RequestAborted);
        var result = ExecuteAction(context, handler, action);
        await hubContext.Clients.All.SendAsync("status", statusProvider.Build(), context.RequestAborted);
        return Results.Json(result, JsonOptions);
    }

    private DevwtCommandResult ExecuteAction(HttpContext context, DevwtControlHandler handler, DevwtWebUiAction? action)
    {
        if (action is null || string.IsNullOrWhiteSpace(action.Action))
        {
            return new DevwtCommandResult("missing action\n", 2);
        }

        try
        {
            if (DevwtWebUiActionMapper.IsScopedTargetAction(action.Action))
            {
                return handler.Handle(DevwtWebUiActionMapper.Map(action));
            }
            if (DevwtWebUiActionMapper.IsManagementAction(action.Action))
            {
                return handler.Handle(DevwtWebUiActionMapper.MapManagement(action));
            }

            return action.Action.ToLowerInvariant() switch
            {
                "pause" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.Pause,
                    WorktreePath: action.WorktreePath)),
                "resume" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.Resume,
                    WorktreePath: action.WorktreePath)),
                "remove-repo" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.RemoveRepository,
                    RepositoryName: action.RepositoryName)),
                "set-active-target" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.SetActiveTarget,
                    ActiveTarget: new DevwtActiveTarget(
                        action.ContextId ?? throw new ArgumentException("contextId is required"),
                        action.Port ?? throw new ArgumentException("port is required"),
                        string.IsNullOrWhiteSpace(action.Scheme) ? "auto" : action.Scheme),
                    ActiveTargetBrowserKey: action.BrowserScoped ? ResolveRequiredBrowserKey(context) : null)),
                "set-active-target-mode" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.SetActiveTarget,
                    ActiveTargetMode: ParseActiveTargetMode(action.ActiveTargetMode))),
                "set-global-active-context" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.SetActiveTarget,
                    ActiveTargetMode: DevwtActiveTargetMode.GlobalContext,
                    GlobalActiveContextId: action.ContextId ?? throw new ArgumentException("contextId is required"))),
                "set-https-proxy-mode" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.SetHttpsProxyMode,
                    HttpsProxyEndpoint: new DevwtHttpsProxyEndpoint(
                        action.ListenIp ?? throw new ArgumentException("listenIp is required"),
                        action.Port ?? throw new ArgumentException("port is required"),
                        ParseHttpsProxyMode(action.HttpsProxyMode)))),
                "clear-active-target" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.SetActiveTarget,
                    ClearActiveTarget: true,
                    Port: action.Port,
                    ActiveTargetBrowserKey: action.BrowserScoped ? ResolveRequiredBrowserKey(context) : null)),
                "add-session-rule" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.SetSessionRule,
                    SessionRule: BuildSessionRule(action))),
                "remove-session-rule" => handler.Handle(new DevwtControlRequest(
                    DevwtControlOperation.RemoveSessionRule,
                    SessionRuleName: action.SessionRuleName)),
                _ => new DevwtCommandResult($"unknown action: {action.Action}\n", 2)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return new DevwtCommandResult(ex.Message + Environment.NewLine, 2);
        }
    }

    private string? ResolveBrowserKey(HttpContext context)
    {
        if (connectionSource is null
            || processSource is null
            || context.Connection.RemoteIpAddress is null
            || context.Connection.LocalIpAddress is null
            || context.Connection.RemotePort <= 0
            || context.Connection.LocalPort <= 0)
        {
            return null;
        }

        return DevwtBrowserKeyResolver.ResolveBrowserKey(
            connectionSource,
            processSource,
            new System.Net.IPEndPoint(context.Connection.RemoteIpAddress, context.Connection.RemotePort),
            new System.Net.IPEndPoint(context.Connection.LocalIpAddress, context.Connection.LocalPort));
    }

    private string ResolveRequiredBrowserKey(HttpContext context) =>
        ResolveBrowserKey(context) ?? throw new InvalidOperationException("Could not resolve browser process for browser-scoped routing.");

    private static DevwtSessionRule BuildSessionRule(DevwtWebUiAction action) =>
        new(
            action.SessionRuleName ?? throw new ArgumentException("sessionRuleName is required"),
            BuildSessionMatch(action),
            new DevwtSessionIdentity(
                ParseSessionIdentityKind(action.SessionIdentityKind),
                action.SessionIdentityValue,
                action.SessionPrefix ?? ""));

    private static DevwtSessionMatch BuildSessionMatch(DevwtWebUiAction action)
    {
        var value = action.SessionMatchValue ?? throw new ArgumentException("sessionMatchValue is required");
        return (action.SessionMatchKind ?? "").ToLowerInvariant() switch
        {
            "process-name" => new DevwtSessionMatch(ProcessName: value),
            "image-path" => new DevwtSessionMatch(ImagePathContains: value),
            "command-line" => new DevwtSessionMatch(CommandLineContains: value),
            "env" => new DevwtSessionMatch(EnvironmentVariable: value),
            _ => throw new ArgumentException("Unknown session match kind.")
        };
    }

    private static DevwtSessionIdentityKind ParseSessionIdentityKind(string? value) =>
        (value ?? "").ToLowerInvariant() switch
        {
            "env" => DevwtSessionIdentityKind.EnvironmentVariable,
            "process" => DevwtSessionIdentityKind.Process,
            "root-process" => DevwtSessionIdentityKind.RootProcess,
            "command-line-regex" => DevwtSessionIdentityKind.CommandLineRegex,
            _ => throw new ArgumentException("Unknown session identity kind.")
        };

    private static DevwtActiveTargetMode ParseActiveTargetMode(string? value) =>
        (value ?? "").ToLowerInvariant() switch
        {
            "global-context" => DevwtActiveTargetMode.GlobalContext,
            "per-port" => DevwtActiveTargetMode.PerPort,
            _ => throw new ArgumentException("Unknown active target mode.")
        };

    private static DevwtHttpsProxyMode ParseHttpsProxyMode(string? value) =>
        (value ?? "").ToLowerInvariant() switch
        {
            "auto" => DevwtHttpsProxyMode.Auto,
            "inspect" => DevwtHttpsProxyMode.Inspect,
            "tunnel" => DevwtHttpsProxyMode.Tunnel,
            "raw" => DevwtHttpsProxyMode.Raw,
            _ => throw new ArgumentException("Unknown TCP handling mode.")
        };
}

internal sealed class DevwtStatusHub(DevwtWebUiStatusProvider statusProvider) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("status", statusProvider.Build(), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}

internal sealed class DevwtStatusPublisher(
    DevwtWebUiStatusProvider statusProvider,
    IHubContext<DevwtStatusHub> hubContext,
    ILogger<DevwtStatusPublisher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previousSnapshot = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = statusProvider.Build();
                var snapshot = JsonSerializer.Serialize(status, JsonOptions);
                if (!string.Equals(snapshot, previousSnapshot, StringComparison.Ordinal))
                {
                    previousSnapshot = snapshot;
                    await hubContext.Clients.All.SendAsync("status", status, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
            {
                logger.LogWarning(ex, "DevWT status publish failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
