# Installation

Build a bundle:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-DevWTInstallerBundle.ps1
```

Install from the generated `private\installer\DevWT` folder in an elevated PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-DevWT.ps1
```

The installer stores the CLI under `C:\Program Files\DevWT\app` and each hook
runtime under `C:\Program Files\DevWT\hooks\<version>`. It copies the browser
extension to `C:\Program Files\DevWT\extension\devwt-browser`. If applications still
have an older `devwt-hook.dll` loaded, the default install reports them but does
not terminate them. Restart those applications later, or explicitly use
`-KillHookedApplications` when a disruptive upgrade is intended.

Update only the managed CLI, service, gateway, and extension while preserving
the currently loaded hook DLL and running hooked applications:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Update-DevWTManaged.ps1
```

The managed updater stages a versioned app directory, switches and restarts the
Windows service, and verifies that the previous live backend PIDs and original
gateway endpoints return. It rolls the service path back on failure and copies
the bundled extension to `C:\Program Files\DevWT\extension\devwt-browser`.
Before switching versions it stops stale DevWT folder watchers so an orphaned
injector cannot keep applying an older hook to newly started processes.
Use this for CLI, service, gateway, Console, and extension-only releases.

When the release also changes the native hook, stage it for the service and
future processes without terminating applications that already loaded an older
hook:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Update-DevWTManaged.ps1 -UpdateHookRuntime
```

Existing hooked applications keep their loaded DLL until they are restarted.
New processes use the immutable hook version selected by the updated service.

Uninstall:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-DevWT.ps1
```

Normal uninstall reports applications with a DevWT hook DLL loaded but does not
kill them unless `-KillHookedApplications` is specified. To detach the service,
watcher, machine `Path`, and `DEVWT_STATE_ROOT` while deliberately leaving
installed files and running applications in place, use:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-DevWT.ps1 -DisconnectOnly
```

Clean reinstall while closing every DevWT runtime and application that still
has an installed hook DLL loaded:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Reinstall-DevWTClean.ps1
```

This preserves contexts and routing state. Add `-RemoveState` only when that
state must also be reset. The command terminates hooked applications, including
browsers, IDEs, terminals, and Codex.

The bundle includes the DevWT hook runtime used by the supported default path.

Install the PowerShell integration when you want arbitrary native commands to
enter DevWT automatically inside registered worktrees:

```powershell
devwt shell install
```

Open a new PowerShell tab after installing. The profile hooks that shell in
children-only mode without replacing command names. Each future native child is
matched against the registered worktree map and remains unchanged outside a
DevWT context.

Load the installed optional browser extension from:

```text
C:\Program Files\DevWT\extension\devwt-browser
```

Load it as an unpacked extension in Chrome or Edge when you want toolbar-based
gateway target selection while keeping browser traffic on `localhost`.
Extension `0.3.22` applies one active-worktree rule to the selected tab, hard reloads it with the
HTTP cache bypassed, and closes the popup after a successful `Use in this tab`.
The active card stores Automatic, explicit provider, or No redirect policies
for its context ID and natural port. They apply to every extension tab using
that worktree and are ignored whenever the active worktree starts listening.
Use `Settings > Browser Missing-Port Fallback` in DevWT Console as the default
for worktree/port pairs without a policy. `Automatic` forces fallback even when
that default is off, `Console default` removes the policy, and `No redirect -
stay here` forces a `502`. Effective redirects and automatic
fallback display a dismissible extension notice; the gateway does not rewrite
the application's HTTPS response body.
The popup always shows `Automatic` and `Console default`, keeps an assigned context visible when the
current port is closed, and lets its card choose a sibling provider. A
missing-port selection updates future requests without closing the popup or
reloading the tab.
Context tab grouping and the description-or-Git-ref tab title prefix are
independent popup options; both are off by default.
Tab/context and managed-group/context links survive browser restarts and
extension updates; the extension restores their request rules and visual state
as Chrome finishes restoring the corresponding tabs. Saved tab-group links are
retained when their Chrome window closes.
When grouping is enabled, dragging a localhost tab into a DevWT-created group
selects that context as the active worktree for every localhost port in the tab.
The popup search matches context names, descriptions, branches, IDs, worktree
paths, and ports. Press `/` to focus it.
Use the HTTP or HTTPS action on a context card to select that context and open
the chosen localhost scheme in a new tab.
Expand `Other ports` on the card to open another listener from the same context
without changing the current tab. Expanded panels stay open during live status
updates.
Main and additional port labels include the short backend process name. A PID
is shown when DevWT cannot read the name.
After updating unpacked files, press `Reload` once on `chrome://extensions` or
`edge://extensions`.

For transparent HTTPS inspection and tab-scoped context routing, explicitly
trust the DevWT gateway root for the machine from an elevated shell:

```powershell
devwt gateway-cert trust --machine
```

The installer does not silently add a trusted root. In `Auto`, TLS remains a TCP
tunnel until this command succeeds. The private root and server PFX files are
restricted to SYSTEM, Administrators, and the identity that created them.

For `TLS Tunnel` mode, trust the target application's localhost development
certificate when the browser requires it:

```powershell
devwt cert trust
```

`HTTP Inspect` detects whether the selected backend speaks HTTP or HTTPS and
uses that upstream transport independently of browser-side TLS. Kestrel and
YARP handle HTTP/1.1, browser-side HTTP/2, cleartext HTTP/2 prior knowledge,
WebSockets, and gRPC. Use `TLS Tunnel` for opaque TLS protocols or `Raw` to
disable application-protocol detection for the entire TCP endpoint.
