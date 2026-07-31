# DevWT Command Matrix

Use this matrix only after `devwt status` confirms the repository/worktree is
registered, or when the user explicitly requests DevWT configuration. An
unregistered repository or unrelated application must use its normal commands;
do not register it automatically.

## Inspect

```powershell
devwt status
devwt service status
devwt ide watch list
devwt shell status
devwt gateway-cert status
devwt port process --port <port>
devwt port process --port <port> --context <context-id>
devwt port check --port <port>
devwt port check --port <port> --context <context-id>
```

Prefer `devwt status` and the service-hosted Console at
`http://127.0.0.1:17776/`. Use `devwt ui` only when deliberately running a
standalone foreground UI instance.

Console Routing shows one selected natural port and its candidate contexts.
Activity `Callers` uses adjacent Image, Process, and Session lists with one
inspector; `Timeline` shows the bounded newest-first history.

`port process` resolves the current registered worktree context when
`--context` is omitted and reports every live TCP/UDP backend binding with its
real process. `port check` runs the same query and returns `0` when a listener
exists, `1` when none exists, or `2` for a usage/context error.

## Register And Label

```powershell
devwt add
devwt add --name <repo>
devwt add --linked-repo <name> --linked-repo-path <path>
devwt remove
devwt remove --repo <name-or-id>
devwt pause --repo <name-or-id>
devwt pause --worktree <path>
devwt resume --repo <name-or-id>
devwt resume --worktree <path>
devwt context describe "<short task description>"
devwt context describe --worktree <path> "<short task description>"
devwt context describe --clear
```

`add`, `remove`, `pause`, and `resume` change persistent shared state. Use them
only when registration or lifecycle management is part of the request.

## Launch

```powershell
devwt run -- <program> [args...]
devwt run --worktree <path> -- <program> [args...]
devwt run --children-only -- <launcher> [args...]
devwt exec -- <program> [args...]
devwt terminal
devwt terminal --worktree <path> --shell powershell
devwt terminal --worktree <path> --shell cmd
```

- `run`: Hook target and descendants inside the selected context.
- `run --children-only`: Keep the parent natural; hook children at creation.
- `exec`: Use DevWT inside a context and pass through outside it.
- `terminal`: Keep a series of interactive commands in one context.

## Linked Worktrees

```powershell
devwt link map `
  --linked-repo <name> `
  --source <source-worktree> `
  --target <linked-worktree>
```

Use this when one product worktree must resolve a related repository's matching
worktree. It does not create a filesystem junction or symbolic link.

## Scoped Routing

For an existing interactive Chrome/Edge tab, select one active worktree with
`Use in this tab`. Under that active card, set a worktree missing-port policy
only when a natural port is absent locally. Automatic, a same-repository
provider, and No redirect are persisted by active context ID plus port and
apply to every extension tab using that worktree. A live listener in the active
worktree always wins. DevWT Console `Settings > Browser Missing-Port Fallback`
is the default only for worktree/port pairs without a saved policy.

```powershell
devwt proxy process target --pid <pid> --context <context-id>
devwt proxy process clear --pid <pid>

devwt proxy target --context <context-id> --port <port>
devwt proxy clear --port <port>

devwt proxy context --context <context-id>
devwt proxy clear
```

Scope order for manual choices:

1. Process override for one caller tree.
2. Session/image selection in the Console.
3. Per-port fallback.
4. Global-context fallback.

The Console supports process, image, and session defaults for all ports or one
observed port. The current CLI directly exposes process and fallback controls;
use the Console for session/image targets and session-rule configuration.
When only one eligible route exists for an IP/port/protocol, DevWT uses it
directly and reports `single-target`.

## Stop A Backend On A Virtual Port

```powershell
devwt proxy child stop --port <port> [--context <context-id>] [--protocol tcp|udp]
devwt proxy child kill --port <port> [--context <context-id>] [--protocol tcp|udp]
```

Prefer `stop`. Treat `kill` as destructive and require explicit authorization.
Specify context when multiple backends use the same original port.

## IDE And Store Launchers

```powershell
devwt ide watch add --name Rider --path "<rider64.exe>"
devwt ide watch add --name "Custom IDE" --path "<ide-host.exe>"
devwt ide watch add --name Codex --app-id "<AppID>"
devwt ide watch add --name Codex --package-family "<PFN>"
devwt ide watch remove --name <name>
devwt ide watch remove --all
```

For Store/MSIX apps, discover the stable AppID with:

```powershell
Get-StartApps | Where-Object Name -like "*<app>*"
```

Prefer AppID or package family to a versioned `WindowsApps` path. IDE watches
are persistent machine behavior; do not add or remove them for a one-off run.

## Persistent Shell Integration

```powershell
devwt shell install
devwt shell status
devwt shell uninstall
```

Installation hooks each new PowerShell process in children-only mode. It does
not replace command names: any future native executable is matched through the
registered context map. It modifies user profiles and requires a new terminal,
so do not change it implicitly.

## Certificates And Service

```powershell
devwt cert status
devwt cert trust
devwt cert clean

devwt gateway-cert status
devwt gateway-cert trust --user
devwt gateway-cert trust --machine
devwt gateway-cert clean

devwt service status
devwt service start
devwt service stop
devwt service restart
devwt service install --yes
devwt service uninstall --yes
```

Certificate trust/clean and service stop/restart/install/uninstall can affect
unrelated work. Require an explicit user request. Installation and machine
certificate trust may require elevation.

## Escape Hatch

For a single helper process that must bypass DevWT:

```powershell
$env:DEVWT_HOOK_DISABLE = "1"
<command>
Remove-Item Env:DEVWT_HOOK_DISABLE
```

Use only in that process tree. Do not persist it.
