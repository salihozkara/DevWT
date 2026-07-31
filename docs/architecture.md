# Architecture

DevWT hook-core has five runtime pieces:

1. CLI: registers repos, starts runtime processes with DevWT enabled, and exposes service/gateway controls.
2. Windows service: owns state, Git hook registration, worktree sync, optional gateway, and Web UI.
3. Hook runtime: `devwt-hook.dll`, `devwt-hook-launcher.exe`, and `devwt-folder-watcher.exe`.
4. Web UI/gateway: expose state, observed listeners, and optional proxy target controls.
5. Browser extension: optional Chrome/Edge toolbar for selecting a gateway target while keeping browser URLs on `localhost`.

State is stored under `DEVWT_STATE_ROOT` or `%ProgramData%\DevWT`:

- `repos.json`
- `contexts.json`
- `routing.json`
- `runtime.json`
- `hook-port-bindings.tsv`

Each context receives a deterministic localhost backend port range. The
supported hook-core runtime rewrites TCP/UDP binds in user mode by preserving
the requested bind IP and moving only the port to a private backend port in the
context's deterministic range. It records the original IP/port and backend
IP/port mapping, and keeps `getsockname()` natural so the app still believes it
owns the original endpoint. The default path stays in user mode and does not
require loopback aliases.

Repeated binds to the same nonzero IP/port in one context are rewritten to the
same backend IP/port; DevWT does not retry on another random port. The real
Winsock `bind()` call therefore retains authority over sharing and exclusivity:
socket options such as `SO_REUSEADDR` and `SO_EXCLUSIVEADDRUSE` determine
whether another process can share the endpoint or receives the native bind
error. Port `0` remains an application-requested ephemeral bind and is not
shifted.

Many Windows runtimes resolve `localhost` to both IPv4 `127.0.0.1` and IPv6
`::1` before they call `bind()`. In port-shift mode DevWT leaves name
resolution, command arguments, configuration, and child environment blocks
unchanged, then shifts both `AF_INET` and `AF_INET6` binds while preserving the
selected address. DevWT does not recognize or modify runtime-specific settings.
Literal IPv4 and IPv6 binds use the same port-shift path.

The reliable direct path is process creation-time injection through `devwt run`.
Normal mode hooks the target process and its children. Children-only mode hooks
only process creation in the target process; the target keeps its own localhost
behavior, while child runtime/app processes are injected before they can bind.

For natural IDE use, `devwt ide watch add` stores explicit IDE parent executable
paths or Microsoft Store/MSIX package-family selectors. The service watcher
injects those parent processes in children-only mode after they appear, without
modifying taskbar shortcuts, Start menu entries, file associations, Jump Lists,
or IDE installations. This is the preferred shape for IDEs and launchers such
as Rider, custom IDEs, or Store-installed apps such as Codex.

The folder watcher performs an initial process scan, then tries to subscribe to
Windows process-start events from the service process. If that event session is
unavailable, it falls back to the
polling scan. Folder matching is executable-agnostic: any process launched from
or located under a registered worktree is eligible, apart from DevWT's own
runtime and proxy processes. The fallback remains best-effort for already-running
matched processes and cannot guarantee catching a process that binds immediately
before it is injected.

`devwt exec -- <program>` is the safe runtime shim target. Inside a registered
context it launches the program through the hook runtime; outside DevWT contexts
it passes through to the real program. `devwt shell install` injects each new
PowerShell process in children-only mode. It does not replace named commands;
every future native child is resolved through the same context map. `devwt run`,
`devwt exec`, child injection, and folder matching all accept arbitrary
executable images.

`DEVWT_HOOK_DISABLE=1` is a process-local escape hatch for management clients
that must bypass context bind/connect rewriting, such as diagnostics or helper
browsers that need to talk to the host gateway directly.

The browser extension intentionally uses gateway mode, not direct IP redirects.
It installs tab-scoped Chrome session rules that add DevWT-only request headers
for `localhost`, `127.0.0.1`, and `[::1]`. After a successful target change,
the extension hard reloads the selected tab with its HTTP cache bypassed and
closes the popup. The gateway resolves the target context from those headers
and strips them before forwarding.

Before evaluating caller identity, TCP and UDP immediately use an eligible
route when the original IP, numeric port, and protocol have only one candidate.
The decision reason is `single-target`. With multiple candidates, TCP resolves
a route in this order:

1. Explicit DevWT request header.
2. Browser-scoped target.
3. Listener/self-process context.
4. Configured process target, checking process-port before process-wide while walking the client and its ancestors.
5. Configured session target, checking session-port before session-wide.
6. A listener with the same natural session identity.
7. Context inferred from the process or its ancestors.
8. Context cookie.
9. Configured image target, checking image-port before image-wide.
10. The configured global-context or per-port fallback mode.
11. Last remembered context for the client process.
12. The newest matching listener for the original IP and port.

A candidate is used only when that context has a matching route for the
requested IP, numeric port, and protocol. UDP starts at listener/self-process
context and follows the same process, session, image, fallback, remembered, and
newest order when Windows can identify the endpoint owner. Request headers,
browser targets, and HTTP context cookies do not apply to UDP.

The configured fallback mode is mutually exclusive: `GlobalContext` uses one
context for every port, while `PerPort` stores an independent context for each
numeric port. TCP and UDP listeners on the same numeric port share the per-port
selection. Both configurations remain persisted when the active mode changes.

The Windows service supervises one gateway owner worker for each original
IP/port pair. TCP and UDP for that pair live in the same worker, so Windows port
tools report a narrow process instead of the central service. The worker's
executable alias contains the observed backend image name(s), and the complete
set is also carried in its command line. If the worker exits unexpectedly,
DevWT treats the exit as an explicit port-release request: it force-stops every
backend listener routed from that IP/port, suppresses worker recreation while
any route remains, and leaves unrelated ports running. An intentional service
shutdown stops workers without terminating backend applications.

Session identities come from the ordered rules in `runtime.json`. A rule can
match a process name, image path, command line, or environment variable and can
derive the identity from an environment value, process ID, root process ID, or
command-line regular expression. Environment-only matching is supported for
launchers such as Codex that intentionally propagate a task ID to their child
processes. Parent traversal rejects parent PIDs that started after the child,
preventing PID reuse from joining unrelated processes. Natural same-session
routing compares the client identity with listener process identities and does
not persist process environment data.

When a context map exists, child hook propagation requires the child working
directory to match a registered context. A full-hooked process cannot pass its
context to a child launched for a different task directory merely because the
parent already carries a context.

Gateway workers send activity to the service over the local control pipe through
a bounded 200-entry queue. The service keeps a separate bounded in-memory queue
of 200 decisions. The Web UI presents this temporary snapshot in a flat
Image -> Process -> Session caller explorer with one inspector, or as a
diagnostic timeline. Scoped defaults are persisted in `routing.json`; history
is not. Process identity and last-context caches are bounded to 512 entries
each. Identity entries expire after five seconds and last-context entries after
five minutes, with inactive processes pruned from both caches.

The original IP/port remains owned by the outer TCP gateway so raw TCP and TLS
tunneling stay available. HTTP and inspected HTTPS connections are relayed to
an internal Kestrel endpoint and forwarded by YARP. The original client process
identity is carried across that internal hop. Routing then runs per HTTP request
or HTTP/2 stream, while YARP handles keep-alive, framing, streaming, and protocol
upgrades.

TCP transport and application handling are separate decisions. The hook records
whether the bound socket is TCP or UDP; the gateway never infers that from packet
contents. Each TCP IP/port then selects `Auto`, `HTTP Inspect`, `TLS Tunnel`, or
`Raw`. `Auto` recognizes HTTP/1.1, the cleartext HTTP/2 prior-knowledge preface,
and TLS ClientHello. `HTTP Inspect` forces the Kestrel/YARP path. `TLS Tunnel`
keeps encrypted TLS bytes opaque while recognized cleartext HTTP remains
inspectable. `Raw` skips sniffing and immediately establishes an L4 connection.
UDP datagrams always remain opaque.

TLS inspection terminates browser TLS with the DevWT localhost certificate,
reads the tab-scoped context header, strips all DevWT headers, detects whether
the selected backend speaks plain HTTP or HTTPS, and uses the matching upstream
transport. An HTTPS upstream is still validated for certificate name, validity,
and server-auth usage. `Auto` chooses TLS inspection only when the DevWT root is
trusted in the local-machine certificate store. TLS handling is necessarily
scoped to IP/port because the context header is encrypted until after the
handling mode has already been selected.

Inspection accepts HTTP/1.1, HTTP/2 over TLS, and cleartext HTTP/2 prior knowledge,
and supports WebSockets and gRPC. Cleartext HTTP/2 prior knowledge is preserved
for an h2c backend. When browser-side HTTP/2 terminates at DevWT and the selected
backend is plain HTTP/1.1, YARP uses HTTP/1.1 upstream. For verified local target
addresses, HTTPS upstream validation allows an untrusted development chain only
after the certificate name, validity period, and server-auth usage are checked.

Normal installs place hook builds in immutable
`C:\Program Files\DevWT\hooks\<version>` directories and point the stable app
entry at the selected hook root. Managed updates stage CLI/service builds under
`app-versions\<version>`, preserve the active hook root and backend processes,
switch only the Windows service path, verify the Web UI and previous gateway
endpoints, and roll back that path if verification fails.
