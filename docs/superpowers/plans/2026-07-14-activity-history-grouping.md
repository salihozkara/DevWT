# Activity History Grouping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a bounded `Image -> Process -> Session -> History` activity view and configurable all-port/per-port defaults for image, process, and session scopes while preserving the chronological view.

**Architecture:** Extend the backward-compatible routing state with four focused target records, resolve the new scopes in the gateway without adding a second activity index, and attach the already-derived session ID to each bounded history entry. The Web UI groups at most 200 serialized entries at render time and sends narrow control actions through the existing ASP.NET host.

**Tech Stack:** .NET 10, C# records and `System.Text.Json`, ASP.NET Core/SignalR, built-in HTML/CSS/JavaScript, xUnit, PowerShell installer tooling.

## Global Constraints

- Preserve existing `routing.json` files; every new collection defaults to empty.
- Keep `DevwtConnectionHistory` at 200 entries by default.
- Cap process identity and last-context caches at 512 entries and evict least-recently-seen entries.
- Do not add server-side activity grouping, process polling, frontend dependencies, or persistent activity history.
- Per-port targets override all-port targets only within the same image/process/session scope.
- TCP and UDP share target records by numeric port; route candidate matching still checks protocol and listen IP.
- Browser request headers, browser-scoped targets, and self-listener routing retain their existing precedence.
- Do not install or uninstall DevWT on the main machine; produce the installer zip only after verification.
- The working tree contains pre-existing source changes. Do not stage or commit production files unless their complete diff has been reviewed as belonging to this feature.

---

### Task 1: Persist Scoped Routing Targets

**Files:**
- Modify: `src/Devwt.Core/DevwtModels.cs`
- Test: `tests/Devwt.Core.Tests/HookCoreStateTests.cs`

**Interfaces:**
- Produces: `DevwtApplicationContextTarget`, `DevwtProcessPortTarget`, `DevwtSessionContextTarget`, and `DevwtSessionPortTarget`.
- Produces: `DevwtRoutingState.ApplicationContextTargets`, `ProcessPortTargets`, `SessionContextTargets`, and `SessionPortTargets`.
- Preserves: existing positional `DevwtRoutingState` constructor arguments and empty defaults for old JSON.

- [ ] **Step 1: Write failing persistence and normalization tests**

Add tests that round-trip all four collections and verify last-write-wins normalization:

```csharp
[Fact]
public void Routing_state_round_trips_scoped_targets()
{
    using var temp = new TempDirectory();
    var store = new DevwtStateStore(temp.Path);
    var state = new DevwtRoutingState([], null)
    {
        ApplicationContextTargets = [new(@"C:\tools\codex.exe", "ctx-a")],
        ProcessPortTargets = [new(1200, "ctx-b", 44334, "auto")],
        SessionContextTargets = [new("codex:thread-a", "ctx-a")],
        SessionPortTargets = [new("codex:thread-a", "ctx-b", 44334, "auto")]
    };

    store.SaveRouting(state);
    var loaded = store.LoadRouting();

    Assert.Equal(state.ApplicationContextTargets, loaded.ApplicationContextTargets);
    Assert.Equal(state.ProcessPortTargets, loaded.ProcessPortTargets);
    Assert.Equal(state.SessionContextTargets, loaded.SessionContextTargets);
    Assert.Equal(state.SessionPortTargets, loaded.SessionPortTargets);
}

[Fact]
public void Routing_state_normalizes_duplicate_scoped_targets_by_logical_key()
{
    var normalized = DevwtRoutingState.Normalize(new DevwtRoutingState([], null)
    {
        ApplicationContextTargets = [
            new(@"C:\Tools\Codex.exe", "ctx-a"),
            new(@"c:\tools\codex.exe", "ctx-b")
        ],
        ProcessPortTargets = [
            new(1200, "ctx-a", 44334, "auto"),
            new(1200, "ctx-b", 44334, "https")
        ],
        SessionContextTargets = [
            new("codex:thread-a", "ctx-a"),
            new("CODEX:THREAD-A", "ctx-b")
        ],
        SessionPortTargets = [
            new("codex:thread-a", "ctx-a", 44334, "auto"),
            new("CODEX:THREAD-A", "ctx-b", 44334, "https")
        ]
    });

    Assert.Equal("ctx-b", Assert.Single(normalized.ApplicationContextTargets).ContextId);
    Assert.Equal("ctx-b", Assert.Single(normalized.ProcessPortTargets).ContextId);
    Assert.Equal("ctx-b", Assert.Single(normalized.SessionContextTargets).ContextId);
    Assert.Equal("ctx-b", Assert.Single(normalized.SessionPortTargets).ContextId);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Core.Tests\Devwt.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~Routing_state_round_trips_scoped_targets|FullyQualifiedName~Routing_state_normalizes_duplicate_scoped_targets"
```

Expected: compilation fails because the four target records and state properties do not exist.

- [ ] **Step 3: Add the target records and optional state collections**

Add these records next to the existing target records:

```csharp
public sealed record DevwtApplicationContextTarget(string ApplicationKey, string ContextId);

public sealed record DevwtProcessPortTarget(int ProcessId, string ContextId, int Port, string Scheme);

public sealed record DevwtSessionContextTarget(string SessionId, string ContextId);

public sealed record DevwtSessionPortTarget(string SessionId, string ContextId, int Port, string Scheme);
```

Append nullable constructor parameters after `PortActiveTargets` and expose initialized properties:

```csharp
public IReadOnlyList<DevwtApplicationContextTarget> ApplicationContextTargets { get; init; } = [];
public IReadOnlyList<DevwtProcessPortTarget> ProcessPortTargets { get; init; } = [];
public IReadOnlyList<DevwtSessionContextTarget> SessionContextTargets { get; init; } = [];
public IReadOnlyList<DevwtSessionPortTarget> SessionPortTargets { get; init; } = [];
```

Normalize keys with `Trim()`, compare application/session keys case-insensitively, reject non-positive PIDs and ports outside `1..65535`, keep the last duplicate, and sort by key/PID then port. Reuse the existing scheme values `auto`, `http`, and `https`; invalid persisted schemes normalize to `auto` rather than making old state unreadable.

- [ ] **Step 4: Run Core tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\Devwt.Core.Tests\Devwt.Core.Tests.csproj --no-restore
```

Expected: all Core tests pass.

- [ ] **Step 5: Review the task diff without staging unrelated work**

Run:

```powershell
git diff --check -- src/Devwt.Core/DevwtModels.cs tests/Devwt.Core.Tests/HookCoreStateTests.cs
git diff -- src/Devwt.Core/DevwtModels.cs tests/Devwt.Core.Tests/HookCoreStateTests.cs
```

Expected: no whitespace errors; the new constructor parameters are appended and old JSON remains valid.

---

### Task 2: Resolve Process, Session, And Image Targets

**Files:**
- Modify: `src/Devwt.Service/DevwtRuntimeServers.cs`
- Modify: `src/Devwt.Service/GatewayRouting.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Consumes: the four target collections from Task 1.
- Changes: `ProcessContextTargetResolver.ResolveConfiguredTarget(int processId, int port, DevwtContextState contexts, IReadOnlyList<ProcessObservation> processes, DevwtRoutingState routing)`.
- Produces: `GatewayRouteTable.ResolveSessionTarget(int port, string? sessionId, GatewayRouteProtocol protocol = GatewayRouteProtocol.Tcp, string? listenIp = null)`.
- Extends: `GatewayRouteTable.ResolveApplicationTarget` to check image-port then image-wide targets.

- [ ] **Step 1: Write failing resolver precedence tests**

Add focused tests for process ancestor overrides, session overrides, and image-wide fallback:

```csharp
[Fact]
public void Process_port_target_overrides_process_wide_target_through_parent_chain()
{
    var contexts = new DevwtContextState([
        Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
        Context("ctx-b", "repo-a", @"C:\work\b", "127.0.0.1")
    ]);
    var processes = new[] {
        new ProcessObservation(100, null, @"C:\tools\codex.exe", null, null),
        new ProcessObservation(200, 100, @"C:\tools\node.exe", null, null)
    };
    var routing = new DevwtRoutingState([], null, ProcessTargets: [new(100, "ctx-a")])
    {
        ProcessPortTargets = [new(100, "ctx-b", 44334, "auto")]
    };

    Assert.Equal("ctx-b", ProcessContextTargetResolver.ResolveConfiguredTarget(200, 44334, contexts, processes, routing));
    Assert.Equal("ctx-a", ProcessContextTargetResolver.ResolveConfiguredTarget(200, 5001, contexts, processes, routing));
}

[Fact]
public void Session_port_target_overrides_session_wide_target()
{
    var table = GatewayRouteTable.FromRoutes(
        [Route("ctx-a", 44334, 24434), Route("ctx-b", 44334, 34434)],
        DevwtRepositoryState.Empty,
        ContextState("ctx-a", "ctx-b"),
        new DevwtRoutingState([], null)
        {
            SessionContextTargets = [new("codex:a", "ctx-a")],
            SessionPortTargets = [new("codex:a", "ctx-b", 44334, "auto")]
        });

    Assert.Equal("ctx-b", table.ResolveSessionTarget(44334, "CODEX:A")!.ContextId);
}

[Fact]
public void Application_target_uses_port_override_then_image_wide_target()
{
    var routing = new DevwtRoutingState([], null, ApplicationTargets: [new(@"C:\tools\codex.exe", "ctx-b", 44334, "auto")])
    {
        ApplicationContextTargets = [new(@"C:\tools\codex.exe", "ctx-a")]
    };
    var table = RouteTableForTwoContextsAndPorts(routing, 44334, 5001);

    Assert.Equal("ctx-b", table.ResolveApplicationTarget(44334, @"c:\TOOLS\codex.exe")!.ContextId);
    Assert.Equal("ctx-a", table.ResolveApplicationTarget(5001, @"c:\TOOLS\codex.exe")!.ContextId);
}
```

Construct the two-port route table explicitly so the test does not depend on a
new helper:

```csharp
var table = GatewayRouteTable.FromRoutes(
    [
        new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 44334, "127.0.0.1", 24434, 10),
        new GatewayRoute("ctx-b", "repo-a", @"C:\work\b", 44334, "127.0.0.1", 34434, 20),
        new GatewayRoute("ctx-a", "repo-a", @"C:\work\a", 5001, "127.0.0.1", 25001, 10),
        new GatewayRoute("ctx-b", "repo-a", @"C:\work\b", 5001, "127.0.0.1", 35001, 20)
    ],
    DevwtRepositoryState.Empty,
    new DevwtContextState([
        Context("ctx-a", "repo-a", @"C:\work\a", "127.0.0.1"),
        Context("ctx-b", "repo-a", @"C:\work\b", "127.0.0.1")
    ]),
    routing);
```

- [ ] **Step 2: Run resolver tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Process_port_target_overrides|FullyQualifiedName~Session_port_target_overrides|FullyQualifiedName~Application_target_uses_port_override"
```

Expected: compilation fails on missing target properties/signatures or assertions fail because only existing all-port/process and image-port targets resolve.

- [ ] **Step 3: Implement scope-local precedence**

In `ProcessContextTargetResolver`, build dictionaries for process-port `(PID, port)` and process-wide PID targets, then walk the caller and ancestor PIDs once:

```csharp
while (current is int currentPid && visited.Add(currentPid))
{
    if (portTargets.TryGetValue((currentPid, port), out var portTarget)
        && activeContextIds.Contains(portTarget.ContextId))
    {
        return portTarget.ContextId;
    }

    if (targetsByProcess.TryGetValue(currentPid, out var target)
        && activeContextIds.Contains(target.ContextId))
    {
        return target.ContextId;
    }

    current = byPid.TryGetValue(currentPid, out var process) ? process.ParentProcessId : null;
}
```

In `GatewayRouteTable`, resolve session and application targets only against `CandidatesForPort(port, protocol, listenIp)`. For both scopes, select the matching per-port record first, then the all-port record. Return `null` when the selected context has no matching candidate so the gateway can continue to weaker signals.

- [ ] **Step 4: Add failing end-to-end TCP and UDP precedence tests**

Create one TCP and one UDP gateway test whose caller process has a session ID. Assert these reasons and targets in order:

```csharp
Assert.Equal("process-context", processDecision.RouteReason);
Assert.Equal("session-default", sessionDecision.RouteReason);
Assert.Equal("session-context", naturalSessionDecision.RouteReason);
Assert.Equal("app-default", imageDecision.RouteReason);
```

For each stronger scope, clear its state before testing the next signal. Include a configured target whose context has no candidate for the requested port and assert resolution continues.

- [ ] **Step 5: Run end-to-end tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Gateway_scoped_target_precedence|FullyQualifiedName~Gateway_udp_scoped_target_precedence"
```

Expected: new session reasons are absent and session/image-wide targets are not selected.

- [ ] **Step 6: Refactor TCP and UDP decision flow to the approved order**

Extend `ClientProcessIdentity` and `GatewayRouteDecision` with `string? SessionId`. Resolve session ID once from the same process snapshot used to obtain the image path. Apply decisions after self-listener routing in this order:

```csharp
configured process port/wide
configured session port/wide
natural same-session route
inferred process context
configured image port/wide
configured gateway fallback
last remembered process context
newest listener
```

Use route reasons `process-context`, `session-default`, `session-context`, `app-default`, `global-active`, `last-process`, and `newest`. Keep request-header and browser routing before self-listener for TCP and preserve the existing UDP limitations.

- [ ] **Step 7: Run Service tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore
```

Expected: all Service tests pass.

---

### Task 3: Record Session Metadata And Bound Process Caches

**Files:**
- Create: `src/Devwt.Service/ProcessRoutingCache.cs`
- Modify: `src/Devwt.Service/DevwtConnectionHistory.cs`
- Modify: `src/Devwt.Service/DevwtRuntimeServers.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Adds: final optional `string? SessionId = null` field to `DevwtConnectionHistoryEntry`.
- Changes internal cache values to carry `LastSeenAt` and session identity.
- Preserves: `DevwtConnectionHistory(int capacity = 200)` and newest-first snapshots.

- [ ] **Step 1: Write failing history and cache-bound tests**

Extend TCP/UDP history assertions with `SessionId`, retain the capacity test, and add a focused cache policy unit:

```csharp
[Fact]
public void Process_routing_cache_evicts_expired_and_least_recent_entries()
{
    var cache = new ProcessRoutingCache(
        capacity: 2,
        identityLifetime: TimeSpan.FromSeconds(5),
        lastContextLifetime: TimeSpan.FromMinutes(5));
    var now = DateTimeOffset.Parse("2026-07-14T10:00:00Z");
    cache.SetIdentity(10, new("a.exe", "a.exe", "session-a"), now);
    cache.SetIdentity(20, new("b.exe", "b.exe", "session-b"), now.AddSeconds(1));
    cache.SetIdentity(30, new("c.exe", "c.exe", "session-c"), now.AddSeconds(2));

    Assert.Null(cache.TryGetIdentity(10, now.AddSeconds(2)));
    Assert.NotNull(cache.TryGetIdentity(20, now.AddSeconds(2)));
    Assert.Null(cache.TryGetIdentity(20, now.AddSeconds(7)));
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Process_routing_cache_evicts|FullyQualifiedName~Connection_history"
```

Expected: `ProcessRoutingCache` and history `SessionId` do not exist.

- [ ] **Step 3: Add a focused bounded cache type**

Create `src/Devwt.Service/ProcessRoutingCache.cs` with this bounded
least-recently-seen implementation:

```csharp
internal sealed record CachedProcessIdentity(string? ProcessImagePath, string? ApplicationKey, string? SessionId);

internal sealed class ProcessRoutingCache
{
    private sealed record Entry<T>(T Value, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt);
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry<CachedProcessIdentity>> _identities = [];
    private readonly Dictionary<int, Entry<string>> _lastContexts = [];
    private readonly int _capacity;
    private readonly TimeSpan _identityLifetime;
    private readonly TimeSpan _lastContextLifetime;

    public ProcessRoutingCache(
        int capacity = 512,
        TimeSpan? identityLifetime = null,
        TimeSpan? lastContextLifetime = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _identityLifetime = identityLifetime ?? TimeSpan.FromSeconds(5);
        _lastContextLifetime = lastContextLifetime ?? TimeSpan.FromMinutes(5);
    }

    public CachedProcessIdentity? TryGetIdentity(int processId, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            if (!_identities.TryGetValue(processId, out var entry)) return null;
            _identities[processId] = entry with { LastSeenAt = now };
            return entry.Value;
        }
    }

    public void SetIdentity(int processId, CachedProcessIdentity value, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            _identities[processId] = new(value, now, now + _identityLifetime);
            Trim(_identities);
        }
    }

    public string? TryGetLastContext(int processId, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            if (!_lastContexts.TryGetValue(processId, out var entry)) return null;
            _lastContexts[processId] = entry with { LastSeenAt = now };
            return entry.Value;
        }
    }

    public void SetLastContext(int processId, string contextId, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            _lastContexts[processId] = new(contextId, now, now + _lastContextLifetime);
            Trim(_lastContexts);
        }
    }

    public void Prune(IReadOnlySet<int> activeProcessIds, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            RemoveInactive(_identities, activeProcessIds);
            RemoveInactive(_lastContexts, activeProcessIds);
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        RemoveWhere(_identities, entry => entry.ExpiresAt <= now);
        RemoveWhere(_lastContexts, entry => entry.ExpiresAt <= now);
    }

    private void Trim<T>(Dictionary<int, Entry<T>> entries)
    {
        foreach (var processId in entries
                     .OrderBy(item => item.Value.LastSeenAt)
                     .Take(Math.Max(0, entries.Count - _capacity))
                     .Select(item => item.Key)
                     .ToArray())
        {
            entries.Remove(processId);
        }
    }

    private static void RemoveInactive<T>(Dictionary<int, Entry<T>> entries, IReadOnlySet<int> activeProcessIds) =>
        RemoveWhere(entries, (_, processId) => !activeProcessIds.Contains(processId));

    private static void RemoveWhere<T>(Dictionary<int, Entry<T>> entries, Func<Entry<T>, bool> predicate) =>
        RemoveWhere(entries, (entry, _) => predicate(entry));

    private static void RemoveWhere<T>(Dictionary<int, Entry<T>> entries, Func<Entry<T>, int, bool> predicate)
    {
        foreach (var processId in entries
                     .Where(item => predicate(item.Value, item.Key))
                     .Select(item => item.Key)
                     .ToArray())
        {
            entries.Remove(processId);
        }
    }
}
```

Replace `_browserKeyCache` and `_lastContextByProcess` with one
`ProcessRoutingCache`. Call `Prune` with the current observed PID set after a
process snapshot is read. Cache `SessionId` in the existing identity value
instead of adding another PID dictionary.

- [ ] **Step 4: Append session ID to history writes**

Append `decision.SessionId` in both `RecordConnection` and `RecordUdpConnection`. Do not store process observations, environment variables, route candidates, or settings in history.

- [ ] **Step 5: Run focused and full Service tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Process_routing_cache_evicts|FullyQualifiedName~Connection_history"
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore
```

Expected: all selected tests and the full Service suite pass.

---

### Task 4: Expose Scoped Target Control Actions

**Files:**
- Modify: `src/Devwt.Service/ControlApi.cs`
- Modify: `src/Devwt.Service/DevwtRuntimeServers.cs`
- Modify: `src/Devwt.Cli/DevwtWebUiAspNetHost.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Adds control payloads for image-wide, process-port, session-wide, and session-port targets.
- Adds `ProcessId` and `SessionId` to `DevwtWebUiAction`.
- Adds Web actions: `set/clear-process-target`, `set/clear-process-port-target`, `set/clear-image-context-target`, `set/clear-session-context-target`, and `set/clear-session-port-target`.
- Preserves existing `set/clear-application-target` for image-port targets.

- [ ] **Step 1: Write failing control-handler isolation tests**

For every new target kind, set two logical keys, replace one, clear one, and assert the unrelated record remains. Representative process-port assertion:

```csharp
handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetProcessTarget,
    ProcessPortTarget: new DevwtProcessPortTarget(1200, "ctx-a", 44334, "auto")));
handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetProcessTarget,
    ProcessPortTarget: new DevwtProcessPortTarget(1200, "ctx-b", 5001, "auto")));
handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetProcessTarget,
    ClearProcessPortTarget: true,
    ProcessId: 1200,
    Port: 44334));

var remaining = Assert.Single(store.LoadRouting().ProcessPortTargets);
Assert.Equal(5001, remaining.Port);
```

Also assert blank keys, unknown contexts, invalid PIDs, invalid ports, and invalid schemes return exit code `2` without changing routing state.

- [ ] **Step 2: Run control tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Control_handler_sets_and_clears_scoped"
```

Expected: new request fields and session operation do not exist.

- [ ] **Step 3: Implement atomic set/clear handlers**

Append request fields rather than reordering the positional record. Normalize application keys with `DevwtBrowserKey.Normalize`; trim session IDs; validate context existence and `1..65535` ports. Replace records only when their logical keys match:

```csharp
ProcessPortTarget: (ProcessId, Port)
ApplicationContextTarget: ApplicationKey, case-insensitive
SessionContextTarget: SessionId, case-insensitive
SessionPortTarget: (SessionId, Port), session ID case-insensitive
```

Save through `DevwtStateStore.SaveRouting` so Task 1 normalization remains the final boundary.

- [ ] **Step 4: Write failing Web action mapping tests**

Extend the Web host action tests to POST each action and inspect persisted routing. Include process-wide and process-port calls for the same PID, plus session-wide and session-port calls for the same session.

- [ ] **Step 5: Run Web action tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Web_ui_maps_scoped_target_actions"
```

Expected: actions return `unknown action` or required action fields are absent.

- [ ] **Step 6: Map narrow Web actions to control requests**

Append `int? ProcessId` and `string? SessionId` to `DevwtWebUiAction`. Map each action explicitly in `ExecuteAction`; do not introduce a generic JSON routing-state endpoint. Use existing error handling so validation messages return as `DevwtCommandResult` with exit code `2`.

- [ ] **Step 7: Run Service tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore
```

Expected: all Service tests pass.

---

### Task 5: Build Grouped And Timeline Activity UX

**Files:**
- Modify: `src/Devwt.Service/DevwtRuntimeServers.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Produces JavaScript helpers: `setActivityView`, `filterActivityHistory`, `groupActivityHistory`, `renderGroupedActivity`, `renderTimelineActivity`, and `renderScopeTargetEditor`.
- Consumes all status routing collections and Web actions from Tasks 1 and 4.
- Preserves IDs `activity-search`, `activity-reason-filter`, and `connection-history` for timeline compatibility.

- [ ] **Step 1: Write a failing shell contract test**

Extend `Web_ui_shell_is_routing_first_and_tabbed` or add a focused test with these assertions:

```csharp
Assert.Contains("data-activity-view=\"grouped\"", html, StringComparison.Ordinal);
Assert.Contains("data-activity-view=\"timeline\"", html, StringComparison.Ordinal);
Assert.Contains("id=\"activity-grouped\"", html, StringComparison.Ordinal);
Assert.Contains("id=\"activity-timeline\"", html, StringComparison.Ordinal);
Assert.Contains("groupActivityHistory", html, StringComparison.Ordinal);
Assert.Contains("renderGroupedActivity", html, StringComparison.Ordinal);
Assert.Contains("renderTimelineActivity", html, StringComparison.Ordinal);
Assert.Contains("activityView", html, StringComparison.Ordinal);
Assert.Contains("devwt.activityView", html, StringComparison.Ordinal);
Assert.Contains("set-process-port-target", html, StringComparison.Ordinal);
Assert.Contains("set-session-context-target", html, StringComparison.Ordinal);
Assert.Contains("set-session-port-target", html, StringComparison.Ordinal);
Assert.DoesNotContain("data-action=\"set-application-target\"", ExtractTimelineMarkup(html), StringComparison.Ordinal);
```

Extract the timeline renderer directly and assert it has no scope editor call:

```csharp
var timelineStart = html.IndexOf("function renderTimelineActivity", StringComparison.Ordinal);
var timelineEnd = html.IndexOf("function historyRouteMatches", timelineStart, StringComparison.Ordinal);
Assert.True(timelineStart >= 0 && timelineEnd > timelineStart);
var timelineRenderer = html[timelineStart..timelineEnd];
Assert.DoesNotContain("renderScopeTargetEditor", timelineRenderer, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the shell test and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Web_ui_shell_is_routing_first_and_tabbed"
```

Expected: grouped/timeline controls and helpers are absent.

- [ ] **Step 3: Replace the Activity surface markup**

Add a compact segmented control before the existing search/reason controls:

```html
<div class="segmented" role="group" aria-label="Activity view">
  <button data-activity-view="grouped" aria-pressed="true" onclick="setActivityView('grouped')">Grouped</button>
  <button data-activity-view="timeline" aria-pressed="false" onclick="setActivityView('timeline')">Timeline</button>
</div>
<div id="activity-grouped"></div>
<div id="activity-timeline" hidden>
  <div class="table-wrap"><table><!-- existing columns and connection-history body --></table></div>
</div>
```

Use unframed nested disclosure rows, not cards inside cards. Image summaries show executable name/path and process/session/access counts. Process summaries show PID and target state. Session summaries show session ID, observed ports, latest decision, and count. Long paths use the existing `path-cell` truncation and title tooltip.

- [ ] **Step 4: Implement bounded client-side grouping**

Read `devwt.activityView` with a guarded local-storage call and default to `grouped`. Filter the supplied history first, then create temporary nested maps:

```javascript
function groupActivityHistory(entries) {
  const images = new Map();
  for (const entry of entries.slice(0, 200)) {
    const imageValue = entry.applicationKey || entry.processImagePath || '';
    const imageKey = imageValue ? imageValue.toLowerCase() : '__unknown_image__';
    const processKey = entry.processId ? String(entry.processId) : '__unknown_process__';
    const sessionKey = entry.sessionId || '__no_session__';
    if (!images.has(imageKey)) {
      images.set(imageKey, { key: imageKey, value: imageValue, processes: new Map(), count: 0 });
    }
    const image = images.get(imageKey);
    image.count += 1;
    if (!image.processes.has(processKey)) {
      image.processes.set(processKey, {
        key: processKey,
        processId: entry.processId || null,
        sessions: new Map(),
        count: 0
      });
    }
    const process = image.processes.get(processKey);
    process.count += 1;
    if (!process.sessions.has(sessionKey)) {
      process.sessions.set(sessionKey, {
        key: sessionKey,
        sessionId: entry.sessionId || null,
        entries: [],
        ports: new Set()
      });
    }
    const session = process.sessions.get(sessionKey);
    session.entries.push(entry);
    session.ports.add(Number(entry.port));
  }
  return [...images.values()].map(image => ({
    ...image,
    processes: [...image.processes.values()].map(process => ({
      ...process,
      sessions: [...process.sessions.values()].map(session => ({
        ...session,
        ports: [...session.ports].sort((a, b) => a - b)
      }))
    }))
  }));
}
```

Maintain one `Set` of expanded string keys. After grouping, intersect it with keys present in the current snapshot. Auto-expand only the newest entry's image/process/session path when the set is initially empty. Do not put history entries or full group objects in local storage.

- [ ] **Step 5: Render scope target editors**

Use one reusable renderer receiving `{ scope, key, ports, routing, routes, contexts }`. It renders an `All ports` select and observed-port rows. Each port select lists only contexts with a matching route on that numeric port. All-port selects list active contexts. A blank selection clears that exact target. Process editors use PID numbers; unknown image/process/session nodes render no editor.

Wire helper functions to the Task 4 actions. After `action()` refreshes status, all repeated controls for a shared session ID must derive from the same routing collection and display the same selection.

- [ ] **Step 6: Keep Timeline diagnostic-only**

Reuse the current newest-first row details, search, and reason filtering, but remove per-row image target selectors/buttons. Include session ID as a compact line or badge in the process column. Add `session-default` and `session-context` options to the reason filter.

- [ ] **Step 7: Add responsive CSS**

Use stable grid columns for summaries and editors, `minmax(0, 1fr)` for paths, and existing 8px-or-less radii. At `max-width: 980px`, stack target editor rows. At `max-width: 560px`, keep the view segmented control full width and table horizontal scrolling contained inside `.table-wrap`. Do not introduce gradients, decorative cards, or nested card borders.

- [ ] **Step 8: Run the shell and full test suites**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Web_ui_shell_is_routing_first_and_tabbed"
dotnet test .\Devwt.slnx --no-restore
```

Expected: shell test passes; Core and Service suites have zero failures.

---

### Task 6: Browser Verification, Documentation, And Installer Export

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/troubleshooting.md`
- Build output: `private/installer/DevWT-installer.zip`

**Interfaces:**
- Documents the final precedence and Activity controls.
- Produces a validated installer archive without installing it.

- [ ] **Step 1: Update operator documentation**

Document grouped/timeline modes, image/process/session all-port and per-port semantics, natural same-session routing, the exact precedence, 200 history entries, and 512 process-cache entries. State that unknown identities cannot receive scoped defaults.

- [ ] **Step 2: Start a non-installed development UI host**

Build and run on an unused loopback port with a temporary state root:

```powershell
dotnet build .\src\Devwt.Cli\Devwt.Cli.csproj --no-restore
$env:DEVWT_STATE_ROOT = Join-Path $env:TEMP 'devwt-activity-ui'
.\src\Devwt.Cli\bin\Debug\net10.0\Devwt.Cli.exe ui --state-root $env:DEVWT_STATE_ROOT --listen http://127.0.0.1:18777/
```

Use a hidden background process and stop only the verified `Devwt.Cli` listener on port `18777` after testing.

- [ ] **Step 3: Verify desktop and mobile behavior with Playwright**

At `1440x900` and `390x844`, verify:

- only the selected Activity mode is visible;
- mode selection persists after reload;
- hierarchy is image -> process -> session -> history;
- image, process, and session editors show all-port and observed-port controls;
- repeated session controls update consistently;
- Timeline has no target editors;
- long paths and unknown identities remain contained;
- `document.documentElement.scrollWidth === document.documentElement.clientWidth` outside intentional `.table-wrap` scrolling;
- browser console has no new errors.

- [ ] **Step 4: Run final source verification**

Run:

```powershell
git diff --check
dotnet test .\Devwt.slnx --no-restore
dotnet build .\Devwt.slnx -c Release --no-restore
```

Expected: no whitespace errors, zero test failures, zero build errors, and zero build warnings.

- [ ] **Step 5: Build and validate the installer zip**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-DevWTInstallerBundle.ps1 -Configuration Release
Get-FileHash .\private\installer\DevWT-installer.zip -Algorithm SHA256
```

Open the archive with `System.IO.Compression.ZipFile` and assert it contains:

```text
Install-DevWT.ps1
Uninstall-DevWT.ps1
app/Devwt.Cli.exe
app/hook/devwt-hook.dll
extension/devwt-browser/manifest.json
```

Expected: archive is valid and no install/uninstall command has run.

- [ ] **Step 6: Review final scope without committing unrelated changes**

Run:

```powershell
git status --short
git diff --stat
```

Report the tests, build result, browser viewports, zip path/hash, cache bounds, and any source changes intentionally left uncommitted because of the pre-existing dirty worktree.
