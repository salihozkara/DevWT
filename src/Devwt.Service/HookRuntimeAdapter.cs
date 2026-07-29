using Devwt.Core;

namespace Devwt.Service;

public sealed record HookRuntimeCommand(string ExecutablePath, IReadOnlyList<string> Arguments);

public static class HookRuntimeContextMap
{
    public const string FileName = "hook-contexts.tsv";

    public static string ResolvePath(string stateRoot) => Path.Combine(stateRoot, FileName);

    public static void Write(string mapPath, IReadOnlyList<DevwtContext> contexts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapPath);
        Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        var activeContexts = contexts
            .Where(context => context.Status == DevwtContextStatus.Active)
            .Select(DevwtPortShift.Normalize)
            .OrderByDescending(context => DevwtPath.Normalize(context.WorktreeRootPath).Length)
            .ThenBy(context => context.Id, StringComparer.OrdinalIgnoreCase)
            .Select(context =>
            {
                var root = DevwtPath.Normalize(context.WorktreeRootPath);
                return $"{root}\t{context.Id}\t{DevwtPortShift.LoopbackAddress}\t{DevwtPortShift.LoopbackAddress}\t{context.AssignedPortBase}";
            });

        var temp = mapPath + ".tmp";
        File.WriteAllLines(temp, activeContexts);
        if (File.Exists(mapPath))
        {
            File.Replace(temp, mapPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, mapPath);
        }
    }
}

public sealed record HookPortBinding(
    string ContextId,
    string OriginalIp,
    int OriginalPort,
    string TargetIp,
    int TargetPort,
    int ProcessId,
    GatewayRouteProtocol Protocol = GatewayRouteProtocol.Tcp);

public static class HookPortBindingMap
{
    public const string FileName = "hook-port-bindings.tsv";

    public static string ResolvePath(string stateRoot) => Path.Combine(stateRoot, FileName);

    public static IReadOnlyList<HookPortBinding> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var bindings = new List<HookPortBinding>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 5)
            {
                continue;
            }

            if (parts.Length >= 6
                && int.TryParse(parts[2], out var originalPort)
                && int.TryParse(parts[4], out var targetPort)
                && int.TryParse(parts[5], out var processId))
            {
                if (string.IsNullOrWhiteSpace(parts[0])
                    || string.IsNullOrWhiteSpace(parts[1])
                    || string.IsNullOrWhiteSpace(parts[3])
                    || originalPort <= 0
                    || targetPort <= 0
                    || processId <= 0)
                {
                    continue;
                }

                var protocol = parts.Length >= 7 && parts[6].Equals("udp", StringComparison.OrdinalIgnoreCase)
                    ? GatewayRouteProtocol.Udp
                    : GatewayRouteProtocol.Tcp;
                bindings.Add(new HookPortBinding(parts[0], parts[1], originalPort, parts[3], targetPort, processId, protocol));
                continue;
            }

            if (!int.TryParse(parts[1], out originalPort)
                || !int.TryParse(parts[3], out targetPort)
                || !int.TryParse(parts[4], out processId))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(parts[0])
                || string.IsNullOrWhiteSpace(parts[2])
                || originalPort <= 0
                || targetPort <= 0
                || processId <= 0)
            {
                continue;
            }

            var legacyProtocol = parts.Length >= 6 && parts[5].Equals("udp", StringComparison.OrdinalIgnoreCase)
                ? GatewayRouteProtocol.Udp
                : GatewayRouteProtocol.Tcp;
            bindings.Add(new HookPortBinding(
                parts[0],
                DevwtPortShift.LoopbackAddress,
                originalPort,
                parts[2],
                targetPort,
                processId,
                legacyProtocol));
        }

        return bindings;
    }
}

public static class HookRuntimeCommandPlanner
{
    public static HookRuntimeCommand PlanFolderWatcher(
        string watcherPath,
        string hookDllPath,
        IReadOnlyList<DevwtContext> contexts,
        IReadOnlyList<DevwtIdeWatch>? ideWatches = null,
        string? mapFilePath = null,
        string? portBindingsPath = null,
        int pollMs = 1000,
        string? logPath = null)
    {
        var activeContexts = contexts
            .Where(context => context.Status == DevwtContextStatus.Active)
            .OrderByDescending(context => context.WorktreeRootPath.Length)
            .ToArray();
        var activeIdeWatches = (ideWatches ?? [])
            .Where(watch => !string.IsNullOrWhiteSpace(watch.ImagePath)
                || !string.IsNullOrWhiteSpace(watch.PackageFamilyName))
            .OrderBy(watch => watch.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (activeContexts.Length == 0 && activeIdeWatches.Length == 0)
        {
            throw new InvalidOperationException("At least one active context or IDE watch is required to start the hook runtime watcher.");
        }

        var args = new List<string>
        {
            "--dll",
            hookDllPath,
            "--process-events",
            "--poll-ms",
            pollMs.ToString(),
            "--duration-ms",
            "0"
        };
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            args.Add("--log");
            args.Add(logPath);
        }

        if (!string.IsNullOrWhiteSpace(mapFilePath))
        {
            args.Add("--map-file");
            args.Add(mapFilePath);
        }

        if (!string.IsNullOrWhiteSpace(portBindingsPath))
        {
            args.Add("--port-bindings-file");
            args.Add(portBindingsPath);
        }

        foreach (var context in activeContexts)
        {
            var normalized = DevwtPortShift.Normalize(context);
            args.Add("--map");
            args.Add($"{normalized.WorktreeRootPath}={normalized.Id},{DevwtPortShift.LoopbackAddress},{DevwtPortShift.LoopbackAddress},{normalized.AssignedPortBase}");
        }

        foreach (var watch in activeIdeWatches)
        {
            if (!string.IsNullOrWhiteSpace(watch.ImagePath))
            {
                args.Add("--children-only-image");
                args.Add(watch.ImagePath);
            }

            if (!string.IsNullOrWhiteSpace(watch.PackageFamilyName))
            {
                args.Add("--children-only-package-family");
                args.Add(watch.PackageFamilyName);
            }
        }

        return new HookRuntimeCommand(watcherPath, args);
    }
}

public interface IHookRuntimeConfigurator
{
    void Configure(DevwtRepository repository, DevwtContext context);

    void Remove(DevwtContext context);
}

public sealed class HookRuntimeConfigurator : IHookRuntimeConfigurator
{
    public void Configure(DevwtRepository repository, DevwtContext context)
    {
    }

    public void Remove(DevwtContext context)
    {
    }
}
