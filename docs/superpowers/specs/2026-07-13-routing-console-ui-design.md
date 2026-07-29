# DevWT Routing Console UI Design

## Goal

Replace the growing single-page console with a task-focused tabbed interface and replace the single `context + port` active target with an explicit routing mode: one context for all ports or an independent context selection per port.

## Scope

This change covers the persisted routing state, control API, CLI proxy commands, gateway fallback resolution, and the built-in Web UI. Existing browser-, process-, application-, session-, and self-process routing signals remain intact and retain their current priority over the global fallback modes.

## Routing Model

The routing state has one active fallback mode:

- `GlobalContext`: one context ID applies to every requested port. A request is routed only when that context currently exposes the requested IP, port, and protocol.
- `PerPort`: each numeric port has an independent context ID. TCP and UDP share the same port selection. Changing one port cannot change another port's target.

The state retains both the global context selection and the per-port selections when the mode changes. Only the selected mode participates in routing. This lets users switch modes without losing prior configuration.

The existing `DevwtActiveTarget(ContextId, Port, Scheme)` value is migrated on load into a per-port selection. The scheme field is no longer used to choose a backend because the gateway proxies opaque TCP and UDP traffic. Existing persisted state must continue to deserialize.

Global and per-port targets are fallback decisions only. The gateway keeps this priority order:

1. Explicit request context
2. Browser-scoped target
3. Self-process listener match
4. Process, parent-process, or session context
5. Application default target
6. Selected global/per-port fallback mode
7. Last process context
8. Newest matching listener

## Control API And CLI

The control API supports three explicit operations through the existing active-target command surface:

- Select global mode and set its context.
- Select per-port mode and set or replace one port's context.
- Clear one per-port selection or clear the global selection.

CLI behavior remains backward compatible:

- `devwt proxy target --context <context-id> --port <port>` sets only that port and selects per-port mode.
- `devwt proxy context --context <context-id>` sets the global context and selects global mode.
- `devwt proxy clear --port <port>` clears only that port.
- `devwt proxy clear` clears the selection for the currently active mode.

Browser-scoped extension targets and application defaults remain port-specific and do not mutate the global fallback state.

## Information Architecture

The Web UI uses four tabs. The selected tab is stored in browser `localStorage`; Routing is the default when no preference exists.

### Routing

Routing is the primary workspace. It contains:

- A segmented mode control: `One context` and `Per port`.
- In global mode, one context selector and a compact summary of ports currently available in that context.
- In per-port mode, listeners grouped by numeric port. Each port group has one context selector, protocol badges, listener details, and a clear action.
- Port groups are independent. Updating a group does not rerender another group into a different selection.

The former `Active Proxy Target` summary and separate `Open Ports` section are removed. Their information is combined into this workspace.

### Contexts

Contexts contains the existing searchable context table, status filters, open-port indicators, and pause/resume actions. It remains optimized for scanning worktrees rather than choosing gateway targets.

### Activity

Activity contains Connection History. It adds lightweight filters for application/process, endpoint/port, context, and route reason. Existing application-default controls remain available on each relevant history row.

### Settings

Settings contains Runtime Backends, Session Rules, and Repositories. Session-rule creation remains a structured form; it is not replaced with raw JSON.

## Shared Header And Responsive Behavior

The sticky header contains DevWT identity, service/live status, Refresh, and the tab navigation. Repository, context, open-port, and update counts become a compact status strip rather than five large cards.

Desktop layouts use dense tables and unframed tab panels. Mobile layouts keep the tab bar horizontally scrollable, stack form fields, and allow tables to scroll without forcing the whole page wider than the viewport. Long paths remain truncated with their full value in a title attribute.

## State And Rendering

SignalR status updates continue to refresh the active tab. All tab panels remain in the document so existing element IDs and action handlers stay stable, but inactive panels are hidden with the `hidden` attribute. Rendering functions update their own panel and do not switch the current tab.

The tab selection is UI-only state and is never persisted in DevWT server state.

## Error Handling

- A global or per-port context must reference an existing context.
- A port must be in the valid TCP/UDP port range.
- If the selected context does not currently expose the requested port, that fallback produces no route and the gateway continues to the last-process/newest-listener fallbacks.
- Invalid control requests return the existing command error shape and do not partially update routing state.
- Legacy active-target state is migrated deterministically without deleting browser, process, or application targets.

## Testing

Tests will be added before implementation for:

- Persisted routing-state round-trip and legacy active-target migration.
- Independent per-port updates and clears.
- Global-context resolution across multiple ports.
- TCP and UDP sharing a per-port target.
- Existing stronger routing signals winning before fallback mode.
- CLI parsing and control requests for global context and port-specific clear.
- Web UI tab structure, Routing default, compact status strip, mode controls, and removal of the old duplicated active-target/open-port layout.

The complete .NET test suite and release build must pass. The installer bundle will be rebuilt, but it will not be installed on the main machine.
