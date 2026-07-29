# DevWT Routing Console UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace DevWT's single active `context + port` fallback and oversized one-page console with explicit global/per-port routing modes and a routing-first tabbed UI.

**Architecture:** Normalize legacy routing state at the state-store boundary, retain one inactive configuration per mode, and resolve only the selected fallback mode in the gateway. Keep browser, process, session, application, and self-process decisions ahead of this fallback. Reorganize the existing dependency-free HTML/CSS/JavaScript shell into four accessible tab panels without changing the ASP.NET Core Minimal API/SignalR hosting model.

**Tech Stack:** .NET 10, C# records and `System.Text.Json`, ASP.NET Core Minimal APIs and SignalR, xUnit, embedded HTML/CSS/JavaScript.

## Global Constraints

- The active fallback mode is either `GlobalContext` or `PerPort`; the modes never apply simultaneously.
- Per-port targets are keyed by numeric port, and TCP/UDP share the same target.
- Changing or clearing one port must not alter another port.
- Browser-, self-process-, process/session-, and application-specific targets keep their current precedence over fallback routing.
- Existing `routing.json` files containing `ActiveTarget` must load as a per-port target without losing other routing collections.
- Existing `devwt proxy target --context <context-id> --port <port>` behavior stays available.
- The Web UI defaults to Routing and contains Routing, Contexts, Activity, and Settings tabs.
- Do not install or uninstall DevWT on the main machine; build only the installer archive.

---

## File Map

- `src/Devwt.Core/DevwtModels.cs`: routing mode and persisted global/per-port target models plus legacy normalization.
- `src/Devwt.Core/DevwtStateStore.cs`: normalize routing state on load and before save.
- `src/Devwt.Core/DevwtCommandParser.cs`: global context and port-specific clear CLI syntax.
- `src/Devwt.Service/GatewayRouting.cs`: resolve the selected fallback mode for TCP and UDP candidates.
- `src/Devwt.Service/ControlApi.cs`: validate and atomically update mode/global/per-port state.
- `src/Devwt.Service/DevwtCliRunner.cs`: map parsed proxy commands to control requests.
- `src/Devwt.Cli/DevwtWebUiAspNetHost.cs`: map Web UI mode/global/port-clear actions to control requests.
- `src/Devwt.Service/DevwtRuntimeServers.cs`: tabbed shell, compact status strip, routing workspace, activity filters, and rendering.
- `tests/Devwt.Core.Tests/HookCoreStateTests.cs`: persistence and legacy migration.
- `tests/Devwt.Core.Tests/HookCoreCommandTests.cs`: CLI parsing.
- `tests/Devwt.Service.Tests/DevwtCliRunnerTests.cs`: CLI-to-control request mapping.
- `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`: control state transitions, gateway decisions, and Web UI contract.
- `README.md`, `docs/architecture.md`, `docs/troubleshooting.md`: current routing modes and commands.

---

### Task 1: Persisted Routing Modes And Legacy Migration

**Files:**
- Modify: `src/Devwt.Core/DevwtModels.cs`
- Modify: `src/Devwt.Core/DevwtStateStore.cs`
- Test: `tests/Devwt.Core.Tests/HookCoreStateTests.cs`

**Interfaces:**
- Produces: `DevwtActiveTargetMode`, `DevwtPortActiveTarget`, `DevwtRoutingState.ActiveTargetMode`, `DevwtRoutingState.GlobalActiveContextId`, `DevwtRoutingState.PortActiveTargets`, and `DevwtRoutingState.Normalize(DevwtRoutingState)`.
- Preserves: legacy `DevwtActiveTarget` only as a deserialization input; normalized state sets it to `null`.

- [ ] **Step 1: Add failing state round-trip and migration tests**

Add tests that save modern state and load hand-written legacy JSON:

```csharp
[Fact]
public void Routing_state_round_trips_global_and_independent_port_targets()
{
    using var temp = new TempDirectory();
    var store = new DevwtStateStore(temp.Path);
    store.SaveRouting(new DevwtRoutingState([], null)
    {
        ActiveTargetMode = DevwtActiveTargetMode.GlobalContext,
        GlobalActiveContextId = "ctx-global",
        PortActiveTargets =
        [
            new DevwtPortActiveTarget("ctx-a", 44334),
            new DevwtPortActiveTarget("ctx-b", 5001)
        ]
    });

    var loaded = store.LoadRouting();

    Assert.Equal(DevwtActiveTargetMode.GlobalContext, loaded.ActiveTargetMode);
    Assert.Equal("ctx-global", loaded.GlobalActiveContextId);
    Assert.Equal([44334, 5001], loaded.PortActiveTargets.Select(x => x.Port));
}

[Fact]
public void Routing_state_migrates_legacy_active_target_to_per_port_mode()
{
    using var temp = new TempDirectory();
    File.WriteAllText(Path.Combine(temp.Path, "routing.json"), """
        {
          "explicitLinkMaps": [],
          "activeTarget": { "contextId": "ctx-legacy", "port": 44334, "scheme": "https" },
          "processTargets": [{ "processId": 17, "contextId": "ctx-process" }]
        }
        """);

    var loaded = new DevwtStateStore(temp.Path).LoadRouting();

    Assert.Equal(DevwtActiveTargetMode.PerPort, loaded.ActiveTargetMode);
    Assert.Null(loaded.ActiveTarget);
    Assert.Equal(new DevwtPortActiveTarget("ctx-legacy", 44334), Assert.Single(loaded.PortActiveTargets));
    Assert.Equal(17, Assert.Single(loaded.ProcessTargets).ProcessId);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Core.Tests\Devwt.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~Routing_state_"
```

Expected: compilation fails because `DevwtActiveTargetMode`, `DevwtPortActiveTarget`, and the new properties do not exist.

- [ ] **Step 3: Add the routing types and normalization**

Add the following model surface and extend the existing JSON constructor with nullable collection arguments:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<DevwtActiveTargetMode>))]
public enum DevwtActiveTargetMode
{
    PerPort,
    GlobalContext
}

public sealed record DevwtPortActiveTarget(string ContextId, int Port);

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

    public static DevwtRoutingState Normalize(DevwtRoutingState state)
    {
        var targets = state.PortActiveTargets
            .Where(target => target.Port is > 0 and <= 65535)
            .GroupBy(target => target.Port)
            .Select(group => group.Last())
            .ToList();
        if (state.ActiveTarget is { } legacy && targets.All(target => target.Port != legacy.Port))
        {
            targets.Add(new DevwtPortActiveTarget(legacy.ContextId, legacy.Port));
        }

        return state with
        {
            ActiveTarget = null,
            ActiveTargetMode = state.ActiveTarget is null ? state.ActiveTargetMode : DevwtActiveTargetMode.PerPort,
            PortActiveTargets = targets.OrderBy(target => target.Port).ToArray()
        };
    }
}
```

Keep the existing JSON constructor and `Empty` factory, adding optional `ActiveTargetMode`, `GlobalActiveContextId`, and `PortActiveTargets` parameters and assigning them to the properties.

Update the store boundary:

```csharp
public DevwtRoutingState LoadRouting() =>
    DevwtRoutingState.Normalize(Load(RoutingPath, DevwtRoutingState.Empty));

public void SaveRouting(DevwtRoutingState state) =>
    Save(RoutingPath, DevwtRoutingState.Normalize(state));
```

- [ ] **Step 4: Run focused and full Core tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\Devwt.Core.Tests\Devwt.Core.Tests.csproj --no-restore
```

Expected: all Core tests pass; update the old round-trip assertion from `ActiveTarget` to the migrated `PortActiveTargets` value.

- [ ] **Step 5: Review the task diff**

Run:

```powershell
git diff --check -- src/Devwt.Core/DevwtModels.cs src/Devwt.Core/DevwtStateStore.cs tests/Devwt.Core.Tests/HookCoreStateTests.cs
```

Expected: no whitespace errors. Do not stage unrelated pre-existing changes from these already-modified files.

---

### Task 2: Gateway Resolution For Global And Per-Port Modes

**Files:**
- Modify: `src/Devwt.Service/GatewayRouting.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Consumes: `DevwtRoutingState.Normalize`, `DevwtActiveTargetMode`, and `DevwtPortActiveTarget` from Task 1.
- Produces: existing `GatewayRouteTable.ResolveGlobalActiveTarget(int, GatewayRouteProtocol, string?)` with new mode semantics; no gateway caller signature changes.

- [ ] **Step 1: Add failing gateway tests**

```csharp
[Fact]
public void Gateway_global_context_target_resolves_same_context_across_ports()
{
    var table = GatewayRouteTable.FromRoutes(
        [
            Route("ctx-a", 44334, 24434),
            Route("ctx-b", 44334, 34434),
            Route("ctx-a", 5001, 25001),
            Route("ctx-b", 5001, 35001)
        ],
        ContextState("ctx-a", "ctx-b"),
        new DevwtRoutingState([], null)
        {
            ActiveTargetMode = DevwtActiveTargetMode.GlobalContext,
            GlobalActiveContextId = "ctx-b"
        });

    Assert.Equal("ctx-b", table.ResolveGlobalActiveTarget(44334)!.ContextId);
    Assert.Equal("ctx-b", table.ResolveGlobalActiveTarget(5001)!.ContextId);
}

[Fact]
public void Gateway_per_port_targets_are_independent_and_shared_by_tcp_udp()
{
    var routing = new DevwtRoutingState([], null)
    {
        ActiveTargetMode = DevwtActiveTargetMode.PerPort,
        PortActiveTargets =
        [
            new DevwtPortActiveTarget("ctx-a", 44334),
            new DevwtPortActiveTarget("ctx-b", 5001)
        ]
    };
    var table = GatewayRouteTable.FromRoutes(
        RoutesForTcpAndUdp("ctx-a", "ctx-b", 44334, 5001),
        ContextState("ctx-a", "ctx-b"),
        routing);

    Assert.Equal("ctx-a", table.ResolveGlobalActiveTarget(44334, GatewayRouteProtocol.Tcp)!.ContextId);
    Assert.Equal("ctx-a", table.ResolveGlobalActiveTarget(44334, GatewayRouteProtocol.Udp)!.ContextId);
    Assert.Equal("ctx-b", table.ResolveGlobalActiveTarget(5001)!.ContextId);
}
```

Use the test class's existing route/context helpers; add a local helper only when the existing signatures cannot represent UDP.

- [ ] **Step 2: Run the focused gateway tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Gateway_global_context_target|FullyQualifiedName~Gateway_per_port_targets"
```

Expected: assertions fail because only legacy `ActiveTarget` is inspected.

- [ ] **Step 3: Resolve one selected fallback mode**

Normalize routing in `FromRoutes`/`WithRouting`, then centralize target-context lookup:

```csharp
private string? ResolveConfiguredContextId(int port) =>
    _routing.ActiveTargetMode switch
    {
        DevwtActiveTargetMode.GlobalContext => _routing.GlobalActiveContextId,
        DevwtActiveTargetMode.PerPort => _routing.PortActiveTargets
            .FirstOrDefault(target => target.Port == port)?.ContextId,
        _ => null
    };
```

Use the returned context ID in both `ResolveGlobalActiveTarget` and the private `ResolveActiveTarget`. Candidate filtering must still use requested protocol and listen IP before matching the context.

- [ ] **Step 4: Run focused gateway tests and verify GREEN**

Run the command from Step 2.

Expected: both new tests pass.

- [ ] **Step 5: Run all Service tests**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore
```

Expected: all Service tests pass after updating legacy active-target fixtures to normalized per-port state only where their assertions depend on persisted shape.

---

### Task 3: Atomic Control Updates And CLI Commands

**Files:**
- Modify: `src/Devwt.Core/DevwtCommandParser.cs`
- Modify: `src/Devwt.Service/ControlApi.cs`
- Modify: `src/Devwt.Service/DevwtCliRunner.cs`
- Modify: `src/Devwt.Cli/DevwtWebUiAspNetHost.cs`
- Test: `tests/Devwt.Core.Tests/HookCoreCommandTests.cs`
- Test: `tests/Devwt.Service.Tests/DevwtCliRunnerTests.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Produces: `ProxyContextTargetCommand`, `ProxyClearCommand(int? Port)`, `DevwtControlRequest.ActiveTargetMode`, and `DevwtControlRequest.GlobalActiveContextId`.
- Preserves: browser-scoped `set-active-target` continues to write `BrowserActiveTargets` and never changes the global fallback mode.

- [ ] **Step 1: Add failing parser and runner tests**

```csharp
[Fact]
public void Proxy_context_and_port_specific_clear_commands_are_parsed()
{
    var context = Assert.IsType<ProxyContextTargetCommand>(DevwtCommandParser.Parse(
        ["proxy", "context", "--context", "ctx-a"]));
    var clearPort = Assert.IsType<ProxyClearCommand>(DevwtCommandParser.Parse(
        ["proxy", "clear", "--port", "44334"]));
    var clearMode = Assert.IsType<ProxyClearCommand>(DevwtCommandParser.Parse(
        ["proxy", "clear"]));

    Assert.Equal("ctx-a", context.ContextId);
    Assert.Equal(44334, clearPort.Port);
    Assert.Null(clearMode.Port);
}
```

Extend `DevwtCliRunnerTests` so `ProxyContextTargetCommand` sends `ActiveTargetMode = GlobalContext` plus `GlobalActiveContextId`, while port clear sends `ClearActiveTarget = true` plus `Port`.

- [ ] **Step 2: Add failing control-state tests**

Add tests proving:

```csharp
handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetActiveTarget,
    ActiveTarget: new DevwtActiveTarget("ctx-a", 44334, "auto")));
handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetActiveTarget,
    ActiveTarget: new DevwtActiveTarget("ctx-b", 5001, "auto")));
handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetActiveTarget,
    ClearActiveTarget: true,
    Port: 44334));

var routing = store.LoadRouting();
Assert.Equal(DevwtActiveTargetMode.PerPort, routing.ActiveTargetMode);
Assert.Equal(new DevwtPortActiveTarget("ctx-b", 5001), Assert.Single(routing.PortActiveTargets));
```

Also test that selecting global mode retains inactive `PortActiveTargets`, and that a browser-scoped update leaves `ActiveTargetMode`, `GlobalActiveContextId`, and `PortActiveTargets` unchanged.

- [ ] **Step 3: Run parser, runner, and control tests and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Core.Tests\Devwt.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~Proxy_context_and_port_specific_clear"
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Proxy_|FullyQualifiedName~active_target"
```

Expected: compilation fails for new command/request members or assertions fail against single-target behavior.

- [ ] **Step 4: Implement parser and runner mappings**

Add:

```csharp
public sealed record ProxyContextTargetCommand(string ContextId) : DevwtCommand;
public sealed record ProxyClearCommand(int? Port = null) : DevwtCommand;
```

Parse `proxy context --context <context-id>` and `proxy clear [--port <1..65535>]` with the same strict unknown-option behavior as `ParseProxyTarget`. Update HelpText with the exact command forms.

Map commands in `DevwtCliRunner.Execute`:

```csharp
ProxyContextTargetCommand context => controlClient.Send(new DevwtControlRequest(
    DevwtControlOperation.SetActiveTarget,
    ActiveTargetMode: DevwtActiveTargetMode.GlobalContext,
    GlobalActiveContextId: context.ContextId)),
ProxyClearCommand clear => controlClient.Send(new DevwtControlRequest(
    DevwtControlOperation.SetActiveTarget,
    ClearActiveTarget: true,
    Port: clear.Port)),
```

- [ ] **Step 5: Implement atomic control state transitions**

Extend `DevwtControlRequest` with:

```csharp
DevwtActiveTargetMode? ActiveTargetMode = null,
string? GlobalActiveContextId = null
```

Refactor `SetActiveTarget` into explicit branches in this order:

1. Browser-scoped clear/set: preserve existing behavior and return.
2. Port clear: validate 1..65535, remove only the matching `PortActiveTargets` entry, save, return.
3. Current-mode clear: clear `GlobalActiveContextId` in global mode or all `PortActiveTargets` in per-port mode.
4. Global context set: validate context, set `ActiveTargetMode = GlobalContext` and `GlobalActiveContextId`, preserve port targets.
5. Mode-only set: update `ActiveTargetMode`, preserve both configurations.
6. Per-port set: validate context and port, replace only the same-port entry, set `ActiveTargetMode = PerPort`, preserve global context.

All saved states must set legacy `ActiveTarget = null` through `DevwtRoutingState.Normalize`.

- [ ] **Step 6: Add Web UI action mappings**

Map these JSON actions in `DevwtWebUiAspNetHost.ExecuteAction`:

```csharp
"set-active-target-mode" => handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetActiveTarget,
    ActiveTargetMode: ParseActiveTargetMode(action.ActiveTargetMode))),
"set-global-active-context" => handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetActiveTarget,
    ActiveTargetMode: DevwtActiveTargetMode.GlobalContext,
    GlobalActiveContextId: action.ContextId)),
"clear-active-target" => handler.Handle(new DevwtControlRequest(
    DevwtControlOperation.SetActiveTarget,
    ClearActiveTarget: true,
    Port: action.Port,
    ActiveTargetBrowserKey: action.BrowserScoped ? ResolveRequiredBrowserKey(context) : null)),
```

Add `ActiveTargetMode` to `DevwtWebUiAction` and parse only `global-context` and `per-port`.

- [ ] **Step 7: Run all Core and Service tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\Devwt.Core.Tests\Devwt.Core.Tests.csproj --no-restore
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore
```

Expected: all tests pass.

---

### Task 4: Routing-First Tabbed Web UI

**Files:**
- Modify: `src/Devwt.Service/DevwtRuntimeServers.cs`
- Test: `tests/Devwt.Service.Tests/HookCoreServiceTests.cs`

**Interfaces:**
- Consumes status JSON properties `routing.activeTargetMode`, `routing.globalActiveContextId`, and `routing.portActiveTargets`.
- Produces Web UI actions `set-active-target-mode`, `set-global-active-context`, `set-active-target`, and port-aware `clear-active-target`.
- Preserves existing DOM IDs used by render functions and tests where the underlying feature remains.

- [ ] **Step 1: Replace old shell assertions with failing tabbed-UI assertions**

Update the Web UI asset test to assert:

```csharp
Assert.Contains("role=\"tablist\"", html, StringComparison.Ordinal);
Assert.Contains("data-tab=\"routing\"", html, StringComparison.Ordinal);
Assert.Contains("data-tab=\"contexts\"", html, StringComparison.Ordinal);
Assert.Contains("data-tab=\"activity\"", html, StringComparison.Ordinal);
Assert.Contains("data-tab=\"settings\"", html, StringComparison.Ordinal);
Assert.Contains("id=\"routing-mode-global\"", html, StringComparison.Ordinal);
Assert.Contains("id=\"routing-mode-per-port\"", html, StringComparison.Ordinal);
Assert.Contains("id=\"global-context-select\"", html, StringComparison.Ordinal);
Assert.Contains("id=\"port-routing-groups\"", html, StringComparison.Ordinal);
Assert.Contains("id=\"activity-search\"", html, StringComparison.Ordinal);
Assert.Contains("localStorage.getItem('devwt.activeTab')", html, StringComparison.Ordinal);
Assert.DoesNotContain("Active Proxy Target", html, StringComparison.Ordinal);
Assert.DoesNotContain("id=\"active-target-summary\"", html, StringComparison.Ordinal);
Assert.DoesNotContain("class=\"overview\"", html, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the Web UI test and verify RED**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore --filter "FullyQualifiedName~Web_ui"
```

Expected: assertions fail because the current shell has no tabs or routing modes and still contains the old active-target layout.

- [ ] **Step 3: Build the shared header and accessible tabs**

Replace the five-card overview with a compact `#status-strip`. Add a tab list using buttons with `role="tab"`, `aria-selected`, and `aria-controls`. Add four panels with stable IDs:

```html
<nav class="tabs" role="tablist" aria-label="DevWT sections">
  <button role="tab" data-tab="routing" aria-controls="panel-routing">Routing</button>
  <button role="tab" data-tab="contexts" aria-controls="panel-contexts">Contexts</button>
  <button role="tab" data-tab="activity" aria-controls="panel-activity">Activity</button>
  <button role="tab" data-tab="settings" aria-controls="panel-settings">Settings</button>
</nav>
```

Initialize Routing unless `localStorage.getItem('devwt.activeTab')` contains one of the four allowed values. `selectTab(name)` sets `hidden`, `aria-selected`, and `tabindex` consistently and writes the preference.

- [ ] **Step 4: Build the Routing workspace**

Add a compact mode toolbar and render functions:

```javascript
function setRoutingMode(mode) {
  return action({ action: 'set-active-target-mode', activeTargetMode: mode });
}
function setGlobalContext(contextId) {
  return action({ action: 'set-global-active-context', contextId });
}
function setActivePort(contextId, port) {
  return action({ action: 'set-active-target', contextId, port, scheme: 'auto' });
}
function clearActivePort(port) {
  return action({ action: 'clear-active-target', port });
}
```

`renderRouting(status, routes)` groups routes by numeric port, finds the matching entry in `status.routing.portActiveTargets`, and renders one context selector per group. Its selected value comes only from that group's port target. Global mode renders one context selector from `globalActiveContextId`. TCP/UDP are badges inside the same port group.

- [ ] **Step 5: Move existing features into Contexts, Activity, and Settings**

- Contexts: retain `#context-search`, status segmented filter, and `#contexts` table.
- Activity: retain `#connection-history`, add `#activity-search` and `#activity-reason-filter`, and filter before mapping rows.
- Settings: move `#runtime-backends`, `#session-rule-form`, `#session-rules`, and `#repos` without changing their action payloads.

Remove inline feature-description copy that explains how to use the application. Keep only concise state labels and empty-state text.

- [ ] **Step 6: Tighten responsive CSS**

Use a neutral multi-color palette already present in the app, 8px maximum card radius, sticky compact header, horizontally scrollable tabs on narrow screens, stable control heights, and panel-level table scrolling. Do not use gradient backgrounds, nested cards, oversized headings, or viewport-scaled fonts.

- [ ] **Step 7: Run Web UI and all Service tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\Devwt.Service.Tests\Devwt.Service.Tests.csproj --no-restore
```

Expected: all Service tests pass, including the new shell contract.

- [ ] **Step 8: Inspect the rendered shell at desktop and mobile widths**

Serve `DevwtWebUiAssets.RenderShell()` only through a non-installed development process on a free high port, then inspect 1440x900 and 390x844 viewports. Verify no overlapping controls, no document-level horizontal overflow, Routing is initially visible, each tab switches without a network request, and per-port selectors remain independent after a SignalR-style rerender.

---

### Task 5: Documentation, Regression Verification, And Installer Archive

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/troubleshooting.md`
- Verify: all changed source and test files
- Generate: `private/installer/DevWT-installer.zip`

**Interfaces:**
- Documents exact CLI syntax and fallback priority from Tasks 2 and 3.
- Produces a manually installable archive without changing the installed DevWT instance.

- [ ] **Step 1: Update routing documentation**

Document:

```text
devwt proxy target --context <context-id> --port <port>
devwt proxy context --context <context-id>
devwt proxy clear --port <port>
devwt proxy clear
```

State that global and per-port are mutually exclusive active modes, TCP/UDP share a numeric-port target, and browser/process/application selections remain stronger signals.

- [ ] **Step 2: Run solution tests**

Run:

```powershell
dotnet test .\Devwt.slnx --no-restore
```

Expected: all Core and Service tests pass with zero failures.

- [ ] **Step 3: Run release build**

Run:

```powershell
dotnet build .\Devwt.slnx -c Release --no-restore
```

Expected: build succeeds with zero errors; investigate and remove warnings introduced by this change.

- [ ] **Step 4: Check changed-file hygiene**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors. Confirm no generated files outside `private/installer` and no unrelated user changes were reverted.

- [ ] **Step 5: Build, but do not install, the archive**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-DevWTInstallerBundle.ps1 -Configuration Release
Get-FileHash .\private\installer\DevWT-installer.zip -Algorithm SHA256
```

Expected: `D:\GitHub\devwt\private\installer\DevWT-installer.zip` exists and a SHA256 hash is printed. Do not run `Install-DevWT.ps1` or `Uninstall-DevWT.ps1`.
