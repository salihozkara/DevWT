using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Net;

namespace Devwt.Core;

public sealed record LinkedRepository(string Name, string Path, string AbsolutePath);

public sealed record DevwtRepository(
    string Id,
    string Name,
    string RootPath,
    string GitCommonDir,
    IReadOnlyList<LinkedRepository> LinkedRepositories);

public sealed record DevwtRepositoryState(IReadOnlyList<DevwtRepository> Repositories)
{
    public static DevwtRepositoryState Empty { get; } = new([]);
}

[JsonConverter(typeof(JsonStringEnumConverter<DevwtContextStatus>))]
public enum DevwtContextStatus
{
    Active,
    Paused
}

public sealed record DevwtContext(
    string Id,
    string RepositoryId,
    string Name,
    string WorktreeRootPath,
    string GitRef,
    string AssignedIp,
    string RuntimeName,
    DevwtContextStatus Status,
    int AssignedPortBase = 0,
    string? Description = null);

public sealed record DevwtContextState(IReadOnlyList<DevwtContext> Contexts)
{
    public static DevwtContextState Empty { get; } = new([]);
}

public static class DevwtPortShift
{
    public const string LoopbackAddress = "127.0.0.1";

    public static int AssignedPortBaseFor(string contextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(contextId.ToLowerInvariant()));
        var value = BitConverter.ToUInt32(hash, 0);
        return 10_000 + (int)(value % 50_000);
    }

    public static DevwtContext Normalize(DevwtContext context)
    {
        var portBase = context.AssignedPortBase > 0
            ? context.AssignedPortBase
            : AssignedPortBaseFor(context.Id);
        return context with
        {
            AssignedIp = LoopbackAddress,
            AssignedPortBase = portBase
        };
    }

    public static DevwtContextState Normalize(DevwtContextState state) =>
        new(state.Contexts.Select(Normalize).ToArray());
}

public sealed record DevwtLinkMap(string LinkedRepositoryName, string SourceWorktreePath, string TargetWorktreePath);

public sealed record DevwtActiveTarget(string ContextId, int Port, string Scheme);

public sealed record DevwtActiveTargetInput(string ContextId, int Port, string Scheme);

[JsonConverter(typeof(JsonStringEnumConverter<DevwtActiveTargetMode>))]
public enum DevwtActiveTargetMode
{
    PerPort,
    GlobalContext
}

public sealed record DevwtPortActiveTarget(string ContextId, int Port);

[JsonConverter(typeof(JsonStringEnumConverter<DevwtHttpsProxyMode>))]
public enum DevwtHttpsProxyMode
{
    Auto,
    Inspect,
    Tunnel,
    Raw
}

public sealed record DevwtHttpsProxyEndpoint(string Ip, int Port, DevwtHttpsProxyMode Mode);

public sealed record DevwtBrowserActiveTarget(string BrowserKey, string ContextId, int Port, string Scheme);

public sealed record DevwtProcessTarget(int ProcessId, string ContextId);

public sealed record DevwtApplicationTarget(string ApplicationKey, string ContextId, int Port, string Scheme);

public sealed record DevwtApplicationContextTarget(string ApplicationKey, string ContextId);

public sealed record DevwtProcessPortTarget(int ProcessId, string ContextId, int Port, string Scheme);

public sealed record DevwtSessionContextTarget(string SessionId, string ContextId);

public sealed record DevwtSessionPortTarget(string SessionId, string ContextId, int Port, string Scheme);

public sealed record DevwtRoutingState(
    IReadOnlyList<DevwtLinkMap> ExplicitLinkMaps,
    DevwtActiveTarget? ActiveTarget)
{
    public DevwtActiveTargetMode ActiveTargetMode { get; init; } = DevwtActiveTargetMode.PerPort;

    public string? GlobalActiveContextId { get; init; }

    public IReadOnlyList<DevwtPortActiveTarget> PortActiveTargets { get; init; } = [];

    public IReadOnlyList<DevwtBrowserActiveTarget> BrowserActiveTargets { get; init; } = [];

    public IReadOnlyList<DevwtProcessTarget> ProcessTargets { get; init; } = [];

    public IReadOnlyList<DevwtApplicationTarget> ApplicationTargets { get; init; } = [];

    public IReadOnlyList<DevwtApplicationContextTarget> ApplicationContextTargets { get; init; } = [];

    public IReadOnlyList<DevwtProcessPortTarget> ProcessPortTargets { get; init; } = [];

    public IReadOnlyList<DevwtSessionContextTarget> SessionContextTargets { get; init; } = [];

    public IReadOnlyList<DevwtSessionPortTarget> SessionPortTargets { get; init; } = [];

    public IReadOnlyList<DevwtHttpsProxyEndpoint> HttpsProxyEndpoints { get; init; } = [];

    [JsonConstructor]
    public DevwtRoutingState(
        IReadOnlyList<DevwtLinkMap> ExplicitLinkMaps,
        DevwtActiveTarget? ActiveTarget,
        IReadOnlyList<DevwtBrowserActiveTarget>? BrowserActiveTargets = null,
        IReadOnlyList<DevwtProcessTarget>? ProcessTargets = null,
        IReadOnlyList<DevwtApplicationTarget>? ApplicationTargets = null,
        DevwtActiveTargetMode ActiveTargetMode = DevwtActiveTargetMode.PerPort,
        string? GlobalActiveContextId = null,
        IReadOnlyList<DevwtPortActiveTarget>? PortActiveTargets = null,
        IReadOnlyList<DevwtApplicationContextTarget>? ApplicationContextTargets = null,
        IReadOnlyList<DevwtProcessPortTarget>? ProcessPortTargets = null,
        IReadOnlyList<DevwtSessionContextTarget>? SessionContextTargets = null,
        IReadOnlyList<DevwtSessionPortTarget>? SessionPortTargets = null,
        IReadOnlyList<DevwtHttpsProxyEndpoint>? HttpsProxyEndpoints = null)
        : this(ExplicitLinkMaps, ActiveTarget)
    {
        this.ActiveTargetMode = ActiveTargetMode;
        this.GlobalActiveContextId = GlobalActiveContextId;
        this.PortActiveTargets = PortActiveTargets ?? [];
        this.BrowserActiveTargets = BrowserActiveTargets ?? [];
        this.ProcessTargets = ProcessTargets ?? [];
        this.ApplicationTargets = ApplicationTargets ?? [];
        this.ApplicationContextTargets = ApplicationContextTargets ?? [];
        this.ProcessPortTargets = ProcessPortTargets ?? [];
        this.SessionContextTargets = SessionContextTargets ?? [];
        this.SessionPortTargets = SessionPortTargets ?? [];
        this.HttpsProxyEndpoints = HttpsProxyEndpoints ?? [];
    }

    public static DevwtRoutingState Empty { get; } = new([], null, [], [], []);

    public static DevwtRoutingState Normalize(DevwtRoutingState state)
    {
        var targets = state.PortActiveTargets
            .Where(target => target.Port is > 0 and <= 65535)
            .GroupBy(target => target.Port)
            .Select(group => group.Last())
            .ToList();
        if (state.ActiveTarget is { } legacy
            && legacy.Port is > 0 and <= 65535
            && targets.All(target => target.Port != legacy.Port))
        {
            targets.Add(new DevwtPortActiveTarget(legacy.ContextId, legacy.Port));
        }

        var applicationContextTargets = state.ApplicationContextTargets
            .Where(target => !string.IsNullOrWhiteSpace(target.ApplicationKey)
                && !string.IsNullOrWhiteSpace(target.ContextId))
            .Select(target => target with
            {
                ApplicationKey = DevwtBrowserKey.Normalize(target.ApplicationKey),
                ContextId = target.ContextId.Trim()
            })
            .GroupBy(target => target.ApplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(target => target.ApplicationKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var processPortTargets = state.ProcessPortTargets
            .Where(target => target.ProcessId > 0
                && target.Port is > 0 and <= 65535
                && !string.IsNullOrWhiteSpace(target.ContextId))
            .Select(target => target with
            {
                ContextId = target.ContextId.Trim(),
                Scheme = NormalizeTargetScheme(target.Scheme)
            })
            .GroupBy(target => (target.ProcessId, target.Port))
            .Select(group => group.Last())
            .OrderBy(target => target.ProcessId)
            .ThenBy(target => target.Port)
            .ToArray();
        var sessionContextTargets = state.SessionContextTargets
            .Where(target => !string.IsNullOrWhiteSpace(target.SessionId)
                && !string.IsNullOrWhiteSpace(target.ContextId))
            .Select(target => target with
            {
                SessionId = target.SessionId.Trim(),
                ContextId = target.ContextId.Trim()
            })
            .GroupBy(target => target.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(target => target.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sessionPortTargets = state.SessionPortTargets
            .Where(target => !string.IsNullOrWhiteSpace(target.SessionId)
                && target.Port is > 0 and <= 65535
                && !string.IsNullOrWhiteSpace(target.ContextId))
            .Select(target => target with
            {
                SessionId = target.SessionId.Trim(),
                ContextId = target.ContextId.Trim(),
                Scheme = NormalizeTargetScheme(target.Scheme)
            })
            .GroupBy(target => (target.SessionId.ToUpperInvariant(), target.Port))
            .Select(group => group.Last())
            .OrderBy(target => target.SessionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.Port)
            .ToArray();
        var httpsProxyEndpoints = state.HttpsProxyEndpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Ip)
                && endpoint.Port is > 0 and <= 65535)
            .Select(endpoint => endpoint with { Ip = NormalizeEndpointIp(endpoint.Ip) })
            .GroupBy(endpoint => (endpoint.Ip.ToUpperInvariant(), endpoint.Port))
            .Select(group => group.Last())
            .OrderBy(endpoint => endpoint.Ip, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Port)
            .ToArray();

        return state with
        {
            ActiveTarget = null,
            ActiveTargetMode = state.ActiveTarget is null
                ? state.ActiveTargetMode
                : DevwtActiveTargetMode.PerPort,
            PortActiveTargets = targets.OrderBy(target => target.Port).ToArray(),
            ApplicationContextTargets = applicationContextTargets,
            ProcessPortTargets = processPortTargets,
            SessionContextTargets = sessionContextTargets,
            SessionPortTargets = sessionPortTargets,
            HttpsProxyEndpoints = httpsProxyEndpoints
        };
    }

    private static string NormalizeTargetScheme(string? scheme) =>
        scheme?.Trim().ToLowerInvariant() is "http" or "https" ? scheme.Trim().ToLowerInvariant() : "auto";

    private static string NormalizeEndpointIp(string ip) =>
        IPAddress.TryParse(ip.Trim(), out var address) ? address.ToString() : ip.Trim();
}

public static class DevwtBrowserKey
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim()
            .Trim('"')
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.IsPathRooted(normalized) ? DevwtPath.Normalize(normalized) : normalized;
    }

    public static bool Equals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
}

public sealed record GitWorktreeInfo(string RootPath, string RefName);

public sealed record GitRepositoryInfo(
    string RootPath,
    string GitCommonDir,
    IReadOnlyList<GitWorktreeInfo> Worktrees);

public interface IGitInspector
{
    GitRepositoryInfo InspectRepository(string workingDirectory);

    string EnsureHooksDirectory(string workingDirectory, GitRepositoryInfo repository);
}

public sealed record ProcessObservation(
    int ProcessId,
    int? ParentProcessId,
    string? ImagePath,
    string? CommandLine,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? StartTime = null);

public sealed record AddRepositoryRequest(
    string WorkingDirectory,
    string? Name,
    IReadOnlyList<LinkedRepositoryInput> LinkedRepositories);

public sealed record LinkedRepositoryInput(string Name, string Path);

public sealed record AddRepositoryResult(DevwtRepository Repository, IReadOnlyList<DevwtContext> Contexts);

public sealed record RemoveRepositoryResult(int RemovedRepositories, int RemovedContexts, IReadOnlyList<string> Warnings);

public sealed record DevwtRuntimeStatus(bool IsAvailable, string Message);

public sealed record DevwtIdeWatch(
    string Name,
    string? ImagePath = null,
    string? AppId = null,
    string? PackageFamilyName = null);

[JsonConverter(typeof(JsonStringEnumConverter<DevwtSessionIdentityKind>))]
public enum DevwtSessionIdentityKind
{
    EnvironmentVariable,
    Process,
    RootProcess,
    CommandLineRegex
}

public sealed record DevwtSessionMatch(
    string? ProcessName = null,
    string? ImagePathContains = null,
    string? CommandLineContains = null,
    string? EnvironmentVariable = null);

public sealed record DevwtSessionIdentity(
    DevwtSessionIdentityKind Kind,
    string? Value = null,
    string Prefix = "");

public sealed record DevwtSessionRule(
    string Name,
    DevwtSessionMatch Match,
    DevwtSessionIdentity Identity);

[JsonConverter(typeof(JsonStringEnumConverter<DevwtBrowserMissingPortPolicyMode>))]
public enum DevwtBrowserMissingPortPolicyMode
{
    Automatic,
    Disabled,
    Redirect
}

public sealed record DevwtBrowserMissingPortPolicy(
    string ContextId,
    int Port,
    DevwtBrowserMissingPortPolicyMode Mode,
    string? TargetContextId = null);

public sealed record DevwtRuntimeSettings
{
    [JsonConstructor]
    public DevwtRuntimeSettings(
        IReadOnlyList<DevwtIdeWatch>? IdeWatches = null,
        IReadOnlyList<DevwtSessionRule>? SessionRules = null,
        bool BrowserFallbackOnMissingPort = false,
        IReadOnlyList<DevwtBrowserMissingPortPolicy>? BrowserMissingPortPolicies = null)
    {
        this.IdeWatches = IdeWatches ?? [];
        this.SessionRules = SessionRules ?? [];
        this.BrowserFallbackOnMissingPort = BrowserFallbackOnMissingPort;
        this.BrowserMissingPortPolicies = BrowserMissingPortPolicies ?? [];
    }

    public IReadOnlyList<DevwtIdeWatch> IdeWatches { get; init; }

    public IReadOnlyList<DevwtSessionRule> SessionRules { get; init; }

    public bool BrowserFallbackOnMissingPort { get; init; }

    public IReadOnlyList<DevwtBrowserMissingPortPolicy> BrowserMissingPortPolicies { get; init; }

    public static DevwtRuntimeSettings Empty { get; } = new([], [], false, []);
}
