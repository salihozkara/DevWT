# DevWT Activity History Grouping Design

## Goal

Make gateway activity useful for diagnosing and controlling routing without
turning the history store into an unbounded process inventory. Activity keeps a
chronological mode and gains a process-centric grouped mode with the hierarchy:

```text
Image
  Process
    Session
      Access history
```

Each image, process, and session scope can select one context for every port and
can add independent per-port overrides. Process selections apply to the PID and
its descendants.

## Scope

This change covers:

- session metadata in bounded connection-history entries;
- persisted image-wide and session-wide routing targets;
- persisted image-port, process-port, and session-port overrides;
- process-target actions in the Web UI;
- gateway precedence for configured process, session, and image targets;
- grouped and chronological Activity views;
- tests and operator documentation.

It does not add persistent history, process polling, a new session-rule format,
or a second server-side activity index.

## Activity Views

Activity has a `Grouped / Timeline` segmented control. The selected view is
stored in browser local storage independently from the selected top-level tab.

### Grouped

The grouped view is the default. It derives groups from the current bounded
history snapshot during rendering:

1. Normalize the image key from `ApplicationKey`, then `ProcessImagePath`, then
   an `Unknown image` sentinel.
2. Group image entries by normalized case-insensitive key.
3. Group each image by PID. Missing PIDs use an `Unknown process` group and do
   not expose process-target controls.
4. Group each process by resolved session ID. Missing session IDs use a
   `No session` group and do not expose session-target controls.
5. Show the matching chronological entries under the session group.

Image and process groups are collapsible. Session groups show their identity,
latest decision, observed ports, and entry count before their history rows.
Search and decision-reason filters run before grouping, so group counts and
contents describe the visible result set.

The same session ID can appear below multiple process groups. Its controls read
and write the same persisted session target, so an update in one branch is
reflected in every branch on the next status render.

### Timeline

Timeline preserves the existing flat, newest-first table and filters. Existing
image-default controls move to the grouped scope controls; timeline remains a
diagnostic list rather than duplicating target editors on every row.

## Scope Target Controls

### Image

An image group provides:

- an `All ports` context selector;
- one context selector per observed numeric port;
- clear actions for the all-ports target and each port override.

The per-port target takes precedence over the all-ports target. Existing
`ApplicationTargets` remain the per-port representation. A new image-wide
target collection stores one context per normalized application key.

### Process

A process group provides:

- an `All ports` context selector;
- one context selector per observed numeric port;
- clear actions for the all-ports target and each port override.

The existing `ProcessTargets` collection remains the all-ports representation.
A new process-port target collection stores overrides. Both selections apply to
that PID and its descendants through the existing ancestor walk, with the
per-port target taking precedence. Controls are unavailable when the PID is
unknown.

### Session

A resolved session group provides:

- an `All ports` context selector;
- one context selector per observed numeric port;
- clear actions for the all-ports target and each port override.

Session targets are keyed by the exact resolved session ID. A per-port session
target takes precedence over the session-wide target. A session without an
explicit target keeps the current natural behavior: a client routes to a
listener whose process resolves to the same session.

Only contexts that have a route matching the selected scope and port are
offered for per-port controls. All-ports selectors offer active contexts, but a
selection only resolves on ports where that context has a matching route.

## Persisted Model

Routing state adds these backward-compatible collections:

- image-wide targets: application key and context ID;
- process-port targets: PID, context ID, port, and scheme;
- session-wide targets: session ID and context ID;
- session-port targets: session ID, context ID, port, and scheme.

Existing `ApplicationTargets` continue to hold image-port targets. Existing
`ProcessTargets` continue to hold PID-wide targets. Normalization removes
invalid keys, PIDs, and ports, keeps the last duplicate for each logical key,
and sorts the collections deterministically. Existing routing files load with
empty new collections.

## Session Metadata Flow

When a gateway connection resolves its client process, it also resolves the
session ID using the current `ProcessSessionResolver` and runtime session rules.
The route decision carries that ID into the connection-history entry. The
history entry stores only the resolved string, not process observations,
environment dictionaries, or session-rule objects.

The status API returns the added session ID as part of each history entry. No
new polling endpoint is introduced.

## Routing Precedence

After explicit request and self-listener signals, configured and inferred
caller routing resolves in this order:

1. configured process-port target found by walking the PID and its ancestors;
2. configured process-wide target found by the same ancestor walk;
3. configured session-port target;
4. configured session-wide target;
5. natural same-session listener context;
6. existing context inferred from the caller process or ancestor;
7. image-port target;
8. image-wide target;
9. configured global/per-port gateway fallback;
10. last remembered process context;
11. newest matching listener.

Browser-scoped and explicit request-header routing keep their existing stronger
positions. A context target is ignored for a port when no matching route exists,
allowing resolution to continue to the next signal.

## Control API And Web Actions

The control API gains set/clear operations for image-wide, process-port,
session-wide, and session-port targets. Existing application-port and
process-wide operations remain unchanged.

The Web action contract exposes:

- set/clear process-wide target with PID;
- set/clear process-port target with PID and port;
- set/clear image-wide target with application key;
- set/clear image-port target with application key and port;
- set/clear session-wide target with session ID;
- set/clear session-port target with session ID and port.

Every action validates its key, context, and optional port before replacing only
the matching logical target. Unrelated scope and port selections are preserved.

## Memory Bound

`DevwtConnectionHistory` remains a fixed-capacity queue with a default capacity
of 200 entries. Adding session metadata does not change that capacity.

Grouped structures are temporary JavaScript maps and arrays derived from at
most the current 200 serialized entries. They are recreated during render and
are not retained in a second service-side cache. Expanded/collapsed UI state may
store only string group keys in the browser and must be pruned to keys present
in the current snapshot.

No process observation, environment block, or route table is copied into a
history entry. Tests verify capacity eviction and that grouped rendering uses
the supplied history snapshot rather than accumulating entries.

The existing process-identity cache and last-context map are also bounded as
part of this change. Expired or no-longer-observed PIDs are pruned during normal
gateway refresh/resolution, and each collection has a hard limit of 512 entries.
When the limit is exceeded, the least-recently-seen entries are removed first.
Session ID is added to the existing identity cache entry; no additional
per-process cache is introduced.

## Error Handling

- Missing PID disables process controls.
- Missing session ID disables session controls and renders `No session`.
- Missing image identity renders `Unknown image` and disables image controls.
- A target context without a route for the requested port is skipped at route
  resolution rather than producing a failed connection.
- Failed Web actions preserve the current status and show the control-handler
  validation message.

## Testing

Automated tests cover:

- persistence, normalization, and backward compatibility of new target lists;
- set, replace, and clear isolation for every new target scope;
- routing precedence and fallback when a configured context lacks a route;
- natural same-session routing when no explicit session target exists;
- session ID recording for TCP and UDP history;
- the 200-entry history capacity after the metadata addition;
- expiry and 512-entry caps for process identity and last-context caches;
- Web action mapping for process, image, and session targets;
- grouped/timeline controls, hierarchy helpers, filters, and absence of
  per-row target editors in timeline mode.

Browser verification covers grouped and timeline modes at desktop and mobile
viewports, including long image paths, unknown identities, nested expansion,
target editing, and horizontal overflow.
