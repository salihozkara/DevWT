namespace Devwt.Core;

public sealed record DevwtRuntimeLaunchPlan(string ExecutablePath, IReadOnlyList<string> Arguments)
{
    public static DevwtRuntimeLaunchPlan Create(
        string launcherPath,
        string hookDllPath,
        string? bindIp,
        string? connectIp,
        bool childrenOnly,
        string program,
        IReadOnlyList<string> arguments,
        string? contextId = null,
        int? portOffset = null,
        string? portBindingsPath = null,
        string? commandPath = null,
        string? commandPathExt = null,
        string? comSpec = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookDllPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        if (!childrenOnly && string.IsNullOrWhiteSpace(contextId))
        {
            throw new ArgumentException("contextId is required unless childrenOnly is true.", nameof(contextId));
        }

        if (!childrenOnly && portOffset is null)
        {
            throw new ArgumentException("portOffset is required unless childrenOnly is true.", nameof(portOffset));
        }

        var launcherArguments = new List<string>();
        if (childrenOnly)
        {
            launcherArguments.Add("--children-only");
        }

        if (!string.IsNullOrWhiteSpace(contextId))
        {
            launcherArguments.Add("--context-id");
            launcherArguments.Add(contextId);
        }

        if (!string.IsNullOrWhiteSpace(bindIp))
        {
            launcherArguments.Add("--bind-ip");
            launcherArguments.Add(bindIp);
        }

        if (!string.IsNullOrWhiteSpace(connectIp))
        {
            launcherArguments.Add("--connect-ip");
            launcherArguments.Add(connectIp);
        }

        if (portOffset is int offset)
        {
            launcherArguments.Add("--port-offset");
            launcherArguments.Add(offset.ToString());
        }

        if (!string.IsNullOrWhiteSpace(portBindingsPath))
        {
            launcherArguments.Add("--port-bindings-file");
            launcherArguments.Add(portBindingsPath);
        }

        launcherArguments.Add("--dll");
        launcherArguments.Add(hookDllPath);
        var resolved = DevwtWindowsCommandResolver.Resolve(program, arguments, commandPath, commandPathExt, comSpec);
        launcherArguments.Add("--");
        launcherArguments.Add(resolved.Program);
        launcherArguments.AddRange(resolved.Arguments);

        return new DevwtRuntimeLaunchPlan(launcherPath, launcherArguments);
    }
}
