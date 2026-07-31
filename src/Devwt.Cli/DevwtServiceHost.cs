using System.ComponentModel;
using System.Diagnostics;
using Devwt.Core;
using Devwt.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

internal static class DevwtServiceHost
{
    public const string ServiceName = "DevWTService";

    public static async Task<int> RunAsync(
        string? stateRoot,
        bool gatewayEnabled,
        bool uiEnabled,
        Uri uiListenUri)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        builder.Services.AddSingleton(new DevwtServiceOptions(stateRoot, gatewayEnabled, uiEnabled, uiListenUri));
        builder.Services.AddHostedService<DevwtControlHostedService>();
        builder.Services.AddHostedService<DevwtWorktreeSyncHostedService>();
        builder.Services.AddHostedService<DevwtHookRuntimeHostedService>();
        if (gatewayEnabled)
        {
            builder.Services.AddHostedService<DevwtGatewayHostedService>();
        }

        if (uiEnabled)
        {
            builder.Services.AddHostedService<DevwtWebUiHostedService>();
        }

        await builder.Build().RunAsync();
        return 0;
    }
}

internal sealed class DevwtWorktreeSyncHostedService(
    DevwtServiceOptions options,
    ILogger<DevwtWorktreeSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = DevwtServiceFactory.CreateStore(options);
        var runner = new ProcessCommandRunner();
        var hookRuntime = DevwtServiceFactory.CreateRuntimeConfigurator();
        var manager = new DevwtManager(
            store,
            new GitInspector(runner),
            hookRuntime);
        try
        {
            var configured = new DevwtHookRuntimeReconciler(store, hookRuntime).ReconcileOnce();
            if (configured > 0)
            {
                logger.LogInformation("DevWT reconfigured {Count} existing hook runtime context(s).", configured);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DevWT hook runtime reconcile failed.");
        }

        var synchronizer = new DevwtWorktreeSynchronizer(
            store,
            manager,
            new GitInspector(runner),
            new GitWorktreeMaterializer(runner));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = synchronizer.SyncOnce();
                if (count > 0)
                {
                    logger.LogInformation("DevWT registered {Count} new worktree(s).", count);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DevWT worktree sync failed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}

internal sealed record DevwtServiceOptions(
    string? StateRoot,
    bool GatewayEnabled,
    bool UiEnabled,
    Uri UiListenUri);

internal sealed class DevwtHookRuntimeHostedService(
    DevwtServiceOptions options,
    ILogger<DevwtHookRuntimeHostedService> logger) : BackgroundService
{
    private Process? _watcher;
    private string? _signature;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = DevwtServiceFactory.CreateStore(options);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    RefreshWatcher(store);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "DevWT hook runtime refresh failed.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            StopWatcher();
        }
    }

    private void RefreshWatcher(DevwtStateStore store)
    {
        var contexts = store.LoadContexts().Contexts
            .Where(context => context.Status == DevwtContextStatus.Active)
            .OrderBy(context => context.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ideWatches = store.LoadRuntimeSettings().IdeWatches
            .OrderBy(watch => watch.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var signature = string.Join("|", contexts.Select(context => $"{context.Id}={context.WorktreeRootPath}={context.AssignedIp}={context.AssignedPortBase}"))
            + "::ide="
            + string.Join("|", ideWatches.Select(watch => $"{watch.Name}={watch.ImagePath}"));
        Directory.CreateDirectory(store.StateRoot);
        var mapPath = HookRuntimeContextMap.ResolvePath(store.StateRoot);
        var portBindingsPath = HookPortBindingMap.ResolvePath(store.StateRoot);
        HookRuntimeContextMap.Write(mapPath, contexts);
        if (contexts.Length == 0 && ideWatches.Length == 0)
        {
            StopWatcher();
            _signature = signature;
            return;
        }

        if (_watcher is { HasExited: false } && string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            return;
        }

        StopWatcher();
        _signature = signature;

        var hookRoot = DevwtHookRuntimePaths.ResolveHookRoot(AppContext.BaseDirectory);
        var watcherPath = Path.Combine(hookRoot, "devwt-folder-watcher.exe");
        var hookDllPath = Path.Combine(hookRoot, "devwt-hook.dll");
        if (!File.Exists(watcherPath) || !File.Exists(hookDllPath))
        {
            logger.LogWarning("DevWT hook runtime artifacts are missing under {HookRoot}. Run the installer bundle or build the hook runtime.", hookRoot);
            return;
        }

        var command = HookRuntimeCommandPlanner.PlanFolderWatcher(
            watcherPath,
            hookDllPath,
            contexts,
            ideWatches,
            mapFilePath: mapPath,
            portBindingsPath: portBindingsPath,
            logPath: Path.Combine(store.StateRoot, "hook-runtime.log"));
        var startInfo = new ProcessStartInfo(command.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _watcher = Process.Start(startInfo);
        logger.LogInformation(
            "DevWT hook runtime watcher started for {ContextCount} context(s) and {IdeWatchCount} IDE watch entr{Suffix}.",
            contexts.Length,
            ideWatches.Length,
            ideWatches.Length == 1 ? "y" : "ies");
    }

    private void StopWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            if (!_watcher.HasExited)
            {
                _watcher.Kill(entireProcessTree: true);
                _watcher.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
        finally
        {
            _watcher.Dispose();
            _watcher = null;
        }
    }
}

internal static class DevwtServiceFactory
{
    public static DevwtConnectionHistory ConnectionHistory { get; } = new();

    public static DevwtStateStore CreateStore(DevwtServiceOptions options) => new(options.StateRoot);

    public static DevwtManager CreateManager(DevwtStateStore store)
    {
        return new DevwtManager(
            store,
            new GitInspector(new ProcessCommandRunner()),
            CreateRuntimeConfigurator());
    }

    public static IHookRuntimeConfigurator CreateRuntimeConfigurator() =>
        new HookRuntimeConfigurator();
}

internal sealed class DevwtControlHostedService(
    DevwtServiceOptions options,
    ILogger<DevwtControlHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = DevwtServiceFactory.CreateStore(options);
        var manager = DevwtServiceFactory.CreateManager(store);
        var server = new DevwtNamedPipeControlServer(new DevwtControlHandler(
            manager,
            store,
            connectionHistory: DevwtServiceFactory.ConnectionHistory));
        logger.LogInformation("DevWT control pipe started.");
        await server.RunAsync(stoppingToken);
    }
}

internal sealed class DevwtGatewayHostedService(
    DevwtServiceOptions options,
    ILogger<DevwtGatewayHostedService> logger) : BackgroundService
{
    private readonly Dictionary<DevwtGatewayWorkerEndpoint, GatewayWorkerHandle> _workers = [];
    private readonly Dictionary<DevwtGatewayWorkerEndpoint, HashSet<int>> _tombstones = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new ProcessCommandRunner();
        var store = DevwtServiceFactory.CreateStore(options);
        var snapshotBuilder = new DevwtRouteSnapshotBuilder(store, new WindowsTcpListenerObservationSource(runner));
        var snapshotStore = new DevwtGatewayRouteSnapshotStore(store.StateRoot);
        var processController = new DevwtProcessController();
        CleanupWorkerAliases();
        logger.LogInformation("DevWT per-port gateway supervisor started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var routeTable = snapshotBuilder.BuildRouteTable();
                    snapshotStore.Save(routeTable);
                    RefreshWorkers(routeTable, store, processController);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "DevWT gateway worker refresh failed.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var worker in _workers.Values.ToArray())
            {
                StopWorker(worker);
            }

            _workers.Clear();
            _tombstones.Clear();
            CleanupWorkerAliases();
        }
    }

    private void RefreshWorkers(
        GatewayRouteTable routeTable,
        DevwtStateStore store,
        IDevwtProcessController processController)
    {
        var routeGroups = routeTable.Routes
            .GroupBy(DevwtGatewayWorkerEndpoint.FromRoute)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var (key, worker) in _workers.ToArray())
        {
            if (!worker.Process.HasExited)
            {
                continue;
            }

            var exitCode = worker.Process.ExitCode;
            logger.LogWarning(
                "Gateway owner worker {ProcessId} for {Ip}:{Port} exited unexpectedly with code {ExitCode}; releasing the virtual port.",
                worker.Process.Id,
                key.Ip,
                key.Port,
                exitCode);
            DisposeWorker(worker);
            _workers.Remove(key);
            _tombstones[key] = [];
        }

        foreach (var (key, killedProcessIds) in _tombstones.ToArray())
        {
            if (!routeGroups.TryGetValue(key, out var routes))
            {
                _tombstones.Remove(key);
                continue;
            }

            foreach (var processId in DevwtGatewayWorkerExitPolicy.ListenerProcessIdsFor(routes, key))
            {
                if (!killedProcessIds.Add(processId))
                {
                    continue;
                }

                var result = processController.Stop(processId, force: true);
                if (result.Exited)
                {
                    logger.LogInformation(
                        "Stopped virtual listener process {ProcessId} after gateway owner termination for {Ip}:{Port}.",
                        processId,
                        key.Ip,
                        key.Port);
                }
                else
                {
                    logger.LogWarning(
                        "Could not stop virtual listener process {ProcessId} for {Ip}:{Port}: {Message}",
                        processId,
                        key.Ip,
                        key.Port,
                        result.Message);
                }
            }
        }

        foreach (var (key, worker) in _workers.ToArray())
        {
            if (!routeGroups.TryGetValue(key, out var routes))
            {
                StopWorker(worker);
                _workers.Remove(key);
                continue;
            }

            var imageNames = ResolveImageNames(routes);
            var signature = WorkerSignature(key, imageNames);
            if (string.Equals(signature, worker.Signature, StringComparison.Ordinal))
            {
                continue;
            }

            StopWorker(worker);
            _workers.Remove(key);
            _workers[key] = StartWorker(key, imageNames, signature, store.StateRoot);
        }

        foreach (var (key, routes) in routeGroups)
        {
            if (_workers.ContainsKey(key) || _tombstones.ContainsKey(key))
            {
                continue;
            }

            var imageNames = ResolveImageNames(routes);
            var signature = WorkerSignature(key, imageNames);
            _workers[key] = StartWorker(key, imageNames, signature, store.StateRoot);
        }
    }

    private GatewayWorkerHandle StartWorker(
        DevwtGatewayWorkerEndpoint key,
        IReadOnlyList<string> imageNames,
        string signature,
        string stateRoot)
    {
        var executablePath = CreateWorkerAlias(imageNames, signature);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("gateway-worker");
        startInfo.ArgumentList.Add("--state-root");
        startInfo.ArgumentList.Add(stateRoot);
        startInfo.ArgumentList.Add("--ip");
        startInfo.ArgumentList.Add(key.Ip);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(key.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var imageName in imageNames)
        {
            startInfo.ArgumentList.Add("--owner-image");
            startInfo.ArgumentList.Add(imageName);
        }

        startInfo.Environment["DEVWT_PROXY_ORIGINAL_IMAGES"] = string.Join(';', imageNames);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start gateway worker for {key.Ip}:{key.Port}.");
        logger.LogInformation(
            "Gateway owner worker {ProcessId} ({ImageName}) started for {Ip}:{Port}.",
            process.Id,
            Path.GetFileName(executablePath),
            key.Ip,
            key.Port);
        return new GatewayWorkerHandle(process, executablePath, signature);
    }

    private static IReadOnlyList<string> ResolveImageNames(IReadOnlyList<GatewayRoute> routes)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var processId in routes.Select(route => route.ListenerProcessId).Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                names.Add(process.ProcessName + ".exe");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                names.Add($"pid-{processId}.exe");
            }
        }

        return names.ToArray();
    }

    private static string WorkerSignature(DevwtGatewayWorkerEndpoint key, IReadOnlyList<string> imageNames) =>
        $"{key.Ip}:{key.Port}|{string.Join('|', imageNames)}";

    private static string CreateWorkerAlias(IReadOnlyList<string> imageNames, string signature)
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Devwt.Cli.exe");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("DevWT app host is required for gateway workers.", sourcePath);
        }

        var aliasPath = Path.Combine(
            AppContext.BaseDirectory,
            DevwtGatewayWorkerNames.BuildAliasFileName(imageNames, signature));
        File.Copy(sourcePath, aliasPath, overwrite: true);
        return aliasPath;
    }

    private static void StopWorker(GatewayWorkerHandle worker)
    {
        try
        {
            if (!worker.Process.HasExited)
            {
                worker.Process.Kill(entireProcessTree: true);
                worker.Process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
        finally
        {
            DisposeWorker(worker);
        }
    }

    private static void DisposeWorker(GatewayWorkerHandle worker)
    {
        worker.Process.Dispose();
        try
        {
            File.Delete(worker.ExecutablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CleanupWorkerAliases()
    {
        foreach (var aliasPath in Directory.EnumerateFiles(
                     AppContext.BaseDirectory,
                     $"*{DevwtGatewayWorkerNames.AliasMarker}*.exe"))
        {
            try
            {
                File.Delete(aliasPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record GatewayWorkerHandle(Process Process, string ExecutablePath, string Signature);
}

internal sealed class DevwtWebUiHostedService(
    DevwtServiceOptions options,
    ILogger<DevwtWebUiHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = DevwtServiceFactory.CreateStore(options);
        var runner = new ProcessCommandRunner();
        var processSource = new WindowsProcessObservationSource(runner, runtimeSettingsProvider: store.LoadRuntimeSettings);
        var server = new DevwtWebUiAspNetHost(
            store,
            new DevwtControlHandler(DevwtServiceFactory.CreateManager(store), store),
            options.UiListenUri,
            new DevwtRouteSnapshotBuilder(store, new WindowsTcpListenerObservationSource(runner)),
            new WindowsActiveTcpConnectionSource(runner),
            processSource,
            DevwtServiceFactory.ConnectionHistory);
        logger.LogInformation("DevWT Web UI listening on {ListenUri}", options.UiListenUri);
        await server.RunAsync(stoppingToken);
    }
}
