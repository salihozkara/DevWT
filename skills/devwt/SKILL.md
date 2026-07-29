---
name: devwt
description: Operate DevWT only for repositories, worktrees, and launchers explicitly registered with DevWT. Use after `devwt status` confirms the current worktree context, or when the user explicitly asks to configure or diagnose DevWT, for localhost isolation, same-port worktrees, process/session/browser routing, and virtual-port troubleshooting. Do not invoke DevWT merely because a task uses Git worktrees, localhost, an IDE, a browser, or a local application; unregistered repositories and applications must run normally.
---

# DevWT

Use DevWT to let applications keep their natural localhost IP and port while each
registered worktree receives an isolated backend port range. Prefer the narrowest
routing signal that represents the caller: browser, process, session, then
per-port fallback. Never treat every repository or application on the machine as
DevWT-managed.

## Start With State

1. Confirm the command and service before launching an application:

   ```powershell
   Get-Command devwt -ErrorAction SilentlyContinue
   devwt status
   ```

2. Confirm the intended working directory appears as a registered context in
   `devwt status`. If it does not, stop the DevWT workflow and run the original
   command normally. Do not run `devwt add` unless the user explicitly asks to
   register that repository. Do not infer registration from a repository label,
   the presence of a Git worktree, an old browser page, or a port alone.
3. Give the worktree a short human-readable task description when it is missing
   or stale:

   ```powershell
   devwt context describe "Review driver code"
   ```

   Run it from the worktree, or pass `--worktree <path>`. This is an upsert and
   appears in the Console, browser extension, and inspected HTTP response header
   `X-DevWT-Description`. Do not put secrets or full prompts in the description.
4. If DevWT is absent, the service is unavailable, or the path is unregistered,
   do not wrap the application command. Continue normally unless the user
   explicitly requested DevWT, in which case report the blocker. Do not install,
   uninstall, restart, upgrade, trust certificates, alter shell profiles, register
   IDE watches, rewrite shortcuts, or kill processes unless the user explicitly
   asks for that machine-wide or disruptive action.

## Decide Whether To Intervene

Use DevWT only after the registration check above succeeds, or for an explicit
DevWT management request, and one of these is true:

- Starting a local server or test host from a registered worktree.
- Multiple registered DevWT contexts can use the same natural IP and port.
- A browser, Playwright, MCP helper, IDE, or child process must reach the server
  started for a registered context, and its launcher is explicitly covered by a
  DevWT launch command, session, process target, browser extension, or IDE watch.
- A request already handled by DevWT was routed by `newest`, `first`, or another
  incorrect fallback.
- The user asks to select, describe, pause, resume, diagnose, or free a DevWT
  context or virtual port.

Do not add DevWT routing when:

- The repository/worktree is absent from `devwt status` and the user did not ask
  to register it.
- The application is unrelated to a registered context and was not launched or
  watched through DevWT.
- The command only works with remote endpoints or production infrastructure.
- No local process is started or contacted and localhost isolation is irrelevant.
- The process intentionally needs the host gateway rather than a worktree
  backend. For that process only, use:

  ```powershell
  $env:DEVWT_HOOK_DISABLE = "1"
  ```

  Do not make this environment value global.

## Choose The Launch Command

Enter this section only after confirming the worktree is registered. Otherwise
run the user's original command directly. For registered contexts, use one of
these paths:

| Situation | Command | Reason |
| --- | --- | --- |
| Start one server or tool in the current worktree | `devwt run -- <program> [args...]` | Reliable creation-time injection for the target and children. |
| Start from another directory | `devwt run --worktree <path> -- <program> [args...]` | Resolves the intended context explicitly. |
| Build a reusable runtime shim that may run inside or outside DevWT | `devwt exec -- <program> [args...]` | Uses DevWT inside a context and passes through outside one. |
| Work interactively for several commands | `devwt terminal` | Opens a context-aware PowerShell. |
| Launch an IDE or parent while only its children need isolation | `devwt run --children-only -- <launcher> [args...]` | Leaves the launcher itself natural and injects child runtimes. |

Use `devwt run` by default for agent-started long-lived processes. Use `exec`
only when pass-through outside registered contexts is intentional. Do not
replace a direct command with an IDE watch or shell installation for a one-off
task.

## Keep One Task Together

For an agent-owned group of listener, browser, and helper processes, set a stable
task identity before launching every root process:

```powershell
$env:DEVWT_SESSION_ID = "task:<stable-id>"
devwt run -- dotnet run
```

Launch the browser or Playwright controller with the same
`DEVWT_SESSION_ID`. Natural same-session routing then keeps task processes
together without a global fallback. Do not reuse one session ID across unrelated
tasks.

When the launcher already propagates a task variable such as
`CODEX_THREAD_ID`, use a matching session rule configured in the Console instead
of copying one task's context to every child. Session-rule changes are persistent;
make them only when the user asks to configure that launcher.

## Route A Caller

Apply the narrowest option that solves the ambiguity:

1. **Browser tab:** Use the DevWT Chrome/Edge extension and select
   `Use in this tab`.
   This request header is the strongest normal browser signal and permits
   request-by-request switching on inspected HTTP/HTTPS. A successful selection
   hard reloads that tab with its HTTP cache bypassed and closes the popup; a
   failure keeps the popup open.
   The selected context is the tab's active worktree and applies to every
   localhost port. If a required port is not listening in that worktree, open
   `Other ports` on the active card and configure its worktree missing-port
   policy. DevWT persists Automatic, an explicit same-repository provider, or
   No redirect under the active context ID and natural port. That policy applies
   to every extension tab using the worktree and is used only while the active
   worktree lacks the port; if its listener starts later, the active worktree
   wins automatically. Do not use this control to choose a different responder
   for a port already open in the active worktree.
   For a shared AuthServer, first keep the application worktree active. Only
   when its AuthServer port is closed, redirect that missing port to the intended
   sibling worktree. `Automatic` forces DevWT's normal decision chain for that
   worktree/port. `No redirect - stay here` forces a `502`, and `Console
   default` clears the worktree policy. DevWT Console
   `Settings > Browser Missing-Port Fallback` is only the default for
   worktree/port pairs without a policy.
   The extension shows a dismissible Shadow DOM notice for an effective redirect
   or automatic fallback. Do not implement this by
   rewriting the gateway's HTTPS response body: that can break CSP, compression,
   streaming, gRPC, WebSockets, and content lengths.
   The `Automatic`, `Console default`, and `No redirect - stay here` options
   must remain distinct. If the assigned context lacks the tab's current port,
   keep that context visible as the Active card and show the missing-port
   choices inside it. Do not redraw an open select on live status updates.
   Saving a missing-port choice keeps the popup open, must not reload the tab,
   and updates all extension tabs using the active worktree.
   A localhost URL may also select a context directly:

   ```text
   https://localhost:44334/health-status?devwt-context=ctx-replace-with-the-intended-context
   ```

   The extension captures `devwt-context` before the request reaches the
   backend, adds `X-DevWT-Context`, and removes the query parameter from the
   forwarded URL. The backend must never receive the selector in its query
   string. A URL selector always remains fail-closed when its context has no
   matching listener. It removes the extension's automatic-fallback opt-in even
   if the global Console setting is enabled.
   For Playwright MCP, set `X-DevWT-Context` on the dedicated MCP page before
   navigation instead of depending on the extension:

   ```javascript
   async (page) => {
     await page.setExtraHTTPHeaders({
       "X-DevWT-Context": "ctx-replace-with-the-intended-context"
     });
     const response = await page.goto("https://localhost:44334/", {
       waitUntil: "domcontentloaded"
     });
     return {
       status: response?.status(),
       context: response?.headers()["x-devwt-context"],
       reason: response?.headers()["x-devwt-route-reason"]
     };
   }
   ```

   Run this through the Playwright MCP code-execution tool as one operation so
   the first request cannot use a fallback. Reapply it for every newly created
   page. HTTP works directly; HTTPS requires `Auto` or `HTTP Inspect` handling
   and a DevWT gateway certificate trusted by the MCP browser. Read
   [references/playwright-mcp.md](references/playwright-mcp.md) for verification,
   cleanup, HTTP/2 behavior, and fallbacks.
2. **Agent/browser process:** Route the root client or controller PID:

   ```powershell
   devwt proxy process target --pid <pid> --context <context-id>
   ```

   Child callers can match through parent traversal. Clear the temporary override
   when done:

   ```powershell
   devwt proxy process clear --pid <pid>
   ```

3. **Session or image:** Set an `All ports` or per-port default in the Console
   Activity view. Prefer per-port when only one service is ambiguous.
4. **Fallback:** Use only when caller identity cannot be represented:

   ```powershell
   devwt proxy target --context <context-id> --port <port>
   ```

   This changes only that numeric port. Use the broader
   `devwt proxy context --context <context-id>` only when every ambiguous port
   should use the same context.

Do not set a fallback when only one candidate route exists; DevWT should select
that candidate directly. Do not use a global context to repair one process or
one port.

Explicit URL context selectors, Playwright request headers, and ordinary
`X-DevWT-Context` requests are authoritative. When the selected context is
unavailable, stop resolution and return `502 Bad Gateway`. The only exception
is an extension-managed request carrying `X-DevWT-Allow-Fallback`. The gateway
must then evaluate the server-side active-context/port policy first and the
Console default only when no policy exists. It labels automatic decisions
`browser-worktree-fallback-*` or `browser-fallback-*`, labels explicit providers
`browser-worktree-redirect`, and strips the internal header before forwarding.

## Diagnose Before Changing Routing

When traffic reaches the wrong application:

1. Run `devwt status`.
2. Query the original port in the current registered context:

   ```powershell
   devwt port process --port <port>
   devwt port check --port <port>
   ```

   Pass `--context <context-id>` when running outside that worktree. The process
   query checks both TCP and UDP and reports the real backend listener PID, not
   the DevWT gateway worker.
3. Open `http://127.0.0.1:17776/` and inspect Activity in Callers or Timeline
   mode. Check process, ancestors, image, session, endpoint, selected context,
   and decision reason.
4. For inspected HTTP/HTTPS, inspect:
   - `X-DevWT-Context`
   - `X-DevWT-Description`
   - `X-DevWT-Route-Reason`
5. Verify the selected context actually has a listener for the original IP,
   numeric port, and TCP/UDP protocol. A configured target with no matching
   listener is skipped.
6. Correct the narrowest stale scope and retry. Do not edit `routing.json`,
   `contexts.json`, or other state files for normal operations.

Read [references/routing-and-diagnostics.md](references/routing-and-diagnostics.md)
when the decision reason is unexpected, HTTPS inspection fails, an IDE bypasses
the context, or a port owner is being killed.

## Free A Virtual Port

Do not kill every `Devwt.Cli` process or the central service. Stop only the
backend listener represented by the original port:

```powershell
devwt proxy child stop --port <port> --context <context-id> --protocol tcp
```

Use `kill` only after graceful stop fails and the user authorized termination:

```powershell
devwt proxy child kill --port <port> --context <context-id> --protocol tcp
```

Killing a `--DevWT-Proxy--` owner directly is interpreted as freeing that
IP/port and can terminate every virtual backend for that same IP/port. Never do
this as a generic "port in use" fix.

## Cleanup And Reporting

- Clear temporary process or fallback overrides created for the task.
- Leave the worktree description unless it is no longer useful; clear it with
  `devwt context describe --clear` only when requested.
- Do not stop a user-owned application merely because the agent's test ended.
- Report the working directory, launch command, context ID/description, routing
  override created, and cleanup performed.

Read [references/commands.md](references/commands.md) for the complete practical
command matrix and persistent-change boundaries.
