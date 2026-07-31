using Devwt.Core;
using Devwt.Service;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;

if (args.Length == 0)
{
    Console.Write(DevwtCommandParser.HelpText);
    return 0;
}

if (args[0].Equals("update", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        var command = (UpdateCommand)DevwtCommandParser.Parse(args, Environment.CurrentDirectory);
        using var httpClient = new HttpClient();
        var updater = new DevwtReleaseUpdater(
            httpClient,
            new WindowsDevwtElevatedScriptRunner());
        var updateResult = await updater.ExecuteAsync(command);
        Console.Write(updateResult.Output);
        return updateResult.ExitCode;
    }
    catch (Exception ex) when (ex is ArgumentException
        or HttpRequestException
        or InvalidDataException
        or IOException
        or InvalidOperationException
        or UnauthorizedAccessException
        or Win32Exception)
    {
        Console.Error.WriteLine(ex is Win32Exception { NativeErrorCode: 1223 }
            ? "DevWT update was canceled before elevation."
            : ex.Message);
        return 2;
    }
}

if (args[0].Equals("service", StringComparison.OrdinalIgnoreCase))
{
    return await RunServiceCommandAsync(args.Skip(1).ToArray());
}

if (args[0].Equals("gateway-worker", StringComparison.OrdinalIgnoreCase))
{
    return await RunGatewayWorkerAsync(args.Skip(1).ToArray());
}

if (args[0].Equals("cert", StringComparison.OrdinalIgnoreCase))
{
    return RunCertificateCommand(args.Skip(1).ToArray());
}

if (args[0].Equals("gateway-cert", StringComparison.OrdinalIgnoreCase))
{
    return RunGatewayCertificateCommand(args.Skip(1).ToArray());
}

if (args[0].Equals("shell", StringComparison.OrdinalIgnoreCase))
{
    return RunShellCommand(args.Skip(1).ToArray());
}

if (args[0].Equals("shortcut", StringComparison.OrdinalIgnoreCase))
{
    return DevwtShortcutCommand.Execute(args, Environment.CurrentDirectory, ResolveDevwtExecutablePath());
}

if (args[0].Equals("ui", StringComparison.OrdinalIgnoreCase))
{
    var options = ParseUiOptions(args.Skip(1).ToArray());
    var store = new DevwtStateStore(options.StateRoot);
    var runner = new ProcessCommandRunner();
    await new DevwtWebUiAspNetHost(
        store,
        new DevwtControlHandler(DevwtServiceFactory.CreateManager(store), store),
        options.ListenUri,
        new DevwtRouteSnapshotBuilder(store, new WindowsTcpListenerObservationSource(runner))).RunAsync(CancellationToken.None);
    return 0;
}

if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase)
    || args[0].Equals("exec", StringComparison.OrdinalIgnoreCase)
    || args[0].Equals("terminal", StringComparison.OrdinalIgnoreCase))
{
    return DevwtRuntimeLauncher.Execute(args, Environment.CurrentDirectory);
}

var result = DevwtCliRunner.Execute(args, Environment.CurrentDirectory, new DevwtNamedPipeControlClient());
Console.Write(result.Output);

return result.ExitCode;

static string ResolveDevwtExecutablePath()
{
    var appHostPath = Path.Combine(AppContext.BaseDirectory, "Devwt.Cli.exe");
    return File.Exists(appHostPath)
        ? appHostPath
        : Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve DevWT executable path.");
}

static async Task<int> RunServiceCommandAsync(IReadOnlyList<string> args)
{
    if (args.Count == 0)
    {
        Console.WriteLine("service requires subcommand: run, install, uninstall, start, stop, restart, status");
        return 2;
    }

    return args[0].ToLowerInvariant() switch
    {
        "run" => await RunServiceAsync(args.Skip(1).ToArray()),
        "install" => RunServiceInstall(args.Skip(1).ToArray()),
        "uninstall" => RunServiceUninstall(args.Skip(1).ToArray()),
        "start" => RunServiceStart(),
        "stop" => RunScCommand("stop"),
        "restart" => RunServiceRestart(),
        "status" => RunScCommand("query"),
        _ => ServiceHelp(args[0])
    };
}

static int RunCertificateCommand(IReadOnlyList<string> args)
{
    if (args.Count == 0)
    {
        Console.WriteLine("cert requires subcommand: trust, status, clean");
        return 2;
    }

    var runner = new ProcessCommandRunner();
    var result = args[0].ToLowerInvariant() switch
    {
        "trust" => runner.Run(["dotnet", "dev-certs", "https", "--trust"]),
        "status" => runner.Run(["dotnet", "dev-certs", "https", "--check", "--trust"]),
        "clean" => runner.Run(["dotnet", "dev-certs", "https", "--clean"]),
        _ => new CommandResult(2, "", "cert requires subcommand: trust, status, clean\n")
    };
    Console.Write(string.Concat(result.Output, result.Error));
    return result.ExitCode;
}

static int RunGatewayCertificateCommand(IReadOnlyList<string> args)
{
    if (args.Count == 0)
    {
        Console.WriteLine("gateway-cert requires subcommand: trust [--user|--machine], status, clean");
        return 2;
    }

    var store = new DevwtGatewayCertificateStore(DevwtStateDefaults.ResolveStateRoot(null));
    try
    {
        switch (args[0].ToLowerInvariant())
        {
            case "trust":
                var location = args.Skip(1).Any(arg => arg.Equals("--machine", StringComparison.OrdinalIgnoreCase))
                    ? StoreLocation.LocalMachine
                    : StoreLocation.CurrentUser;
                store.TrustRoot(location);
                Console.WriteLine(location == StoreLocation.LocalMachine
                    ? "DevWT gateway certificate root is trusted for the local machine."
                    : "DevWT gateway certificate root is trusted for the current user.");
                Console.Write(store.Status());
                return 0;
            case "status":
                Console.Write(store.Status());
                return 0;
            case "clean":
                store.Clean();
                Console.WriteLine("DevWT gateway certificate files and current-user trust entry were removed.");
                return 0;
            default:
                Console.WriteLine("gateway-cert requires subcommand: trust [--user|--machine], status, clean");
                return 2;
        }
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
    {
        Console.WriteLine(ex.Message);
        return 2;
    }
}

static int RunShellCommand(IReadOnlyList<string> args)
{
    if (args.Count == 0)
    {
        Console.WriteLine("shell requires subcommand: install, uninstall, status, attach");
        return 2;
    }

    var profiles = ShellProfiles();
    try
    {
        switch (args[0].ToLowerInvariant())
        {
            case "install":
                foreach (var profile in profiles)
                {
                    var result = PowerShellShellIntegration.Install(profile);
                    Console.WriteLine($"{(result.Modified ? "installed" : "already installed")}: {result.ProfilePath}");
                }

                Console.WriteLine("Open a new PowerShell tab for generic DevWT child injection to load.");
                return 0;
            case "uninstall":
                foreach (var profile in profiles)
                {
                    var result = PowerShellShellIntegration.Uninstall(profile);
                    Console.WriteLine($"{(result.Modified ? "removed" : "not installed")}: {result.ProfilePath}");
                }

                Console.WriteLine("Open a new PowerShell tab for the change to take effect.");
                return 0;
            case "status":
                foreach (var profile in profiles)
                {
                    Console.WriteLine($"{(PowerShellShellIntegration.IsInstalled(profile) ? "installed" : "not installed")}: {profile}");
                }

                return 0;
            case "attach":
                return RunShellAttach(args.Skip(1).ToArray());
            default:
                Console.WriteLine("shell requires subcommand: install, uninstall, status, attach");
                return 2;
        }
    }
    catch (Exception ex) when (ex is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or System.ComponentModel.Win32Exception)
    {
        Console.WriteLine(ex.Message);
        return 2;
    }
}

static int RunShellAttach(IReadOnlyList<string> args)
{
    if (args.Count != 2
        || !args[0].Equals("--pid", StringComparison.OrdinalIgnoreCase)
        || !int.TryParse(args[1], out var processId)
        || processId <= 0)
    {
        Console.WriteLine("shell attach requires --pid <positive-pid>");
        return 2;
    }

    var hookRoot = DevwtHookRuntimePaths.ResolveHookRoot(AppContext.BaseDirectory);
    var watcherPath = Path.Combine(hookRoot, "devwt-folder-watcher.exe");
    var hookDllPath = Path.Combine(hookRoot, "devwt-hook.dll");
    if (!File.Exists(watcherPath) || !File.Exists(hookDllPath))
    {
        Console.WriteLine($"DevWT hook runtime was not found under {hookRoot}.");
        return 2;
    }

    var stateRoot = DevwtStateDefaults.ResolveStateRoot(null);
    var result = new ProcessCommandRunner().Run(
        [
            watcherPath,
            "--dll",
            hookDllPath,
            "--children-only-pid",
            processId.ToString(CultureInfo.InvariantCulture),
            "--map-file",
            HookRuntimeContextMap.ResolvePath(stateRoot),
            "--port-bindings-file",
            HookPortBindingMap.ResolvePath(stateRoot)
        ]);
    if (!string.IsNullOrWhiteSpace(result.Output))
    {
        Console.Write(result.Output);
    }

    if (!string.IsNullOrWhiteSpace(result.Error))
    {
        Console.Error.Write(result.Error);
    }

    return result.ExitCode;
}

static IReadOnlyList<string> ShellProfiles() =>
[
    PowerShellShellIntegration.DefaultWindowsPowerShellProfilePath(),
    PowerShellShellIntegration.DefaultPowerShellProfilePath()
];

static async Task<int> RunServiceAsync(IReadOnlyList<string> args)
{
    string? stateRoot = null;
    var gateway = true;
    var ui = true;
    var uiListen = new Uri("http://127.0.0.1:17776/");

    for (var index = 0; index < args.Count; index++)
    {
        var option = args[index];
        switch (option)
        {
            case "--state-root":
                stateRoot = RequiredValue(args, ref index, option);
                break;
            case "--no-gateway":
                gateway = false;
                break;
            case "--no-ui":
                ui = false;
                break;
            case "--ui-listen":
                uiListen = ParseHttpUri(RequiredValue(args, ref index, option), option);
                break;
            default:
                Console.WriteLine($"Unknown service run option: {option}");
                return 2;
        }
    }

    return await DevwtServiceHost.RunAsync(stateRoot, gateway, ui, uiListen);
}

static async Task<int> RunGatewayWorkerAsync(IReadOnlyList<string> args)
{
    string? stateRoot = null;
    string? ip = null;
    int? port = null;
    int? parentProcessId = null;

    for (var index = 0; index < args.Count; index++)
    {
        var option = args[index];
        switch (option)
        {
            case "--state-root":
                stateRoot = RequiredValue(args, ref index, option);
                break;
            case "--ip":
                ip = RequiredValue(args, ref index, option);
                break;
            case "--port":
                port = int.Parse(RequiredValue(args, ref index, option), CultureInfo.InvariantCulture);
                break;
            case "--parent-pid":
                parentProcessId = int.Parse(RequiredValue(args, ref index, option), CultureInfo.InvariantCulture);
                break;
            case "--owner-image":
                _ = RequiredValue(args, ref index, option);
                break;
            default:
                Console.Error.WriteLine($"Unknown gateway worker option: {option}");
                return 2;
        }
    }

    if (string.IsNullOrWhiteSpace(stateRoot)
        || string.IsNullOrWhiteSpace(ip)
        || !IPAddress.TryParse(ip, out _)
        || port is not (> 0 and <= 65535)
        || parentProcessId is not > 0)
    {
        Console.Error.WriteLine("gateway-worker requires --state-root, --ip, --port, and --parent-pid.");
        return 2;
    }

    using var lifetime = new CancellationTokenSource();
    var parentTask = MonitorParentProcessAsync(parentProcessId.Value, lifetime);
    var runner = new ProcessCommandRunner();
    var store = new DevwtStateStore(stateRoot);
    var workerEndpoint = new DevwtGatewayWorkerEndpoint(
        IPAddress.Parse(ip).ToString().ToUpperInvariant(),
        port.Value);
    var processSource = new WindowsProcessObservationSource(runner, runtimeSettingsProvider: store.LoadRuntimeSettings);
    await using var historySink = new DevwtControlConnectionHistorySink(
        new DevwtNamedPipeControlClient(connectTimeout: TimeSpan.FromMilliseconds(500)));
    var gateway = new DevwtGatewayServer(
        new DevwtGatewayRouteSnapshotStore(store.StateRoot),
        new WindowsActiveTcpConnectionSource(runner),
        processSource,
        store,
        new WindowsActiveUdpEndpointSource(runner),
        connectionHistory: historySink,
        endpointFilter: endpoint => DevwtGatewayWorkerEndpoint.FromListenEndpoint(endpoint) == workerEndpoint,
        requireEndpointOwnership: true,
        certificateStore: new DevwtGatewayCertificateStore(stateRoot));
    try
    {
        await gateway.RunAsync(lifetime.Token);
        return 0;
    }
    finally
    {
        await lifetime.CancelAsync();
        await parentTask;
    }
}

static async Task MonitorParentProcessAsync(int parentProcessId, CancellationTokenSource lifetime)
{
    try
    {
        using var parent = Process.GetProcessById(parentProcessId);
        await parent.WaitForExitAsync(lifetime.Token);
        await lifetime.CancelAsync();
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        await lifetime.CancelAsync();
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
    {
    }
}

static int RunServiceInstall(IReadOnlyList<string> args)
{
    var confirmed = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);
    if (!confirmed)
    {
        Console.WriteLine("service install requires --yes from an elevated shell");
        return 2;
    }

    var exe = ResolveServiceExecutablePath();
    var binPath = $"\"{exe}\" service run";
    var runner = new ProcessCommandRunner();
    var create = runner.Run([
        "sc.exe",
        "create",
        DevwtServiceHost.ServiceName,
        "binPath=",
        binPath,
        "start=",
        "auto",
        "DisplayName=",
        "DevWT Service"
    ]);
    var createText = string.Concat(create.Output, create.Error);
    var serviceAlreadyExists = create.ExitCode != 0 && createText.Contains("1073", StringComparison.Ordinal);
    if (create.ExitCode != 0 && !serviceAlreadyExists)
    {
        Console.Write(createText);
        return create.ExitCode;
    }

    var serviceOutput = createText;
    if (serviceAlreadyExists)
    {
        var config = runner.Run([
            "sc.exe",
            "config",
            DevwtServiceHost.ServiceName,
            "binPath=",
            binPath,
            "start=",
            "auto",
            "DisplayName=",
            "DevWT Service"
        ]);
        serviceOutput = string.Concat(config.Output, config.Error);
        if (config.ExitCode != 0)
        {
            Console.Write(serviceOutput);
            return config.ExitCode;
        }
    }

    var start = runner.Run(["sc.exe", "start", DevwtServiceHost.ServiceName]);
    Console.Write(string.Concat(serviceOutput, start.Output, start.Error));
    return start.ExitCode == 0 || string.Concat(start.Output, start.Error).Contains("1056", StringComparison.Ordinal) ? 0 : start.ExitCode;
}

static int RunServiceUninstall(IReadOnlyList<string> args)
{
    var confirmed = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);
    if (!confirmed)
    {
        Console.WriteLine("service uninstall requires --yes from an elevated shell");
        return 2;
    }

    var runner = new ProcessCommandRunner();
    var stop = runner.Run(["sc.exe", "stop", DevwtServiceHost.ServiceName]);
    var delete = runner.Run(["sc.exe", "delete", DevwtServiceHost.ServiceName]);
    Console.Write(string.Concat(stop.Output, stop.Error, delete.Output, delete.Error));
    return delete.ExitCode;
}

static int RunServiceRestart()
{
    var stop = RunScCommand("stop", suppressOutput: true);
    Thread.Sleep(TimeSpan.FromSeconds(1));
    var start = RunScCommand("start");
    return start == 0 ? 0 : start;
}

static int RunServiceStart()
{
    var runner = new ProcessCommandRunner();
    var query = runner.Run(["sc.exe", "query", DevwtServiceHost.ServiceName]);
    var queryText = string.Concat(query.Output, query.Error);
    if (query.ExitCode == 0 && queryText.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("DevWTService is already running.");
        return 0;
    }

    var start = runner.Run(["sc.exe", "start", DevwtServiceHost.ServiceName]);
    var startText = string.Concat(start.Output, start.Error);
    if (startText.Contains("1056", StringComparison.Ordinal))
    {
        Console.WriteLine("DevWTService is already running.");
        return 0;
    }

    Console.Write(startText);
    return start.ExitCode;
}

static int RunScCommand(string command, bool suppressOutput = false)
{
    var runner = new ProcessCommandRunner();
    var result = runner.Run(["sc.exe", command, DevwtServiceHost.ServiceName]);
    if (!suppressOutput)
    {
        Console.Write(string.Concat(result.Output, result.Error));
    }

    return result.ExitCode;
}

static UiOptions ParseUiOptions(IReadOnlyList<string> args)
{
    string? stateRoot = null;
    var listen = new Uri("http://127.0.0.1:17776/");
    for (var index = 0; index < args.Count; index++)
    {
        var option = args[index];
        switch (option)
        {
            case "--state-root":
                stateRoot = RequiredValue(args, ref index, option);
                break;
            case "--listen":
                listen = ParseHttpUri(RequiredValue(args, ref index, option), option);
                break;
            default:
                Console.WriteLine($"Unknown ui option: {option}");
                Environment.ExitCode = 2;
                break;
        }
    }

    return new UiOptions(stateRoot, listen);
}

static Uri ParseHttpUri(string value, string option)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
    {
        throw new ArgumentException($"{option} must be an absolute http URI");
    }

    return uri.ToString().EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri + "/");
}

static string RequiredValue(IReadOnlyList<string> args, ref int index, string option)
{
    if (index + 1 >= args.Count)
    {
        throw new ArgumentException($"{option} requires a value");
    }

    return args[++index];
}

static int ServiceHelp(string command)
{
    Console.WriteLine($"Unknown service command: {command}");
    return 2;
}

static string ResolveServiceExecutablePath()
{
    var appHost = Path.Combine(AppContext.BaseDirectory, "Devwt.Cli.exe");
    return File.Exists(appHost)
        ? appHost
        : Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve DevWT executable path.");
}

internal sealed record UiOptions(string? StateRoot, Uri ListenUri);
