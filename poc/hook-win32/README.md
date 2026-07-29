# DevWT Win32 Hook POC

This is an isolated proof of concept for driverless localhost bind rewriting.

It starts a target process suspended, injects `devwt-hook.dll`, then resumes the
target. The DLL patches imported Winsock functions. In the current port-shift
mode it:

- moves `bind(<ip>:<port>)` to the context's deterministic backend port while
  preserving the IPv4 or IPv6 address
- retries the next shifted backend port when Windows rejects a candidate with
  `WSAEACCES`, up to 512 total attempts by default; set
  `DEVWT_HOOK_BIND_MAX_ATTEMPTS` to a value from 1 through 1024 to override the
  process-local limit; explicit overrides propagate to child processes without
  pinning an older hook version's default after an upgrade
- leaves `connect(<ip>:<port>)`, `getaddrinfo(localhost)`, and child process
  environment blocks unchanged so clients reach the gateway and applications
  retain their natural configuration
- masks `getsockname()` back to the complete original endpoint

`WSAEADDRINUSE` is not retried. This preserves the socket reuse and exclusivity
semantics when another application in the same context already owns the logical
port.

The older address-alias mode remains available when no port offset is supplied.
That mode rewrites IPv4 bind/connect and localhost name resolution to the
configured `127.80.x.y` loopback addresses without adapter aliases or admin
privileges.

Build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-HookPoc.ps1
```

Smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-HookPocSmoke.ps1
```

IPv4/IPv6 port-shift smokes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-PortShiftHookSmoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-PortShiftIpv6HookSmoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-PortShiftAccessDeniedFallbackSmoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-PortShiftAccessDeniedFallbackSmoke.ps1 -MaxBindAttempts 32 -ExpectLimitReached
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-CrossVersionHookReinjectionSmoke.ps1
```

The cross-version smoke verifies that a watcher update reloads an existing
DevWT hook from its original version directory instead of loading a second hook
DLL into the application. The watcher uses both Toolhelp and PSAPI module
enumeration and fails closed when neither can verify the target. Existing
applications keep their loaded hook version until they restart.

Connect rewrite smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ConnectHookSmoke.ps1
```

Hook disable smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-HookDisableSmoke.ps1
```

`DEVWT_HOOK_DISABLE=1` is a process-local bypass. When set, the DLL remains
loaded but does not rewrite bind/connect calls and does not inject child
processes.

Node localhost bind smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-NodeLocalhostBindSmoke.ps1
```

Manual example:

```powershell
.\artifacts\devwt-hook-launcher.exe --bind-ip 127.80.0.10 -- .\artifacts\devwt-bind-probe.exe --port 55251 --label a
.\artifacts\devwt-hook-launcher.exe --bind-ip 127.80.0.11 -- .\artifacts\devwt-bind-probe.exe --port 55251 --label b
```

Force-folder style POC:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ForceFolderWatcherSmoke.ps1
```

`devwt-folder-watcher.exe` runs as a background user-mode watcher. It performs an
initial process scan, can listen for Windows process-start events with
`--process-events`, matches current directory or image path against configured
folders without a runtime image allowlist, writes a PID-scoped hook config file,
and injects `devwt-hook.dll`.
This proves the force-folder shape without a custom driver, but the fallback
polling path is not race-free for processes that bind immediately after process
start. A creation-time parent hook is still the strongest user-mode path for
fully natural fast-bind coverage.

Child process inheritance smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ChildProcessHookSmoke.ps1
```

Children-only parent hook smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ChildrenOnlyHookSmoke.ps1
```

Children-only watcher smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ChildrenOnlyWatcherSmoke.ps1
```

Children-only package-family watcher smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ChildrenOnlyPackageFamilyWatcherSmoke.ps1
```

Children-only process-event watcher smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ChildrenOnlyEventWatcherSmoke.ps1
```

The process-event smoke requires permission to start a kernel process ETW
session. A normal user shell may report it as skipped with
`PROCESS_EVENTS_UNAVAILABLE`; the installed service runs the watcher with the
permissions needed for this path.

Children-only mode does not rewrite the parent process' own Winsock calls. It
only patches process creation so children are injected before they start. This
is intended for IDEs and launchers where the host process should keep its own
network behavior.

PowerShell Start-Process + localhost smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-CwdPowerShellStartProcessNodeLocalhostSmoke.ps1
```

When a hooked parent process starts a child with `CreateProcessW/A`, the hook
writes the child PID config and injects the same hook DLL into the child. This
matches common development flows where `npm`, `dotnet`, shell scripts, or IDE
tooling spawn the actual server process. The child is created suspended,
injected, and then resumed, so this path does not rely on the child having a
startup delay before binding.

Existing terminal current-directory smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-CwdTerminalWatcherSmoke.ps1
```

This starts `cmd.exe` outside a context, changes its current directory into a
mapped folder, lets the watcher inject by CWD, and then starts the child server.
It models an already-open terminal after `cd <worktree>`, followed by an
intermediate development tool such as `npm` or `dotnet` that has startup time
before launching or binding the actual server.

If an already-open terminal immediately starts a tiny executable that binds at
process start, the watcher can still lose the race. A terminal that was hooked
before spawning the child is stronger: `Run-ChildProcessHookSmoke.ps1` covers
that path without a child startup delay.

Fast-bind race measurement:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ForceFolderFastRace.ps1 -Iterations 10
```

On the current POC this is expected to report race losses for immediate bind
processes. That is the practical boundary of a polling user-mode force-folder
watcher.
