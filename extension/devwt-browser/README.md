# DevWT Browser Extension

This Chrome/Edge extension uses DevWT Gateway mode.

It does not rewrite `localhost` to a context IP. The browser URL, origin, cookies,
OAuth callback host, and most frontend tooling expectations stay on `localhost`.
When a user chooses a context, the extension:

1. Stores one active worktree context for the tab.
2. Adds a tab-scoped `X-DevWT-Context` rule for HTTP, HTTPS, WebSocket, and
   secure WebSocket requests to every localhost port.
3. Lets the active card define missing-port redirects to another live worktree
   in the same repository. A redirect overrides the active context only while
   the active worktree has no listener on that natural port. If that listener
   starts later, the extension removes the override automatically.
4. Hard reloads that tab with the new rules and bypasses its HTTP cache.
5. Closes the popup after the selection succeeds.
6. Can prefix the tab title with the active context, using its description or its Git
   ref when no description is set. This setting is off by default; disabling it
   or clearing the selection restores the page title.
7. Optionally groups selected tabs by the same label. This setting is off by
   default; enabling it groups existing selections and moves a tab when its
   context changes. Dragging any localhost tab into an extension-created group
   selects that group's active context and reloads it. A grouped tab that later
   opens another localhost port follows the same active context and its
   missing-port redirects. Disabling grouping removes only extension-managed
   memberships.
8. Persists tab-to-active-context, missing-port redirect, and
   managed-group-to-context selections in
   extension local storage. On browser startup or extension update, it recreates
   the tab-scoped request rules, badges, title labels, and managed context groups.
   Unmatched records stay pending while Chrome restores their tabs. Closing a
   Chrome window keeps records needed to reopen saved tab groups.

Context cards show the worktree name, Git ref, and the description set with
`devwt context describe`. IPv4 and IPv6 listeners for the same context and
natural port are presented as one `localhost` candidate. Status changes are
pushed into an open popup. Inspected responses expose the selected description
as `X-DevWT-Description`.

The popup search filters by context name, description, Git ref, context ID,
worktree path, and port. Press `/` while the popup is focused to jump to search;
press `Escape` to clear it. Active contexts sort first, while technical routing
details stay collapsed until requested.
Context cards offer `Use in this tab` plus explicit HTTP and HTTPS actions.
Opening a protocol action creates the new tab, applies the selected context,
and only then navigates to that localhost URL. The context list uses the popup's
single page scrollbar rather than a nested scrollbar.
If that context has listeners on other natural ports, `Other ports` lists them
inside the card with the same HTTP and HTTPS new-tab actions. Search also
matches those additional ports. Expanded port and technical-detail panels stay
open when a live status message redraws the cards.
The active card also lists `Worktree missing-port policy` under `Other ports`.
Only ports that are absent from the active worktree but live in another
worktree of the same repository are shown. The selected Automatic, explicit
provider, or No redirect policy is stored by DevWT under the active context ID
and natural port and applies to every extension tab using that worktree.
The active worktree always wins when it starts listening on the port.
DevWT Console `Settings > Browser Missing-Port Fallback` is only the default for
worktree/port pairs without a saved policy.
The extension displays an isolated Shadow DOM notice for an effective provider
redirect or automatic fallback. DevWT does not rewrite the HTTPS response body
or inject code into the application's JavaScript context. An unavailable
explicit provider remains fail-closed instead of silently choosing another
worktree.

The extension marks its selected-tab requests with
`X-DevWT-Allow-Fallback`; the gateway then evaluates the server-side
worktree/port policy or the Console default and strips the header before
forwarding. URL `devwt-context` selectors remove this marker and remain
fail-closed.

`Automatic` forces normal DevWT fallback for that worktree/port even when the
Console default is off. `Console default` removes the policy, and `No redirect`
forces fail-closed behavior. The selector does not collapse during live status
updates. Saving a choice keeps the popup open and updates every selected tab
using that worktree without reloading it.

When the assigned context has no listener for the tab's current port, it still
appears as the Active context card. The card marks the port as not listening,
expands `Other ports`, and offers Automatic, No redirect, and live
same-repository providers.
Main and additional port labels include the short backend process name supplied
by DevWT. If the name is unavailable, the extension shows the listener PID.

Each gateway TCP IP/port has an application handling mode. `Auto` detects HTTP
and inspects HTTPS when the DevWT gateway root is trusted for the machine.
`HTTP Inspect` terminates browser-side TLS, reads and removes the DevWT headers,
selects the tab's context, detects whether that backend speaks HTTP or HTTPS,
and forwards with the matching upstream transport.
`TLS Tunnel` keeps the original end-to-end TLS stream and uses browser/process
fallback routing. Use it for non-HTTP TLS protocols. `Raw` disables HTTP and TLS
detection for that endpoint.

Inspection accepts HTTP/1.1, HTTP/2 over TLS, and cleartext HTTP/2 prior knowledge,
then forwards requests through YARP, including WebSockets and gRPC.

## Install for Development

1. Open `chrome://extensions` or `edge://extensions`.
2. Enable developer mode.
3. Choose **Load unpacked**.
4. Select `extension/devwt-browser`.

DevWT service and Web UI must be running on `http://127.0.0.1:17776/`.
