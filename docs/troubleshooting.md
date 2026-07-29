# Troubleshooting

## `devwt add` says the service is not running

Install/start `DevWTService`:

```powershell
devwt service install --yes
devwt service start
```

Install requires an elevated PowerShell. Normal CLI commands should not require an elevated shell after the service is installed with the current version.

## `devwt add` says access to the pipe is denied

Restart or reinstall the service so the named pipe ACL is recreated:

```powershell
devwt service restart
```

If the old service binary is still installed, reinstall from an elevated PowerShell:

```powershell
devwt service uninstall --yes
devwt service install --yes
```

## Git reports dubious ownership

DevWT runs service-side Git inspection with a command-local `safe.directory=*` override. It does not write a global Git safe-directory exception. Reinstall/restart the service if you still see this after upgrading.

## HTTPS development certificates

Use the DevWT wrapper around the standard .NET development certificate flow:

```powershell
devwt cert status
devwt cert trust
```

Cleanup is available when needed:

```powershell
devwt cert clean
```

If `https://localhost:<port>` fails in `Auto` or `HTTP Inspect`, check the gateway
certificate state and trust it explicitly when inspection is intended:

```powershell
devwt gateway-cert status
devwt gateway-cert trust --machine
```

`Auto` uses `TLS Tunnel` until the root is trusted for the machine. `HTTP Inspect`
requires the browser to trust the DevWT localhost certificate. The gateway then
detects whether the selected backend uses plain HTTP or HTTPS; an HTTPS backend
must present a certificate valid for its localhost host. Use `TLS Tunnel` for
non-HTTP TLS services. Use `Raw` when a custom TCP protocol starts with
HTTP-looking bytes or must avoid the initial protocol probe. HTTP/2, gRPC, and
WebSockets are handled by the Kestrel/YARP inspection path. UDP is always
forwarded as opaque datagrams.

## Browser `Use in this tab` does not reload or close the popup

Current extension behavior is version `0.3.22`: after the tab-scoped active
worktree rule is stored successfully, `Use in this tab` hard reloads the active tab
with its HTTP cache bypassed and closes the popup. A failed selection leaves the
popup open so the error remains visible. The HTTP and HTTPS actions instead
create a new selected tab and navigate it to the chosen localhost scheme.
Selections are persisted outside the service worker's in-memory/session state.
Browser startup and extension updates rebuild tab rules, badges, title labels,
and enabled managed groups from that persistent state. If Chrome restores the
tabs after the extension startup event, unmatched records remain pending and
are reconciled by the subsequent tab creation/navigation events. Window-closing
tab removal events retain the records needed by Chrome saved tab groups.
Port labels use the backend process name from the DevWT status API and fall
back to PID when that process has already exited or cannot be inspected.

`Group tabs by context` is off by default. When enabled, existing selected tabs
are grouped per window and context, and changing a tab's context moves it to the
new group. Dragging a localhost tab into a DevWT-created group selects the
group's context as the active worktree and reloads it. If a grouped tab later
opens another localhost port, it keeps the group context. Turning grouping
off removes only memberships managed by DevWT.

If a port is closed in the active worktree but open in another worktree of the
same repository, configure it under `Other ports > Worktree missing-port policy`.
The redirect cannot override a live listener in the active worktree. With no
redirect, the active context stays explicit and the missing request returns
`502` instead of selecting an unrelated worktree.

DevWT Console has one global switch under
`Settings > Browser Missing-Port Fallback`. It is the default only when the
active context and natural port have no worktree policy. The extension selector
stores Automatic, explicit provider, or No redirect by context ID and port, so
the choice applies to every tab using that active worktree.
The extension displays either the provider redirect or the automatic decision
mode in a dismissible Shadow DOM notice. This is extension UI, not gateway
response-body rewriting, so it does not change CSP, compression, streaming,
gRPC, or WebSocket payloads.

The gateway evaluates these policies only for extension-managed tab requests.
URL `devwt-context` selectors, Playwright headers, and ordinary explicit
`X-DevWT-Context` requests remain fail-closed.

`Automatic` forces fallback for the worktree/port even when the Console default
is off. `Console default` removes the policy, and `No redirect` forces
fail-closed behavior. If the assigned context does not listen on the current
port, the popup still shows it as Active and expands these choices. Live status
redraws are deferred while the select has focus. Saving keeps the popup open and
does not reload the tab.

`Show context in tab title` is also off by default. Enabling or disabling it
updates existing selected localhost tabs immediately. Its label uses the context
description when present and otherwise the Git ref.

For an unpacked extension, replacing files does not activate the new service
worker automatically. Open `chrome://extensions` or `edge://extensions`, verify
the DevWT version, and press `Reload` once. Then reopen the localhost tab and
select `Use in this tab` again.

## Inspected HTTP or HTTPS returns `502`

Open Console Activity and confirm that the selected context still has a live
backend route for the same original IP and numeric port. For an inspected
response, also inspect `X-DevWT-Proxy-Error`; it reports the YARP forwarding
failure category.

Restart stale backends through the current hook runtime when their shifted
listener disappeared. Do not force the browser-side scheme onto the backend:
current DevWT independently detects plain HTTP versus HTTPS upstream. For an
HTTPS upstream, check certificate name, validity, and server-auth usage. For an
opaque non-HTTP TLS service, switch that IP/port to `TLS Tunnel` instead of
`HTTP Inspect`.

## A helper process must reach the gateway directly

If a diagnostic tool, browser helper, or script is started from a hooked
terminal and must connect to the host gateway on `127.0.0.1`, set:

```powershell
$env:DEVWT_HOOK_DISABLE = "1"
```

This is process-local. It makes the hook DLL pass through bind/connect calls and
skip child injection, without removing DevWT from other development processes.

## Hook runtime artifacts are missing

Build/install from a DevWT installer bundle, or set `DEVWT_HOOK_ROOT` to a folder that contains:

- `devwt-hook.dll`
- `devwt-hook-launcher.exe`
- `devwt-folder-watcher.exe`

For local development:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\poc\hook-win32\Build-HookPoc.ps1
$env:DEVWT_HOOK_ROOT = "$PWD\poc\hook-win32\artifacts"
```

## Port is already in use on 127.0.0.1

DevWT exposes each original IP/port through a separate process whose image name
contains the virtual backend application name followed by `--DevWT-Proxy--`.
Tools that find a PID by listening port therefore see only that port's owner
worker, not the central `Devwt.Cli` service.

Killing the owner worker force-stops every context backend currently using that
same IP/port, including TCP and UDP listeners, and leaves the proxy port closed
until those routes disappear. Other DevWT ports continue running. If the port
is owned by an unrelated process rather than a `--DevWT-Proxy--` image, stop
that unrelated owner before DevWT can expose the virtual endpoint.

You can choose the fallback target for ambiguous proxy traffic. Use one context
for every port:

```powershell
devwt proxy context --context <context-id>
devwt proxy clear
```

Or select contexts independently by numeric port:

```powershell
devwt proxy target --context <context-id> --port 5025
devwt proxy clear --port 5025
```

Setting a per-port target selects per-port mode but does not change any other
port. TCP and UDP share the same numeric-port selection. Browser-, process-,
session-, and application-specific decisions still take precedence.

## A request goes to the first or newest context

Open the Web UI `Activity` tab and inspect the decision reason. `newest` means
no stronger process, session, image, browser, or fallback target matched that
IP and port. `single-target` means that exact original IP/port/protocol had only
one eligible context, so no manual selection was needed. `Callers` mode presents
adjacent Image, Process, and Session lists with one inspector; Timeline shows
the same newest-first diagnostic records.

Set a default on the narrowest useful scope:

- Process `All ports` applies to that PID; its per-port values override it.
- Session `All ports` applies to every process with that session identity; its per-port values override it.
- Image `All ports` applies to that executable/application key; its per-port values override it.

Selecting `Automatic` removes only the selected scope or port override. A
configured target is skipped when the chosen context has no matching listener
for the requested IP, port, and protocol. Unknown process, image, or session
identities remain visible in history but cannot receive a scoped default.

Processes with the same configured session identity route to one another
naturally before inferred image and fallback routing. If the expected session
is missing, verify the session rule in `Settings`, restart the client process so
the rule can observe its startup identity, and check that the target listener
was started in the same session.

Environment-only rules such as `CODEX_THREAD_ID` are valid when the launcher
intentionally propagates the value to task children. If such a process appears
under an unrelated context, update DevWT: older child-hook propagation could
fall back to the parent's context even when the child directory did not match
any registered worktree.

Activity history is intentionally limited to 200 decisions. Process identity
and remembered-context caches are each limited to 512 entries, so this view and
long-running gateway operation do not grow memory without a bound.

## App output says `BOUND ::1:<port>` or `Failed to bind https://[::1]:<port>`

Current port-shift mode supports both IPv4 and IPv6 and does not modify
runtime-specific arguments, configuration, or environment variables. A literal
`::1` in output therefore does not by itself mean isolation failed:
`getsockname()` is masked back to the application-requested endpoint while the
real listener uses the shifted backend port. Check `hook-port-bindings.tsv`,
`devwt port process`, or the Console before treating that output as a missed
injection.

If no IPv6 binding-map row exists, or Windows still shows the application
owning the original `::1:<port>`, the process was not injected or is using an
older hook DLL. Restart it through a current DevWT runtime.

If this happens after starting a process directly from an ordinary terminal,
restart the app through `devwt run -- <program> [args...]` or through a DevWT
runtime shim. DevWT does not change the shell's own networking unless that
shell was explicitly launched through full `devwt run` mode.

For generic PowerShell child injection, install the profile integration:

```powershell
devwt shell install
```

Open a new tab. The shell is hooked in children-only mode, so any future native
executable is matched to the registered context by working directory without
command-specific wrappers.

Explicit `AF_INET6` binds to literal `::1` use the same deterministic
same-address port-shift path as IPv4.

## Two natural ports converge on one shifted backend port

Windows-excluded ranges can make two different natural ports in one context
walk to the same available backend port. The second bind then reports
`WSAEADDRINUSE (10048)` even though the natural ports differ.

Current hooks continue the bounded shifted-port search only when
`hook-port-bindings.tsv` proves that the occupied context/IP/protocol/backend
port belongs to a live binding for a different natural port. A second bind for
the same natural port does not continue: its normal Winsock reuse or exclusive
socket behavior is preserved. Do not treat every `10048` as retryable.

## Rider or another IDE starts apps outside the DevWT context

If the IDE was opened normally from Explorer or the Windows taskbar, PowerShell
child injection is not involved. The IDE can start any native application
directly, so the app may bind to normal `127.0.0.1` / `::1`.

Register the IDE parent process with the background watcher. This leaves the
IDE process itself alone and injects only the child processes it creates:

```powershell
devwt ide watch add --name Rider --path "$env:LOCALAPPDATA\Programs\Rider\bin\rider64.exe"
devwt ide watch add --name "ABP Studio" --path "$env:LOCALAPPDATA\abp-studio\current\Volo.Abp.Studio.UI.Host.exe"
```

For Microsoft Store/MSIX apps such as Codex, register the AppID or package
family instead of the versioned `WindowsApps` executable path:

```powershell
Get-StartApps | Where-Object Name -like "*Codex*"
devwt ide watch add --name Codex --app-id "OpenAI.Codex_2p2nqsd0c76g0!App"
```

This does not modify taskbar shortcuts, Start menu entries, file associations,
or IDE installations. It applies after the IDE process exists, so ordinary
Windows launching and Jump List recent items keep using the original app.
The installed service watcher first tries Windows process-start events for this
case and falls back to polling if that event session is unavailable.

If you previously wrapped a pinned shortcut, restore it:

```powershell
devwt shortcut restore --taskbar --name "ABP Studio"
devwt shortcut restore --taskbar --name Rider
```

## Worktree does not appear

Run:

```powershell
devwt status
```

Then recreate or checkout the worktree so the post-checkout hook can call `devwt hook worktree-ready`.
