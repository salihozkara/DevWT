const statusNode = document.getElementById("status");
const statusTextNode = document.getElementById("status-text");
const routesNode = document.getElementById("routes");
const tabSummaryNode = document.getElementById("tab-summary");
const searchInput = document.getElementById("context-search");
const clearSearchButton = document.getElementById("clear-search");
const resultsSummaryNode = document.getElementById("results-summary");
const refreshButton = document.getElementById("refresh");
const clearButton = document.getElementById("clear");
const groupTabsCheckbox = document.getElementById("group-tabs");
const showTabTitleCheckbox = document.getElementById("show-tab-title");
const statusPort = chrome.runtime.connect({ name: "devwt-status" });
let currentTab = null;
let currentStatus = null;
let deferredStatusRender = false;
const expandedRouteDetails = new Set();

refreshButton.addEventListener("click", () => refresh());
clearButton.addEventListener("click", () => clearSelection());
groupTabsCheckbox.addEventListener("change", () => updateTabGrouping());
showTabTitleCheckbox.addEventListener("change", () => updateTabTitle());
searchInput.addEventListener("input", () => renderCurrentView());
clearSearchButton.addEventListener("click", () => clearSearch());
document.addEventListener("keydown", (event) => {
  const target = event.target;
  const isTyping = target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement;
  if (event.key === "/" && !isTyping) {
    event.preventDefault();
    searchInput.focus();
  } else if (event.key === "Escape" && searchInput.value) {
    clearSearch();
  }
});
document.addEventListener("focusout", (event) => {
  if (!event.target?.matches?.("select[data-missing-port-redirect]")) {
    return;
  }

  setTimeout(() => {
    if (deferredStatusRender && !isMissingPortSelectActive()) {
      deferredStatusRender = false;
      renderCurrentView();
    }
  }, 0);
});
statusPort.onMessage.addListener((message) => {
  if (message?.type === "status" && message.value && currentTab) {
    currentStatus = message.value;
    if (isMissingPortSelectActive()) {
      deferredStatusRender = true;
      return;
    }
    render(message.value, currentTab);
  }
});
loadSettings();
refresh();

async function loadSettings() {
  try {
    const settings = await send({ type: "extension-settings" });
    groupTabsCheckbox.checked = settings.groupTabsByContext;
    showTabTitleCheckbox.checked = settings.showContextInTabTitle;
  } catch {
    groupTabsCheckbox.checked = false;
    showTabTitleCheckbox.checked = false;
  }
}

async function updateTabTitle() {
  const enabled = showTabTitleCheckbox.checked;
  showTabTitleCheckbox.disabled = true;
  try {
    const result = await send({
      type: "set-tab-title",
      enabled
    });
    showTabTitleCheckbox.checked = result.showContextInTabTitle;
    const count = result.updatedTabCount || 0;
    const failures = result.failedTabCount || 0;
    setStatus(
      failures
        ? `Tab title setting updated; ${failures} tab${failures === 1 ? "" : "s"} could not be changed.`
        : `Context tab titles ${result.showContextInTabTitle ? "enabled" : "disabled"}; ${count} tab${count === 1 ? "" : "s"} updated.`,
      failures ? "error" : "ok");
  } catch (error) {
    showTabTitleCheckbox.checked = !enabled;
    setStatus(error.message || String(error), "error");
  } finally {
    showTabTitleCheckbox.disabled = false;
  }
}

async function updateTabGrouping() {
  const enabled = groupTabsCheckbox.checked;
  groupTabsCheckbox.disabled = true;
  try {
    const result = await send({
      type: "set-tab-grouping",
      enabled
    });
    groupTabsCheckbox.checked = result.groupTabsByContext;
    const action = result.groupTabsByContext ? "grouped" : "ungrouped";
    const count = result.updatedTabCount || 0;
    const failures = result.failedTabCount || 0;
    setStatus(
      failures
        ? `Tab grouping updated; ${failures} tab${failures === 1 ? "" : "s"} could not be changed.`
        : `Tab grouping ${result.groupTabsByContext ? "enabled" : "disabled"}; ${count} tab${count === 1 ? "" : "s"} ${action}.`,
      failures ? "error" : "ok");
  } catch (error) {
    groupTabsCheckbox.checked = !enabled;
    setStatus(error.message || String(error), "error");
  } finally {
    groupTabsCheckbox.disabled = false;
  }
}

async function refresh() {
  setStatus("Connecting to DevWT...");
  try {
    const { status, tab } = await send({ type: "status" });
    currentTab = tab;
    currentStatus = status;
    render(status, tab);
  } catch (error) {
    currentStatus = null;
    setStatus(error.message || String(error), "error");
    resultsSummaryNode.textContent = "Status unavailable";
    clearSearchButton.hidden = true;
    routesNode.innerHTML = "";
  }
}

function renderCurrentView() {
  if (currentStatus && currentTab) {
    render(currentStatus, currentTab);
  }
}

function clearSearch() {
  searchInput.value = "";
  searchInput.focus();
  renderCurrentView();
}

function render(status, tab) {
  const searchTerm = searchInput.value;
  const endpoint = tab.endpoint;
  if (!endpoint) {
    tabSummaryNode.textContent = "Open a localhost page to select a context.";
    setStatus("This tab is not using localhost.", "error");
    const allRoutes = uniqueRoutes(status.routes || []);
    const visibleRoutes = filterRoutes(status, allRoutes, searchTerm);
    updateSearchMeta(visibleRoutes.length, allRoutes.length, searchTerm, "routes");
    routesNode.innerHTML = renderAllPorts(status, visibleRoutes, searchTerm);
    return;
  }

  tabSummaryNode.textContent = `${endpoint.scheme}://${endpoint.hostname || "localhost"}:${endpoint.port}`;
  const tabSelection = tab.selection || null;
  const listeningRoutes = uniqueRoutes(status.routes || [])
    .filter((route) => Number(route.port) === Number(endpoint.port));
  const assignedMissingRoute = buildAssignedMissingRoute(
    status,
    tabSelection,
    endpoint,
    listeningRoutes);
  const routes = assignedMissingRoute
    ? [assignedMissingRoute, ...listeningRoutes]
    : listeningRoutes;
  if (!routes.length) {
    setStatus(`No DevWT listeners are visible on localhost:${endpoint.port}.`, "error");
    const allRoutes = uniqueRoutes(status.routes || []);
    const visibleRoutes = filterRoutes(status, allRoutes, searchTerm);
    updateSearchMeta(visibleRoutes.length, allRoutes.length, searchTerm, "routes");
    routesNode.innerHTML = renderAllPorts(status, visibleRoutes, searchTerm);
    return;
  }

  const visibleRoutes = sortRoutes(
    filterRoutes(status, routes, searchTerm),
    tabSelection);
  setStatus(
    assignedMissingRoute
      ? `The active worktree is not listening on localhost:${endpoint.port}. Choose Automatic or another worktree below.`
      : `${listeningRoutes.length} candidate context${listeningRoutes.length === 1 ? "" : "s"} for localhost:${endpoint.port}.`,
    assignedMissingRoute ? "error" : "ok");
  updateSearchMeta(visibleRoutes.length, routes.length, searchTerm, "contexts");
  routesNode.innerHTML = visibleRoutes.length
    ? visibleRoutes.map((route) => renderRoute(status, route, tabSelection, endpoint)).join("")
    : renderEmptyState("No contexts match", "Try a context name, description, branch, ID, or worktree path.");
  bindRouteDetails();
  bindRouteButtons(endpoint);
  bindMissingPortRedirects();
}

function buildAssignedMissingRoute(status, tabSelection, endpoint, listeningRoutes) {
  if (!tabSelection?.contextId
    || listeningRoutes.some((route) => route.contextId === tabSelection.contextId)) {
    return null;
  }

  const context = (status.contexts || []).find((item) => item.id === tabSelection.contextId);
  return {
    contextId: tabSelection.contextId,
    repositoryId: context?.repositoryId || null,
    worktreeRootPath: context?.worktreeRootPath || "",
    port: Number(endpoint.port),
    protocol: "Tcp",
    listenIp: endpoint.hostname || "localhost",
    targetIp: null,
    targetPort: null,
    listenerProcessId: null,
    processName: null,
    variants: [],
    missingListener: true
  };
}

function renderAllPorts(status, routes, searchTerm) {
  if (!routes.length) {
    return searchTerm
      ? renderEmptyState("No routes match", "Try a different context, branch, port, or path.")
      : renderEmptyState("No open ports observed", "Start an app inside a DevWT context.");
  }

  const endpoints = [...new Map(routes.map((route) => [endpointLabel(route), route])).values()]
    .sort((a, b) => endpointLabel(a).localeCompare(endpointLabel(b)));
  return endpoints.map((route) => {
    const candidateCount = routes.filter((item) => endpointLabel(item) === endpointLabel(route)).length;
    return `
    <div class="route">
      <div class="route-head">
        <div class="route-identity">
          <span class="context-mark" aria-hidden="true">${escapeHtml(portMark(route.port))}</span>
          <div>
          <span class="route-title">${escapeHtml(endpointLabel(route))}</span>
            <p>${candidateCount} candidate context${candidateCount === 1 ? "" : "s"}</p>
          </div>
        </div>
        <div class="protocol-actions" aria-label="Open localhost port">
          ${renderProtocolLink(route, "http")}
          ${renderProtocolLink(route, "https")}
        </div>
      </div>
    </div>
  `;
  }).join("");
}

function contextOptionLabel(context, fallback) {
  const description = typeof context?.description === "string" ? context.description.trim() : "";
  const branch = typeof context?.gitRef === "string" ? context.gitRef.trim() : "";
  const name = typeof context?.name === "string" ? context.name.trim() : "";
  const primary = description || branch || name || fallback;
  return primary === fallback ? fallback : `${primary} · ${fallback}`;
}

function renderRoute(status, route, tabSelection, endpoint) {
  const context = (status.contexts || []).find((item) => item.id === route.contextId);
  const isActive = tabSelection?.contextId === route.contextId;
  const title = context?.name || route.contextId;
  const branch = context?.gitRef || "detached";
  const detail = context?.description || branch;
  const addressFamilies = routeAddressFamilies(route);
  const processLabel = routeProcessLabel(route);
  const otherPorts = otherRoutesForContext(status, route);
  const missingListener = route.missingListener === true;
  return `
    <article class="route ${isActive ? "active" : ""} ${missingListener ? "missing-listener" : ""}">
      <div class="route-head">
        <div class="route-identity">
          <span class="context-mark" aria-hidden="true">${escapeHtml(contextMark(title))}</span>
          <div>
            <span class="route-title" title="${escapeHtml(title)}">${escapeHtml(title)}</span>
            <p>${escapeHtml(detail)}</p>
          </div>
        </div>
        ${isActive ? "<span class=\"active-badge\">Active</span>" : ""}
      </div>
      <div class="route-chips">
        <span class="chip" title="${escapeHtml(branch)}">${escapeHtml(branch)}</span>
        ${missingListener
          ? `<span class="chip missing-listener-chip">${escapeHtml(endpointLabel(route))} · not listening</span>`
          : `<span class="chip endpoint-chip" title="${escapeAttr(`${endpointLabel(route)} · ${processLabel} · ${addressFamilies}`)}">${escapeHtml(endpointLabel(route))} · ${escapeHtml(processLabel)}</span>`}
      </div>
      ${missingListener ? `<div class="missing-listener-callout">
        <strong>This worktree is assigned to the tab, but this port is closed.</strong>
        <span>Choose Automatic or another same-repository worktree under Other ports.</span>
      </div>` : ""}
      ${renderOtherPorts(status, route, otherPorts, tabSelection, isActive)}
      <details class="route-details" data-detail-key="${escapeAttr(routeDetailKey("technical", route))}" ${routeDetailOpenAttribute("technical", route)}>
        <summary>Technical details</summary>
        <div class="route-body">
          <div><span class="muted">Context</span> <code title="${escapeHtml(route.contextId)}">${escapeHtml(route.contextId)}</code></div>
          <div><span class="muted">Branch</span> <code title="${escapeHtml(branch)}">${escapeHtml(branch)}</code></div>
          <div><span class="muted">Gateway</span> <code>${escapeHtml(endpointLabel(route))}</code>${missingListener ? " <span class=\"muted\">not listening</span>" : ` <span class="muted">${escapeHtml(addressFamilies)}</span>`}</div>
          ${missingListener ? "" : `<div><span class="muted">Backend</span> <code>${escapeHtml(backendLabel(route))}</code></div>
          <div><span class="muted">Process</span> <code title="${escapeAttr(processLabel)}">${escapeHtml(processLabel)}</code></div>`}
          <div><span class="muted">Worktree</span> <code title="${escapeHtml(route.worktreeRootPath)}">${escapeHtml(route.worktreeRootPath)}</code></div>
        </div>
      </details>
      ${missingListener ? "" : `<div class="route-actions">
        <button class="primary" type="button" data-context="${escapeAttr(route.contextId)}" data-port="${escapeAttr(route.port)}" data-scheme="${escapeAttr(endpoint.scheme)}">Use in this tab</button>
        <div class="open-new">
          <span>Open new</span>
          ${renderProtocolButton(route, "http")}
          ${renderProtocolButton(route, "https")}
        </div>
      </div>`}
    </article>
  `;
}

function filterRoutes(status, routes, searchTerm) {
  const query = normalizeSearch(searchTerm);
  if (!query) {
    return routes;
  }

  return routes.filter((route) => {
    const context = (status.contexts || []).find((item) => item.id === route.contextId);
    const contextRoutes = (status.routes || [])
      .filter((item) => item.contextId === route.contextId);
    const values = [
      context?.name,
      context?.description,
      context?.gitRef,
      context?.id,
      context?.worktreeRootPath,
      route.contextId,
      route.worktreeRootPath,
      route.port,
      route.targetPort,
      endpointLabel(route),
      backendLabel(route),
      routeProcessLabel(route),
      ...contextRoutes.flatMap((item) => [
        item.port,
        item.targetPort,
        endpointLabel(item),
        item.processName,
        item.listenerProcessId
      ])
    ];
    return normalizeSearch(values.filter((value) => value !== null && value !== undefined).join(" "))
      .includes(query);
  });
}

function sortRoutes(routes, tabSelection) {
  return [...routes].sort((a, b) => {
    const activeDifference = Number(tabSelection?.contextId === b.contextId)
      - Number(tabSelection?.contextId === a.contextId);
    return activeDifference
      || String(a.contextId).localeCompare(String(b.contextId));
  });
}

function normalizeSearch(value) {
  return String(value || "")
    .normalize("NFKD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase();
}

function updateSearchMeta(visibleCount, totalCount, searchTerm, noun) {
  const hasSearch = Boolean(normalizeSearch(searchTerm));
  resultsSummaryNode.textContent = hasSearch
    ? `${visibleCount} of ${totalCount} ${noun}`
    : `${totalCount} ${noun}`;
  clearSearchButton.hidden = !hasSearch;
}

function renderEmptyState(title, detail) {
  return `
    <div class="empty-state">
      <div>
        <span class="empty-icon" aria-hidden="true">⌕</span>
        <strong>${escapeHtml(title)}</strong>
        <p>${escapeHtml(detail)}</p>
      </div>
    </div>
  `;
}

function contextMark(value) {
  const parts = String(value || "DW")
    .split(/[^a-zA-Z0-9]+/)
    .filter(Boolean);
  if (parts.length >= 2) {
    return `${parts[0][0]}${parts[1][0]}`;
  }
  return (parts[0] || "DW").slice(0, 2);
}

function portMark(value) {
  return `:${String(value || "").slice(-2)}`;
}

function otherRoutesForContext(status, currentRoute) {
  return uniqueRoutes(status.routes || [])
    .filter((route) =>
      route.contextId === currentRoute.contextId
      && Number(route.port) !== Number(currentRoute.port))
    .sort((a, b) => Number(a.port) - Number(b.port));
}

function renderOtherPorts(status, currentRoute, routes, tabSelection, isActive) {
  const missingRedirects = isActive
    ? missingPortRedirectOptions(status, currentRoute.contextId, tabSelection, currentRoute.port)
    : [];
  if (!routes.length && !missingRedirects.length) {
    return "";
  }

  return `
    <details class="other-ports" data-detail-key="${escapeAttr(routeDetailKey("other-ports", currentRoute))}" ${currentRoute.missingListener ? "open" : routeDetailOpenAttribute("other-ports", currentRoute)}>
      <summary>
        <span>Other ports</span>
        <span class="other-ports-count">${routes.length} open${missingRedirects.length ? ` · ${missingRedirects.length} missing` : ""}</span>
      </summary>
      ${routes.length ? `<div class="other-ports-list">
        ${routes.map((route) => `
          <div class="other-port-row">
            <div class="other-port-identity">
              <div class="other-port-title">
                <strong>${escapeHtml(endpointLabel(route))}</strong>
                <span class="process-name" title="${escapeAttr(routeProcessLabel(route))}">${escapeHtml(routeProcessLabel(route))}</span>
              </div>
              <span>${escapeHtml(routeAddressFamilies(route))}</span>
            </div>
            <div class="protocol-actions" aria-label="Open ${escapeAttr(endpointLabel(route))} in a new tab">
              ${renderProtocolButton(route, "http")}
              ${renderProtocolButton(route, "https")}
            </div>
          </div>
        `).join("")}
      </div>` : ""}
      ${missingRedirects.length ? renderMissingPortRedirects(missingRedirects) : ""}
    </details>
  `;
}

function missingPortRedirectOptions(status, activeContextId, tabSelection, requestedPort = null) {
  const contexts = status.contexts || [];
  const contextsById = new Map(contexts.map((context) => [context.id, context]));
  const activeContext = contextsById.get(activeContextId);
  if (!activeContext?.repositoryId) {
    return [];
  }

  const routes = uniqueRoutes(status.routes || []);
  const activePorts = new Set(routes
    .filter((route) => route.contextId === activeContextId)
    .map((route) => Number(route.port)));
  const siblingRoutes = routes.filter((route) => {
    const context = contextsById.get(route.contextId);
    return route.contextId !== activeContextId
      && context?.repositoryId === activeContext.repositoryId
      && !activePorts.has(Number(route.port));
  });
  const globalFallbackEnabled = status.runtimeSettings?.browserFallbackOnMissingPort === true;
  const contextPolicies = (status.runtimeSettings?.browserMissingPortPolicies || [])
    .filter((policy) => policy?.contextId === activeContextId);
  const ports = new Set([
    ...siblingRoutes.map((route) => Number(route.port)),
    ...contextPolicies.map((policy) => Number(policy.port)),
    Number(requestedPort)
  ]);
  return [...ports]
    .filter((port) => Number.isInteger(port) && port > 0 && !activePorts.has(port))
    .sort((left, right) => left - right)
    .map((port) => {
      const policy = contextPolicies.find((candidate) => Number(candidate.port) === port);
      const policyMode = String(policy?.mode || "").toLowerCase();
      let selectedContextId = policyMode === "redirect"
        ? policy?.targetContextId || null
        : null;
      const candidates = [...new Set(siblingRoutes
        .filter((route) => Number(route.port) === port)
        .map((route) => route.contextId))]
        .map((contextId) => ({
          contextId,
          context: contextsById.get(contextId),
          unavailable: false
        }));
      for (const contextId of [selectedContextId].filter(Boolean)) {
        if (!candidates.some((candidate) => candidate.contextId === contextId)) {
          const selectedContext = contextsById.get(contextId);
          if (selectedContext?.repositoryId === activeContext.repositoryId
            && selectedContext.id !== activeContext.id) {
            candidates.push({
              contextId,
              context: selectedContext,
              unavailable: true
            });
          } else if (contextId === selectedContextId) {
            selectedContextId = null;
          }
        }
      }
      const mode = policyMode === "disabled"
        ? "none"
        : (policyMode === "automatic"
          ? "automatic"
          : (selectedContextId ? "explicit" : "default"));
      return {
        port,
        mode,
        selectedContextId,
        globalFallbackEnabled,
        candidates,
        availableCount: candidates.filter((candidate) => !candidate.unavailable).length
      };
    });
}

function renderMissingPortRedirects(redirects) {
  return `
    <section class="missing-port-redirects">
      <div class="missing-port-head">
        <strong>Worktree missing-port policy</strong>
        <span>Applies to every extension tab that uses this active worktree.</span>
      </div>
      ${redirects.map(({
        port,
        mode,
        selectedContextId,
        globalFallbackEnabled,
        candidates,
        availableCount
      }) => `
        <label class="missing-port-row">
          <span>
            <strong>localhost:${port}</strong>
            <small>${availableCount} sibling worktree${availableCount === 1 ? "" : "s"} open</small>
          </span>
          <select data-missing-port-redirect="${port}" data-global-fallback="${globalFallbackEnabled}" aria-label="Redirect for missing localhost port ${port}">
            <option value="automatic" ${mode === "automatic" ? "selected" : ""}>Automatic fallback · always</option>
            <option value="default" ${mode === "default" ? "selected" : ""}>Console default · ${globalFallbackEnabled ? "currently on" : "currently off"}</option>
            <option value="none" ${mode === "none" ? "selected" : ""}>No redirect · stay here</option>
            ${candidates
              .sort((left, right) =>
                contextOptionLabel(left.context, left.contextId)
                  .localeCompare(contextOptionLabel(right.context, right.contextId)))
              .map((candidate) => {
                const label = contextOptionLabel(candidate.context, candidate.contextId)
                  + (candidate.unavailable ? " · unavailable" : "");
                return `<option value="context:${escapeAttr(candidate.contextId)}" ${mode === "explicit" && candidate.contextId === selectedContextId ? "selected" : ""}>${escapeHtml(label)}</option>`;
              })
              .join("")}
          </select>
        </label>
      `).join("")}
    </section>
  `;
}

function bindMissingPortRedirects() {
  routesNode.querySelectorAll("select[data-missing-port-redirect]").forEach((select) => {
    select.addEventListener("change", async () => {
      const port = Number(select.dataset.missingPortRedirect);
      const value = select.value;
      select.disabled = true;
      setStatus(`Saving worktree policy for localhost:${port}...`);
      try {
        if (value === "automatic") {
          await send({ type: "set-port-fallback", port });
        } else if (value === "default") {
          await send({ type: "clear-port-redirect", port });
        } else if (value === "none") {
          await send({ type: "disable-port-redirect", port });
        } else {
          await send({
            type: "set-port-redirect",
            contextId: value.replace(/^context:/, ""),
            port
          });
        }
        await refresh();
        setStatus(`Saved worktree policy for localhost:${port}.`, "ok");
      } catch (error) {
        setStatus(error.message || String(error), "error");
        select.disabled = false;
      }
    });
  });
}

function isMissingPortSelectActive() {
  return document.activeElement?.matches?.("select[data-missing-port-redirect]") === true;
}

function routeDetailKey(kind, route) {
  return `${kind}:${route.contextId}:${route.port}`;
}

function routeDetailOpenAttribute(kind, route) {
  return expandedRouteDetails.has(routeDetailKey(kind, route)) ? "open" : "";
}

function bindRouteDetails() {
  routesNode.querySelectorAll("details[data-detail-key]").forEach((details) => {
    details.addEventListener("toggle", () => {
      const key = details.dataset.detailKey;
      if (details.open) {
        expandedRouteDetails.add(key);
      } else {
        expandedRouteDetails.delete(key);
      }
    });
  });
}

function renderProtocolLink(route, scheme) {
  const label = scheme.toUpperCase();
  const url = `${scheme}://${endpointUrlHost(route)}:${route.port}/`;
  return `<a class="protocol-button" href="${escapeAttr(url)}" target="_blank" rel="noreferrer" title="Open ${escapeAttr(url)}">${label}<span aria-hidden="true">↗</span></a>`;
}

function renderProtocolButton(route, scheme) {
  const label = scheme.toUpperCase();
  return `<button class="protocol-button" type="button" data-open-context="${escapeAttr(route.contextId)}" data-port="${escapeAttr(route.port)}" data-open-scheme="${scheme}" title="Open ${label} in a new tab">${label}<span aria-hidden="true">↗</span></button>`;
}

function bindRouteButtons(endpoint) {
  routesNode.querySelectorAll("button[data-context]").forEach((button) => {
    button.addEventListener("click", async () => {
      button.disabled = true;
      setStatus("Updating and reloading this tab...");
      try {
        await send({
          type: "select-context",
          contextId: button.dataset.context,
          port: Number(button.dataset.port),
          scheme: endpoint.scheme || "auto"
        });
        window.close();
      } catch (error) {
        setStatus(error.message || String(error), "error");
      } finally {
        button.disabled = false;
      }
    });
  });

  routesNode.querySelectorAll("button[data-open-context]").forEach((button) => {
    button.addEventListener("click", async () => {
      button.disabled = true;
      setStatus(`Opening ${button.dataset.openScheme.toUpperCase()} in a new tab...`);
      try {
        await send({
          type: "open-context",
          contextId: button.dataset.openContext,
          port: Number(button.dataset.port),
          scheme: button.dataset.openScheme
        });
        window.close();
      } catch (error) {
        setStatus(error.message || String(error), "error");
        button.disabled = false;
      }
    });
  });
}

async function clearSelection() {
  setStatus("Clearing this tab...");
  try {
    const { tab } = await send({ type: "status" });
    await send({
      type: "clear-context",
      port: tab.endpoint?.port || 80,
      scheme: tab.endpoint?.scheme || "auto"
    });
    await refresh();
  } catch (error) {
    setStatus(error.message || String(error), "error");
  }
}

async function send(message) {
  const response = await chrome.runtime.sendMessage(message);
  if (!response?.ok) {
    throw new Error(response?.error || "DevWT extension command failed.");
  }

  return response.value;
}

function setStatus(text, kind = "") {
  statusTextNode.textContent = text;
  statusNode.className = `notice ${kind}`.trim();
}

function uniqueRoutes(routes) {
  const grouped = new Map();
  for (const route of routes) {
    const key = `${route.contextId}:${route.protocol || "Tcp"}:${route.port}`;
    const existing = grouped.get(key);
    if (existing) {
      existing.variants.push(route);
    } else {
      grouped.set(key, { ...route, variants: [route] });
    }
  }

  return [...grouped.values()]
    .sort((a, b) => String(a.contextId).localeCompare(String(b.contextId)) || endpointLabel(a).localeCompare(endpointLabel(b)));
}

function endpointLabel(route) {
  return `localhost:${route.port}`;
}

function endpointUrlHost(route) {
  return "localhost";
}

function backendLabel(route) {
  const ports = [...new Set(route.variants.map((variant) => Number(variant.targetPort)))];
  return ports.length === 1
    ? `localhost:${ports[0]}`
    : ports.map((port) => `localhost:${port}`).join(", ");
}

function routeAddressFamilies(route) {
  const families = new Set(route.variants.map((variant) =>
    String(variant.listenIp || "").includes(":") ? "IPv6" : "IPv4"));
  return [...families].sort().join(" + ");
}

function routeProcessLabel(route) {
  const variants = route.variants?.length ? route.variants : [route];
  const names = [...new Set(variants
    .map((variant) => shortProcessName(variant.processName))
    .filter(Boolean))];
  if (names.length) {
    return names.length > 2
      ? `${names.slice(0, 2).join(", ")} +${names.length - 2}`
      : names.join(", ");
  }

  const processIds = [...new Set(variants
    .map((variant) => Number(variant.listenerProcessId))
    .filter((processId) => Number.isInteger(processId) && processId > 0))];
  if (processIds.length === 1) {
    return `PID ${processIds[0]}`;
  }
  return processIds.length > 1 ? `${processIds.length} processes` : "process unknown";
}

function shortProcessName(value) {
  const name = String(value || "")
    .trim()
    .split(/[\\/]/)
    .pop();
  return name ? name.replace(/\.exe$/i, "") : "";
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (ch) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;"
  }[ch]));
}

function escapeAttr(value) {
  return escapeHtml(value).replace(/`/g, "&#96;");
}
