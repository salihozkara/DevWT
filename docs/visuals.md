# DevWT Visual Tour

The screenshots use a fictional Northwind repository and contain no local
machine or private project data.

## Browser extension

The popup keeps the browser URL on `localhost` while making the selected
worktree and its available ports visible.

![DevWT extension with an active checkout context](assets/screenshots/extension-active-context.png)

When the selected worktree does not listen on the current port, the popup shows
the available providers and the saved missing-port policy without hiding the
active context.

![DevWT extension missing-port policy](assets/screenshots/extension-missing-port-policy.png)

## Web Console

The Overview summarizes registered repositories, active contexts, observed
ports, and recent decisions.

![DevWT Web Console overview](assets/screenshots/web-console-overview.png)

Routing shows every context that owns a natural localhost port and the selected
target for that port.

![DevWT Web Console per-port routing](assets/screenshots/web-console-routing.png)

## Architecture

![DevWT architecture](assets/diagrams/architecture.svg)

## Routing decision

![DevWT routing decision](assets/diagrams/routing-decision.svg)
