# Routing And Diagnostics

## TCP Route Precedence

When the original IP, numeric port, and protocol have only one eligible route,
DevWT uses it immediately with reason `single-target`. Otherwise DevWT
evaluates usable candidates in this order:

1. Explicit DevWT request header.
2. Browser-scoped target.
3. Listener/self-process context.
4. Process target for the client or an ancestor, per-port before process-wide.
5. Session target, per-port before session-wide.
6. Listener with the same natural session identity.
7. Context inferred from the process or an ancestor.
8. Context cookie.
9. Image target, per-port before image-wide.
10. Active per-port or global fallback.
11. Last remembered context for the client process.
12. Newest matching listener.

Configured process/session/image/fallback selections are ignored when their
context has no route for the original bind IP, numeric port, and protocol. An
explicit tab/header assignment is authoritative and returns `502` instead of
continuing. UDP has no browser/header/cookie signal and starts from
listener/process identity before following session, image, fallback, remembered,
and newest routing.

Multiple processes in one context that bind the same nonzero IP/port receive
the same backend IP/port. DevWT does not allocate a second random backend port;
the processes' Winsock reuse/exclusive options decide whether the later bind
succeeds. The gateway treats successful shared bindings as one logical target
while retaining every listener PID for process routing and diagnostics. Port
`0` remains ephemeral.

Shifted-port retry remains bounded. `WSAEACCES` can skip Windows-excluded
backend ports. `WSAEADDRINUSE` can skip a candidate only when the live binding
map proves that the same context and physical endpoint belong to a different
natural port. Never retry `WSAEADDRINUSE` for another bind of the same natural
port; Winsock reuse/exclusive semantics must remain authoritative.

## Wrong Context

1. Inspect Activity and the response headers.
2. If reason is `newest`, no stronger usable identity or configured target
   matched.
3. Check whether the client PID in Activity is the real socket owner. For
   Playwright, Chromium child processes may own connections; target their root
   controller/browser ancestor or use a shared `DEVWT_SESSION_ID`.
4. Check process start times during ancestor traversal. DevWT rejects an older
   PID reused as a false parent.
5. Check the selected context has the requested listener.
6. Replace broad fallback routing with process, session, image, or browser scope.

Do not infer the source worktree from a displayed repository name alone. Trace
the live process/session and context IDs.

## Browser And HTTP

- The extension adds the active worktree context to every localhost request in
  the selected tab.
- Worktree missing-port policies appear only for natural ports absent from the
  active worktree. DevWT stores them by active context ID and port, applies them
  across every extension tab using that worktree, and ignores them whenever the
  active worktree starts listening on the port.
- `No redirect - stay here` preserves the active context header and therefore
  returns `502` when its listener is missing; it does not invoke normal fallback.
- A successful `Use in this tab` reloads that tab with cache bypass and closes
  the popup. A missing-port choice instead keeps the popup open, updates future
  requests without reloading the tab, and defers live redraws while its select
  has focus. Reload an unpacked extension once after replacing its files.
- The gateway removes internal DevWT request headers before forwarding.
- Inspected responses expose `X-DevWT-Context`,
  `X-DevWT-Description`, and `X-DevWT-Route-Reason`.
- HTTP/1.1, HTTP/2, WebSockets, gRPC, and inspected HTTPS use Kestrel/YARP.
- Raw TCP, TLS tunnel, and UDP cannot expose HTTP response headers.
- When only one candidate route exists, no manual selection should be needed.

For a shared service such as AuthServer, first verify the active worktree with
`devwt port process`. Keep that worktree active for the whole tab. If its
AuthServer port is closed, configure a missing-port redirect to the intended
same-repository provider. Never configure a redirect merely to override a live
listener in the active worktree.

For browser automation, prefer either:

- the extension for an existing interactive browser;
- a process target on the automation/browser root PID; or
- one `DEVWT_SESSION_ID` inherited by the listener and browser roots.

## HTTPS

Check:

```powershell
devwt gateway-cert status
```

`Auto` inspects TLS only when the DevWT root is trusted for the local machine;
otherwise it preserves opaque TLS. `HTTP Inspect` requires browser trust and a
backend HTTPS certificate valid for localhost. Use `TLS Tunnel` for non-HTTP
TLS, and `Raw` for custom protocols that must not be inspected.

Do not trust or clean certificates without explicit authorization.

## Browser Missing-Port Fallback And Notices

Use DevWT Console `Settings > Browser Missing-Port Fallback` as the default for
worktree/port pairs without a saved policy. The browser routing order is:

1. The active worktree's listener.
2. The active context/port policy: explicit provider, Automatic, or No redirect.
3. When no policy exists and the Console default is on, the normal DevWT
   browser, process, session, active-target, remembered-context, and newest-route
   decision chain.
4. Otherwise, fail-closed `502` for the explicit active worktree.

`Console default` clears the worktree/port policy. The extension marks selected
tab requests with `X-DevWT-Allow-Fallback`; the gateway evaluates the
server-side policy/default and removes the header before forwarding. URL
selectors and externally supplied explicit context headers remain strict. When
a redirect or automatic fallback is effective, the extension shows a
dismissible Shadow DOM notice. This is isolated extension UI; the gateway must
not rewrite HTML or JavaScript inside HTTPS responses. An assigned context with
no current-port listener remains visible as the Active card, with its
missing-port choices expanded inside the card.

## IDE Bypass

An ordinary IDE opened from the taskbar does not inherit PowerShell shell
functions. Use:

- `devwt run --children-only -- <ide>` for one controlled launch;
- a persistent `devwt ide watch add` only when the user asks for natural future
  IDE launches to be covered.

Use AppID or package family for Store/MSIX launchers because versioned executable
paths change after updates.

## Port Ownership

A process image containing `--DevWT-Proxy--` owns one original IP/port on behalf
of virtual backends. Killing it can force-stop every backend using that same
IP/port and suppress recreation until the routes disappear.

Use `devwt proxy child stop` or explicitly authorized `kill` for one context and
protocol. If an unrelated non-DevWT process owns the port, report that owner and
ask before terminating it.

## IPv6 And Missed Injection

Port-shift mode preserves normal `localhost` name resolution and supports
literal IPv4 and IPv6 binds. Output containing `::1:<port>` is not enough to
diagnose a missed injection because `getsockname()` reports the original
endpoint. Confirm that the context has an IPv6 row in `hook-port-bindings.tsv`
and that the application owns the shifted backend port.

If the normal application really owns the original port, restart it through
`devwt run`. The reliable path injects at process creation. IDE/event watching
is best effort when Windows kernel process-start events are unavailable and the
watcher falls back to polling.
