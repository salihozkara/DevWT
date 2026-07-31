const DEVWT_STATUS_URL = "http://127.0.0.1:17776/api/status";
const DEVWT_ACTION_URL = "http://127.0.0.1:17776/api/action";
const DEVWT_STATUS_WS_URL = "ws://127.0.0.1:17776/hubs/status";
const SIGNALR_RECORD_SEPARATOR = String.fromCharCode(30);
const RULE_ID_SLOT_COUNT = 10000000;
const URL_RULE_ID_BASE = 20000000;
const PORT_RULE_ID_BASE = 1100000000;
const PORT_RULE_IDS_PER_TAB = 50;
const GROUP_TABS_SETTING_KEY = "groupTabsByContext";
const TAB_TITLE_SETTING_KEY = "showContextInTabTitle";
const MANAGED_GROUP_KEY_PREFIX = "group:";
const TAB_KEY_PREFIX = "tab:";
const ALLOW_FALLBACK_HEADER = "X-DevWT-Allow-Fallback";
const LOCALHOST_REGEX = "^(https?|wss?)://(localhost|127\\.0\\.0\\.1|\\[::1\\])(:[0-9]+)?(/|$)";
const RESOURCE_TYPES = [
  "main_frame",
  "sub_frame",
  "stylesheet",
  "script",
  "image",
  "font",
  "object",
  "xmlhttprequest",
  "ping",
  "csp_report",
  "media",
  "websocket",
  "other"
];
let statusCache = null;
let statusSocket = null;
let statusReconnectTimer = null;
let persistedStateRestoreTimer = null;
let tabRuleReconcileTimer = null;
const statusSubscribers = new Set();
const managedGroupMoves = new Set();
const groupedTabContextUpdates = new Map();

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  handleMessage(message, sender)
    .then((value) => sendResponse({ ok: true, value }))
    .catch((error) => sendResponse({ ok: false, error: String(error && error.message ? error.message : error) }));
  return true;
});

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== "devwt-status") {
    return;
  }

  statusSubscribers.add(port);
  port.onDisconnect.addListener(() => statusSubscribers.delete(port));
  if (statusCache) {
    port.postMessage({ type: "status", value: statusCache.value });
  }
});

async function handleMessage(message, sender) {
  switch (message?.type) {
    case "status":
      return getStatus();
    case "select-context":
      return selectContext(message.contextId, Number(message.port));
    case "open-context":
      return openContext(message.contextId, Number(message.port), message.scheme);
    case "clear-context":
      return clearContext();
    case "set-port-redirect":
      return setPortRedirect(Number(message.port), message.contextId);
    case "set-port-fallback":
      return setPortFallback(Number(message.port));
    case "disable-port-redirect":
      return disablePortRedirect(Number(message.port));
    case "clear-port-redirect":
      return clearPortRedirect(Number(message.port));
    case "routing-notice":
      return getRoutingNotice(sender?.tab);
    case "tab-context-label":
      return getTabContextLabel(sender?.tab?.id);
    case "extension-settings":
      return getExtensionSettings();
    case "set-tab-grouping":
      return setTabGroupingEnabled(message.enabled === true);
    case "set-tab-title":
      return setTabTitleEnabled(message.enabled === true);
    default:
      throw new Error(`Unknown DevWT extension message: ${message?.type || "empty"}`);
  }
}

async function getStatus() {
  const tab = await getActiveTabEndpoint();
  const storedSelection = tab.id
    ? (await chrome.storage.local.get(tabKey(tab.id)))[tabKey(tab.id)] || null
    : null;
  return {
    status: await fetchStatus(),
    tab: {
      ...tab,
      selection: normalizeTabSelection(storedSelection, tab)
    }
  };
}

async function fetchStatus() {
  ensureStatusSocket();
  if (statusCache) {
    return statusCache.value;
  }

  await delay(250);
  if (statusCache) {
    return statusCache.value;
  }

  const response = await fetch(DEVWT_STATUS_URL, { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`DevWT status failed: HTTP ${response.status}`);
  }

  const value = await response.json();
  statusCache = {
    value,
    updatedAt: Date.now()
  };
  return value;
}

async function fetchFreshStatus() {
  const response = await fetch(DEVWT_STATUS_URL, { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`DevWT status failed: HTTP ${response.status}`);
  }

  const value = await response.json();
  statusCache = {
    value,
    updatedAt: Date.now()
  };
  return value;
}

function ensureStatusSocket() {
  if (statusSocket && [WebSocket.CONNECTING, WebSocket.OPEN].includes(statusSocket.readyState)) {
    return;
  }

  clearStatusReconnectTimer();
  const socket = new WebSocket(DEVWT_STATUS_WS_URL);
  let buffer = "";
  statusSocket = socket;
  socket.onopen = () => {
    socket.send(JSON.stringify({ protocol: "json", version: 1 }) + SIGNALR_RECORD_SEPARATOR);
  };
  socket.onmessage = (event) => {
    buffer += String(event.data || "");
    let index = buffer.indexOf(SIGNALR_RECORD_SEPARATOR);
    while (index >= 0) {
      const frame = buffer.slice(0, index);
      buffer = buffer.slice(index + 1);
      if (frame) {
        handleStatusFrame(frame);
      }

      index = buffer.indexOf(SIGNALR_RECORD_SEPARATOR);
    }
  };
  socket.onerror = () => socket.close();
  socket.onclose = () => {
    if (statusSocket === socket) {
      statusSocket = null;
    }

    scheduleStatusReconnect();
  };
}

function handleStatusFrame(frame) {
  let message;
  try {
    message = JSON.parse(frame);
  } catch {
    return;
  }

  if (message.type === 1 && message.target === "status" && message.arguments?.[0]) {
    statusCache = {
      value: message.arguments[0],
      updatedAt: Date.now()
    };
    for (const subscriber of statusSubscribers) {
      try {
        subscriber.postMessage({ type: "status", value: statusCache.value });
      } catch {
        statusSubscribers.delete(subscriber);
      }
    }
    scheduleTabRuleReconcile();
  }
}

function scheduleStatusReconnect() {
  if (statusReconnectTimer) {
    return;
  }

  statusReconnectTimer = setTimeout(() => {
    statusReconnectTimer = null;
    ensureStatusSocket();
  }, 1500);
}

function clearStatusReconnectTimer() {
  if (!statusReconnectTimer) {
    return;
  }

  clearTimeout(statusReconnectTimer);
  statusReconnectTimer = null;
}

function scheduleTabRuleReconcile(delayMilliseconds = 100) {
  if (tabRuleReconcileTimer) {
    clearTimeout(tabRuleReconcileTimer);
  }

  tabRuleReconcileTimer = setTimeout(() => {
    tabRuleReconcileTimer = null;
    reconcileSelectedTabRules(statusCache?.value).catch(() => {});
  }, delayMilliseconds);
}

async function reconcileSelectedTabRules(status) {
  if (!status) {
    return;
  }

  await replaceUrlContextRules(status.contexts || []);
  for (const entry of await selectedTabEntries()) {
    await replaceTabRules(entry.tab.id, entry.selection, status);
    await notifyRoutingNotice(entry.tab, entry.selection, status);
  }
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function selectContext(contextId, port) {
  if (!contextId || !Number.isInteger(port) || port <= 0) {
    throw new Error("Context and port are required.");
  }

  const tab = await getActiveTabEndpoint();
  if (!Number.isInteger(tab.id)) {
    throw new Error("No active browser tab is available.");
  }

  return applyContextToTab(tab, contextId, port);
}

async function openContext(contextId, port, scheme) {
  if (!contextId || !Number.isInteger(port) || port <= 0 || !["http", "https"].includes(scheme)) {
    throw new Error("Context, port, and HTTP or HTTPS scheme are required.");
  }

  const tab = await chrome.tabs.create({ url: "about:blank", active: true });
  if (!Number.isInteger(tab?.id)) {
    throw new Error("Could not open a new browser tab.");
  }

  try {
    await applyContextToTab(tab, contextId, port, null, false);
    await chrome.tabs.update(tab.id, {
      url: `${scheme}://localhost:${port}/`
    });
  } catch (error) {
    await chrome.tabs.remove(tab.id).catch(() => {});
    throw error;
  }
  return { contextId, port, scheme, tabId: tab.id };
}

async function applyContextToTab(tab, contextId, port, managedGroupId = null, reloadTab = true) {
  if (!Number.isInteger(tab?.id) || !contextId || !Number.isInteger(port) || port <= 0) {
    throw new Error("Tab, context, and port are required.");
  }

  const token = crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
  const status = await fetchStatus();
  const context = (status.contexts || []).find((item) => item.id === contextId);
  const label = contextLabel(context, contextId);
  const key = tabKey(tab.id);
  const previousSelection = normalizeTabSelection(
    (await chrome.storage.local.get(key))[key] || null,
    tab);
  const selection = createTabSelection(
    tab,
    contextId,
    label,
    token,
    previousSelection,
    port);
  await clearBrowserTarget();
  await replaceTabRules(tab.id, selection, status);
  await persistTabSelection(tab, selection, previousSelection, managedGroupId);
  if (reloadTab) {
    await chrome.tabs.reload(tab.id, { bypassCache: true });
  }
  return { contextId, port, tabId: tab.id };
}

async function setPortRedirect(port, contextId) {
  if (!contextId) {
    throw new Error("A redirect context is required.");
  }

  const { tab, selection, status } = await activeMissingPortPolicyContext(port);
  const contexts = status.contexts || [];
  const activeContext = contexts.find((context) => context.id === selection.contextId);
  const redirectContext = contexts.find((context) => context.id === contextId);
  if (!activeContext?.repositoryId
    || activeContext.repositoryId !== redirectContext?.repositoryId
    || activeContext.id === redirectContext.id) {
    throw new Error("A missing-port redirect must target another worktree in the same repository.");
  }
  if ((status.routes || []).some((route) =>
    route.contextId === activeContext.id && Number(route.port) === port)) {
    throw new Error(`The active worktree already listens on localhost:${port}.`);
  }
  if (!(status.routes || []).some((route) =>
    route.contextId === redirectContext.id && Number(route.port) === port)) {
    throw new Error(`The selected worktree is not listening on localhost:${port}.`);
  }

  await saveBrowserMissingPortPolicy({
    action: "set-browser-missing-port-policy",
    contextId: activeContext.id,
    port,
    browserMissingPortPolicyMode: "redirect",
    targetContextId: contextId
  });
  return {
    port,
    contextId,
    activeContextId: activeContext.id,
    tabId: tab.id,
    scope: "worktree"
  };
}

async function setPortFallback(port) {
  const { tab, selection } = await activeMissingPortPolicyContext(port);
  await saveBrowserMissingPortPolicy({
    action: "set-browser-missing-port-policy",
    contextId: selection.contextId,
    port,
    browserMissingPortPolicyMode: "automatic"
  });
  return {
    port,
    activeContextId: selection.contextId,
    tabId: tab.id,
    scope: "worktree"
  };
}

async function clearPortRedirect(port) {
  const { tab, selection } = await activeMissingPortPolicyContext(port);
  await saveBrowserMissingPortPolicy({
    action: "clear-browser-missing-port-policy",
    contextId: selection.contextId,
    port
  });
  return {
    port,
    activeContextId: selection.contextId,
    tabId: tab.id,
    scope: "worktree"
  };
}

async function disablePortRedirect(port) {
  const { tab, selection } = await activeMissingPortPolicyContext(port);
  await saveBrowserMissingPortPolicy({
    action: "set-browser-missing-port-policy",
    contextId: selection.contextId,
    port,
    browserMissingPortPolicyMode: "disabled"
  });
  return {
    port,
    activeContextId: selection.contextId,
    tabId: tab.id,
    scope: "worktree"
  };
}

async function activeMissingPortPolicyContext(port) {
  if (!Number.isInteger(port) || port <= 0 || port > 65535) {
    throw new Error("A port between 1 and 65535 is required.");
  }

  const tab = await getActiveTabEndpoint();
  if (!Number.isInteger(tab.id)) {
    throw new Error("No active browser tab is available.");
  }

  const key = tabKey(tab.id);
  const previousSelection = normalizeTabSelection(
    (await chrome.storage.local.get(key))[key] || null,
    tab);
  if (!previousSelection.contextId) {
    throw new Error("This tab has no active worktree.");
  }

  const status = await fetchStatus();
  if ((status.routes || []).some((route) =>
    route.contextId === previousSelection.contextId && Number(route.port) === port)) {
    throw new Error(`The active worktree already listens on localhost:${port}.`);
  }
  return { tab, selection: previousSelection, status };
}

async function saveBrowserMissingPortPolicy(payload) {
  await sendDevwtAction(payload);
  const status = await fetchFreshStatus();
  await reconcileSelectedTabRules(status);
}

async function persistTabSelection(tab, selection, previousSelection, managedGroupId = null) {
  const key = tabKey(tab.id);
  const settings = await getExtensionSettings();
  if (isGroupId(managedGroupId) && selection.contextId) {
    selection.autoGroupId = managedGroupId;
    await chrome.tabGroups.update(managedGroupId, { title: selection.label });
    await saveManagedGroup(managedGroupId, selection, tab.windowId);
    await chrome.storage.local.set({ [key]: selection });
  } else if (settings.groupTabsByContext && selection.contextId) {
    managedGroupMoves.add(tab.id);
    try {
      selection.autoGroupId = await groupTabForContext(tab, selection, previousSelection);
      await chrome.storage.local.set({ [key]: selection });
    } catch {
      // Routing and title selection remain valid if Chrome temporarily rejects a group move.
      await chrome.storage.local.set({ [key]: selection });
    } finally {
      managedGroupMoves.delete(tab.id);
    }
  } else {
    await ungroupManagedTab(tab, previousSelection).catch(() => {});
    await chrome.storage.local.set({ [key]: selection });
  }
  await chrome.action.setBadgeText({ tabId: tab.id, text: "DW" });
  await chrome.action.setBadgeBackgroundColor({ tabId: tab.id, color: "#2563eb" });
}

function createTabSelection(
  tab,
  contextId,
  label,
  token,
  previousSelection = null,
  preferredPort = null) {
  const endpoint = parseLocalhostEndpoint(tab?.url);
  const primaryPort = endpoint?.port || preferredPort;
  const selection = {
    contextId,
    label: label || contextId,
    token,
    port: primaryPort || null,
    url: tab?.url || previousSelection?.url || null,
    windowId: tab?.windowId ?? previousSelection?.windowId ?? null,
    index: tab?.index ?? previousSelection?.index ?? null,
    updatedAt: Date.now()
  };
  if (previousSelection?.autoGroupId !== undefined && contextId === previousSelection.contextId) {
    selection.autoGroupId = previousSelection.autoGroupId;
  }
  return selection;
}

function normalizeTabSelection(selection, tab = null) {
  const {
    portContexts: _discardedPortContexts,
    portRedirects: _storedPortRedirects,
    ...baseSelection
  } = selection || {};
  const contextId = selection?.contextId ? String(selection.contextId) : null;

  if (!contextId) {
    return {
      ...baseSelection,
      contextId: null,
      port: null,
      label: null
    };
  }

  const endpoint = parseLocalhostEndpoint(tab?.url);
  const storedPort = Number(selection?.port);
  return {
    ...baseSelection,
    contextId,
    label: selection?.label || contextId,
    port: endpoint?.port
      || (Number.isInteger(storedPort) && storedPort > 0 && storedPort <= 65535 ? storedPort : null)
  };
}

async function getExtensionSettings() {
  const values = await chrome.storage.local.get({
    [GROUP_TABS_SETTING_KEY]: false,
    [TAB_TITLE_SETTING_KEY]: false
  });
  return {
    groupTabsByContext: values[GROUP_TABS_SETTING_KEY] === true,
    showContextInTabTitle: values[TAB_TITLE_SETTING_KEY] === true
  };
}

async function setTabGroupingEnabled(enabled) {
  await chrome.storage.local.set({
    [GROUP_TABS_SETTING_KEY]: enabled
  });

  const result = enabled
    ? await groupAllSelectedTabs()
    : await ungroupAllManagedTabs();
  return {
    groupTabsByContext: enabled,
    ...result
  };
}

async function setTabTitleEnabled(enabled) {
  await chrome.storage.local.set({
    [TAB_TITLE_SETTING_KEY]: enabled
  });
  const result = await updateSelectedTabTitles(enabled);
  return {
    showContextInTabTitle: enabled,
    ...result
  };
}

async function updateSelectedTabTitles(enabled) {
  const entries = (await selectedTabEntries())
    .filter((entry) => parseLocalhostEndpoint(entry.tab.url));
  let updatedTabCount = 0;
  let failedTabCount = 0;
  for (const entry of entries) {
    try {
      const label = enabled ? await currentSelectionLabel(entry.selection) : null;
      await chrome.tabs.sendMessage(entry.tab.id, {
        type: "devwt-tab-label",
        label
      });
      updatedTabCount++;
    } catch {
      failedTabCount++;
    }
  }

  return { updatedTabCount, failedTabCount };
}

async function groupTabForContext(tab, selection, previousSelection) {
  let groupId = null;
  if (previousSelection?.contextId === selection.contextId
    && previousSelection?.autoGroupId === tab.groupId
    && isGroupId(tab.groupId)) {
    groupId = tab.groupId;
  }

  if (!isGroupId(groupId)) {
    groupId = await findContextGroup(tab.windowId, selection.contextId, tab.id);
  }

  groupId = isGroupId(groupId)
    ? await chrome.tabs.group({ groupId, tabIds: tab.id })
    : await chrome.tabs.group({
        tabIds: tab.id,
        createProperties: { windowId: tab.windowId }
      });
  await chrome.tabGroups.update(groupId, { title: selection.label });
  await saveManagedGroup(groupId, selection, tab.windowId);
  return groupId;
}

async function findContextGroup(windowId, contextId, excludedTabId) {
  const mappedGroupId = await findMappedContextGroup(windowId, contextId);
  if (isGroupId(mappedGroupId)) {
    return mappedGroupId;
  }

  const tabs = await chrome.tabs.query({ windowId });
  const candidateTabs = tabs.filter((tab) => Number.isInteger(tab.id) && tab.id !== excludedTabId);
  const keys = candidateTabs.map((tab) => tabKey(tab.id));
  const selections = keys.length ? await chrome.storage.local.get(keys) : {};
  for (const tab of candidateTabs) {
    const selection = normalizeTabSelection(selections[tabKey(tab.id)], tab);
    if (selection?.contextId === contextId
      && selection?.autoGroupId === tab.groupId
      && isGroupId(tab.groupId)) {
      return tab.groupId;
    }
  }

  return null;
}

async function findMappedContextGroup(windowId, contextId) {
  const values = await chrome.storage.local.get(null);
  for (const [key, value] of Object.entries(values)) {
    if (!key.startsWith(MANAGED_GROUP_KEY_PREFIX)
      || value?.windowId !== windowId
      || value?.contextId !== contextId) {
      continue;
    }

    const groupId = Number(key.slice(MANAGED_GROUP_KEY_PREFIX.length));
    if (!isGroupId(groupId)) {
      continue;
    }

    const group = await chrome.tabGroups.get(groupId).catch(() => null);
    if (group?.windowId === windowId) {
      return groupId;
    }
  }

  return null;
}

async function saveManagedGroup(groupId, selection, windowId) {
  await chrome.storage.local.set({
    [managedGroupKey(groupId)]: {
      contextId: selection.contextId,
      label: selection.label || selection.contextId,
      windowId
    }
  });
}

async function getManagedGroup(groupId) {
  const key = managedGroupKey(groupId);
  const stored = (await chrome.storage.local.get(key))[key];
  if (stored?.contextId) {
    return stored;
  }

  const tabs = await chrome.tabs.query({ groupId });
  const keys = tabs
    .filter((tab) => Number.isInteger(tab.id))
    .map((tab) => tabKey(tab.id));
  const selections = keys.length ? await chrome.storage.local.get(keys) : {};
  for (const tab of tabs) {
    const selection = normalizeTabSelection(selections[tabKey(tab.id)], tab);
    if (selection?.contextId
      && selection?.autoGroupId === groupId) {
      const group = {
        contextId: selection.contextId,
        label: selection.label || selection.contextId,
        windowId: tab.windowId
      };
      await chrome.storage.local.set({ [key]: group });
      return group;
    }
  }

  return null;
}

function managedGroupKey(groupId) {
  return `${MANAGED_GROUP_KEY_PREFIX}${groupId}`;
}

async function groupAllSelectedTabs() {
  const entries = await selectedTabEntries();
  const contexts = new Map();
  for (const entry of entries) {
    const key = `${entry.tab.windowId}:${entry.selection.contextId}`;
    const existing = contexts.get(key);
    if (existing) {
      existing.push(entry);
    } else {
      contexts.set(key, [entry]);
    }
  }

  let updatedTabCount = 0;
  let failedTabCount = 0;
  for (const contextEntries of contexts.values()) {
    const tabIds = contextEntries.map((entry) => entry.tab.id);
    const existingGroup = contextEntries.find((entry) =>
      entry.selection.autoGroupId === entry.tab.groupId && isGroupId(entry.tab.groupId));
    for (const tabId of tabIds) {
      managedGroupMoves.add(tabId);
    }
    try {
      const groupId = existingGroup
        ? await chrome.tabs.group({ groupId: existingGroup.tab.groupId, tabIds })
        : await chrome.tabs.group({
            tabIds,
            createProperties: { windowId: contextEntries[0].tab.windowId }
          });
      const label = await currentSelectionLabel(contextEntries[0].selection);
      await chrome.tabGroups.update(groupId, { title: label });
      await saveManagedGroup(groupId, {
        ...contextEntries[0].selection,
        label
      }, contextEntries[0].tab.windowId);
      const updates = {};
      for (const entry of contextEntries) {
        updates[tabKey(entry.tab.id)] = {
          ...entry.selection,
          label,
          autoGroupId: groupId
        };
      }
      await chrome.storage.local.set(updates);
      updatedTabCount += contextEntries.length;
    } catch {
      failedTabCount += contextEntries.length;
    } finally {
      for (const tabId of tabIds) {
        managedGroupMoves.delete(tabId);
      }
    }
  }

  return { updatedTabCount, failedTabCount };
}

async function ungroupAllManagedTabs() {
  const entries = await selectedTabEntries();
  const managedGroupKeys = Object.keys(await chrome.storage.local.get(null))
    .filter((key) => key.startsWith(MANAGED_GROUP_KEY_PREFIX));
  let updatedTabCount = 0;
  let failedTabCount = 0;
  for (const entry of entries) {
    try {
      if (entry.selection.autoGroupId === entry.tab.groupId && isGroupId(entry.tab.groupId)) {
        await chrome.tabs.ungroup(entry.tab.id);
        updatedTabCount++;
      }
      await chrome.storage.local.set({
        [tabKey(entry.tab.id)]: withoutAutoGroup(entry.selection)
      });
    } catch {
      failedTabCount++;
    }
  }

  if (managedGroupKeys.length) {
    await chrome.storage.local.remove(managedGroupKeys);
  }

  return { updatedTabCount, failedTabCount };
}

async function selectedTabEntries() {
  const tabs = (await chrome.tabs.query({}))
    .filter((tab) => Number.isInteger(tab.id));
  const keys = tabs.map((tab) => tabKey(tab.id));
  const selections = keys.length ? await chrome.storage.local.get(keys) : {};
  return tabs
    .map((tab) => ({
      tab,
      selection: normalizeTabSelection(selections[tabKey(tab.id)], tab)
    }))
    .filter((entry) => entry.selection?.contextId);
}

async function currentSelectionLabel(selection) {
  let label = selection.label || selection.contextId;
  try {
    const status = await fetchStatus();
    const context = (status.contexts || []).find((item) => item.id === selection.contextId);
    label = contextLabel(context, selection.contextId);
  } catch {
    // Keep the stored label when the local DevWT service is temporarily unavailable.
  }
  return label;
}

async function ungroupManagedTab(tab, selection) {
  if (selection?.autoGroupId === tab?.groupId && isGroupId(tab.groupId)) {
    await chrome.tabs.ungroup(tab.id);
    return true;
  }

  return false;
}

function withoutAutoGroup(selection) {
  const { autoGroupId: _, ...rest } = selection;
  return rest;
}

function isGroupId(value) {
  return Number.isInteger(value) && value >= 0;
}

function scheduleGroupedTabContextUpdate(tabId) {
  const previous = groupedTabContextUpdates.get(tabId) || Promise.resolve();
  const next = previous
    .catch(() => {})
    .then(() => syncTabContextFromGroup(tabId));
  groupedTabContextUpdates.set(tabId, next);
  next.finally(() => {
    if (groupedTabContextUpdates.get(tabId) === next) {
      groupedTabContextUpdates.delete(tabId);
    }
  }).catch(() => {});
}

async function syncTabContextFromGroup(tabId) {
  if (managedGroupMoves.has(tabId)) {
    return false;
  }

  const settings = await getExtensionSettings();
  if (!settings.groupTabsByContext) {
    return false;
  }

  const tab = await chrome.tabs.get(tabId).catch(() => null);
  if (!tab) {
    return false;
  }

  const key = tabKey(tabId);
  const selection = normalizeTabSelection(
    (await chrome.storage.local.get(key))[key] || null,
    tab);
  if (!isGroupId(tab.groupId)) {
    if (selection?.autoGroupId !== undefined) {
      await chrome.storage.local.set({
        [key]: withoutAutoGroup(selection)
      });
    }
    return false;
  }

  const group = await getManagedGroup(tab.groupId);
  if (!group?.contextId) {
    if (selection?.autoGroupId !== undefined
      && selection.autoGroupId !== tab.groupId) {
      await chrome.storage.local.set({
        [key]: withoutAutoGroup(selection)
      });
    }
    return false;
  }

  const endpoint = parseLocalhostEndpoint(tab.url);
  if (!endpoint) {
    return false;
  }

  if (selection?.contextId === group.contextId
    && Number(selection.port) === endpoint.port
    && selection.token) {
    const label = group.label || selection.label || group.contextId;
    await chrome.storage.local.set({
      [key]: {
        ...selection,
        label,
        autoGroupId: tab.groupId
      }
    });
    if (settings.showContextInTabTitle && label !== selection.label) {
      await chrome.tabs.sendMessage(tabId, {
        type: "devwt-tab-label",
        label
      }).catch(() => {});
    }
    return false;
  }

  await applyContextToTab(tab, group.contextId, endpoint.port, tab.groupId);
  return true;
}

function contextLabel(context, fallback) {
  const description = typeof context?.description === "string" ? context.description.trim() : "";
  if (description) {
    return description;
  }

  const branch = typeof context?.gitRef === "string" ? context.gitRef.trim() : "";
  return branch || fallback;
}

async function getTabContextLabel(tabId) {
  if (!Number.isInteger(tabId)) {
    return null;
  }

  const key = tabKey(tabId);
  const tab = await chrome.tabs.get(tabId).catch(() => null);
  const selection = normalizeTabSelection(
    (await chrome.storage.local.get(key))[key],
    tab);
  if (!selection?.contextId) {
    return null;
  }

  const settings = await getExtensionSettings();
  if (!settings.showContextInTabTitle) {
    return null;
  }

  let label = selection.label || selection.contextId;
  try {
    const status = await fetchStatus();
    const context = (status.contexts || []).find((item) => item.id === selection.contextId);
    label = contextLabel(context, selection.contextId);
  } catch {
    // Keep the stored label when the local DevWT service is temporarily unavailable.
  }

  if (label !== selection.label) {
    await chrome.storage.local.set({
      [key]: { ...selection, label }
    });
  }

  return label;
}

async function clearContext() {
  const tab = await getActiveTabEndpoint();
  if (!Number.isInteger(tab.id)) {
    return {};
  }

  await clearBrowserTarget();
  await clearTab(tab.id);
  await chrome.tabs.reload(tab.id, { bypassCache: true });
  return { tabId: tab.id };
}

async function clearBrowserTarget() {
  await sendDevwtAction({
    action: "clear-active-target",
    browserScoped: true
  });
}

async function sendDevwtAction(payload) {
  const response = await fetch(DEVWT_ACTION_URL, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload)
  });
  const result = await response.json();
  if (!response.ok || Number(result?.exitCode || 0) !== 0) {
    throw new Error(result?.output || `DevWT action failed: HTTP ${response.status}`);
  }
  return result;
}

async function getActiveTabEndpoint() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const endpoint = parseLocalhostEndpoint(tab?.url);
  return {
    id: tab?.id || null,
    windowId: tab?.windowId ?? null,
    groupId: tab?.groupId ?? null,
    title: tab?.title || "",
    url: tab?.url || "",
    endpoint
  };
}

function parseLocalhostEndpoint(value) {
  if (!value) {
    return null;
  }

  let url;
  try {
    url = new URL(value);
  } catch {
    return null;
  }

  if (!["http:", "https:"].includes(url.protocol)) {
    return null;
  }

  if (!["localhost", "127.0.0.1", "[::1]"].includes(url.hostname)) {
    return null;
  }

  const port = url.port ? Number(url.port) : (url.protocol === "https:" ? 443 : 80);
  if (!Number.isInteger(port) || port <= 0) {
    return null;
  }

  return {
    scheme: url.protocol.slice(0, -1),
    hostname: url.hostname,
    port
  };
}

function buildTabHeaderRule(
  ruleId,
  priority,
  tabId,
  regexFilter,
  contextId,
  token,
  extensionManaged = false) {
  return {
    id: ruleId,
    priority,
    action: {
      type: "modifyHeaders",
      requestHeaders: [
        { header: "X-DevWT-Context", operation: "set", value: contextId },
        { header: "X-DevWT-Tab", operation: "set", value: String(tabId) },
        { header: "X-DevWT-Token", operation: "set", value: token },
        ...(extensionManaged
          ? [{ header: ALLOW_FALLBACK_HEADER, operation: "set", value: "1" }]
          : [])
      ]
    },
    condition: {
      regexFilter,
      tabIds: [tabId],
      resourceTypes: RESOURCE_TYPES
    }
  };
}

function buildTabContextRules(tabId, selection, status) {
  if (!selection?.contextId) {
    return [];
  }

  return [
    buildTabHeaderRule(
      portRuleIdForTab(tabId, 0),
      1,
      tabId,
      LOCALHOST_REGEX,
      selection.contextId,
      selection.token,
      true)
  ];
}

function effectiveMissingPortAction(selection, status, port) {
  if (!selection?.contextId || !Number.isInteger(port) || port <= 0 || port > 65535) {
    return null;
  }

  const contexts = status?.contexts || [];
  const routes = status?.routes || [];
  const contextsById = new Map(contexts.map((context) => [context.id, context]));
  const activeContext = contextsById.get(selection.contextId);
  if (!activeContext?.repositoryId || routes.some((route) =>
    route.contextId === activeContext.id && Number(route.port) === port)) {
    return null;
  }

  const policy = (status?.runtimeSettings?.browserMissingPortPolicies || [])
    .find((candidate) =>
      candidate?.contextId === activeContext.id
      && Number(candidate?.port) === port);
  const mode = String(policy?.mode || "").toLowerCase();
  if (mode === "disabled") {
    return null;
  }

  const contextId = mode === "redirect" ? policy?.targetContextId : null;
  const providerContext = contextsById.get(contextId);
  if (contextId
    && providerContext?.repositoryId === activeContext.repositoryId
    && providerContext.id !== activeContext.id) {
    return {
      kind: "redirect",
      port,
      contextId,
      source: "worktree",
      available: routes.some((route) =>
        route.contextId === contextId && Number(route.port) === port),
      activeContext,
      providerContext
    };
  }

  if (mode !== "automatic"
    && status?.runtimeSettings?.browserFallbackOnMissingPort !== true
    || !routes.some((route) => Number(route.port) === port)) {
    return null;
  }

  return {
    kind: "fallback",
    port,
    source: mode === "automatic" ? "worktree" : "global",
    available: true,
    activeContext
  };
}

function buildRoutingNotice(tab, selection, status) {
  const endpoint = parseLocalhostEndpoint(tab?.url);
  if (!endpoint) {
    return null;
  }

  const action = effectiveMissingPortAction(selection, status, Number(endpoint.port));
  if (!action) {
    return null;
  }

  if (action.kind === "fallback") {
    return {
      kind: "fallback",
      port: action.port,
      source: action.source,
      available: true,
      activeContextId: action.activeContext.id,
      activeLabel: contextLabel(action.activeContext, action.activeContext.id)
    };
  }

  return {
    kind: "redirect",
    port: action.port,
    source: action.source,
    available: action.available,
    activeContextId: action.activeContext.id,
    activeLabel: contextLabel(action.activeContext, action.activeContext.id),
    providerContextId: action.providerContext.id,
    providerLabel: contextLabel(action.providerContext, action.providerContext.id)
  };
}

async function getRoutingNotice(tab) {
  if (!Number.isInteger(tab?.id)) {
    return null;
  }

  const key = tabKey(tab.id);
  const selection = normalizeTabSelection(
    (await chrome.storage.local.get(key))[key] || null,
    tab);
  return buildRoutingNotice(tab, selection, await fetchStatus());
}

async function notifyRoutingNotice(tab, selection, status) {
  if (!Number.isInteger(tab?.id)) {
    return;
  }

  await chrome.tabs.sendMessage(tab.id, {
    type: "devwt-routing-notice",
    value: buildRoutingNotice(tab, selection, status)
  }).catch(() => {});
}

async function replaceTabRules(tabId, selection, status) {
  const removeRuleIds = await managedRuleIdsForTab(tabId);
  const addRules = buildTabContextRules(tabId, selection, status);
  await chrome.declarativeNetRequest.updateSessionRules({
    removeRuleIds,
    addRules
  });
}

async function replaceUrlContextRules(contexts) {
  const existingRules = await chrome.declarativeNetRequest.getSessionRules();
  const removeRuleIds = existingRules
    .filter((rule) => rule.id >= URL_RULE_ID_BASE && rule.id < PORT_RULE_ID_BASE)
    .map((rule) => rule.id);
  const addRules = buildUrlContextRules(contexts);
  await chrome.declarativeNetRequest.updateSessionRules({
    removeRuleIds,
    addRules
  });
}

function buildUrlContextRules(contexts) {
  const rules = [];
  for (const [index, context] of contexts.entries()) {
    if (!context?.id) {
      continue;
    }

    const contextId = String(context.id);
    const match = `^https?://(localhost|127\\.0\\.0\\.1|\\[::1\\])(:[0-9]+)?/.*[?&]devwt-context=${escapeRegex(contextId)}(&|#|$)`;
    const base = URL_RULE_ID_BASE + (index * 2);
    rules.push({
      id: base,
      priority: 10,
      action: {
        type: "modifyHeaders",
        requestHeaders: [
          { header: "X-DevWT-Context", operation: "set", value: contextId },
          { header: ALLOW_FALLBACK_HEADER, operation: "remove" }
        ]
      },
      condition: {
        regexFilter: match,
        resourceTypes: ["main_frame"]
      }
    });
    rules.push({
      id: base + 1,
      priority: 11,
      action: {
        type: "redirect",
        redirect: {
          transform: { queryTransform: { removeParams: ["devwt-context"] } }
        }
      },
      condition: {
        regexFilter: match,
        resourceTypes: ["main_frame"]
      }
    });
  }
  return rules;
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\\]\\]/g, "\\$&");
}

async function clearTab(tabId) {
  if (!Number.isInteger(tabId)) {
    return false;
  }

  const key = tabKey(tabId);
  const tab = await chrome.tabs.get(tabId).catch(() => null);
  const selection = normalizeTabSelection(
    (await chrome.storage.local.get(key))[key] || null,
    tab);
  await chrome.declarativeNetRequest.updateSessionRules({
    removeRuleIds: await managedRuleIdsForTab(tabId)
  });
  await ungroupManagedTab(tab, selection).catch(() => {});
  await chrome.storage.local.remove(key);
  await chrome.action.setBadgeText({ tabId, text: "" });
  await chrome.tabs.sendMessage(tabId, { type: "devwt-tab-label", label: null }).catch(() => {});
}

function portRuleIdForTab(tabId, portIndex) {
  return PORT_RULE_ID_BASE
    + ((tabId % RULE_ID_SLOT_COUNT) * PORT_RULE_IDS_PER_TAB)
    + portIndex;
}

async function managedRuleIdsForTab(tabId) {
  if (!Number.isInteger(tabId)) {
    return [];
  }

  const rules = await chrome.declarativeNetRequest.getSessionRules();
  return rules
    .filter((rule) => Array.isArray(rule.condition?.tabIds)
      && rule.condition.tabIds.includes(tabId))
    .map((rule) => rule.id);
}

function tabKey(tabId) {
  return `${TAB_KEY_PREFIX}${tabId}`;
}

async function restorePersistedState() {
  const status = await fetchStatus().catch(() => ({ contexts: [] }));
  const contexts = status.contexts || [];
  const tabs = (await chrome.tabs.query({}))
    .filter((tab) => Number.isInteger(tab.id));
  const values = await chrome.storage.local.get(null);
  const groups = await chrome.tabGroups.query({}).catch(() => []);
  const groupTitles = new Map(groups.map((group) => [group.id, group.title || ""]));
  const persistedEntries = Object.entries(values)
    .filter(([key, selection]) =>
      key.startsWith(TAB_KEY_PREFIX)
      && normalizeTabSelection(selection).contextId)
    .map(([key, selection]) => ({
      key,
      oldTabId: Number(key.slice(TAB_KEY_PREFIX.length)),
      selection
    }));
  const candidates = [];
  for (const persisted of persistedEntries) {
    for (const tab of tabs) {
      if (!parseLocalhostEndpoint(tab.url) || persisted.selection.url !== tab.url) {
        continue;
      }

      let score = 0;
      if (persisted.oldTabId === tab.id) {
        score += 10000;
      }
      if (persisted.selection.windowId === tab.windowId) {
        score += 1000;
      }
      if (persisted.selection.autoGroupId !== undefined
        && groupTitles.get(tab.groupId) === persisted.selection.label) {
        score += 5000;
      }
      score -= Math.abs(Number(persisted.selection.index || 0) - tab.index);
      candidates.push({ persisted, tab, score });
    }
  }
  candidates.sort((left, right) => right.score - left.score);
  const matchedKeys = new Set();
  const matchedTabIds = new Set();
  const entries = [];
  for (const candidate of candidates) {
    if (matchedKeys.has(candidate.persisted.key) || matchedTabIds.has(candidate.tab.id)) {
      continue;
    }

    matchedKeys.add(candidate.persisted.key);
    matchedTabIds.add(candidate.tab.id);
    entries.push({
      tab: candidate.tab,
      sourceKey: candidate.persisted.key,
      selection: candidate.persisted.selection
    });
  }
  const existingRules = await chrome.declarativeNetRequest.getSessionRules();
  const restoredTabIds = new Set(entries.map((entry) => entry.tab.id));
  const removeRuleIds = existingRules
    .filter((rule) => Array.isArray(rule.condition?.tabIds)
      && rule.condition.tabIds.some((tabId) => restoredTabIds.has(tabId)))
    .map((rule) => rule.id);
  const addRules = [];
  const selectionUpdates = {};

  for (const entry of entries) {
    const token = crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
    const normalized = normalizeTabSelection(entry.selection, entry.tab);
    const selection = createTabSelection(
      entry.tab,
      normalized.contextId,
      normalized.label,
      token,
      normalized);
    if (selection.autoGroupId !== undefined
      && groupTitles.get(entry.tab.groupId) === selection.label) {
      selection.autoGroupId = entry.tab.groupId;
    }
    entry.selection = selection;
    selectionUpdates[tabKey(entry.tab.id)] = selection;
    addRules.push(...buildTabContextRules(entry.tab.id, selection, status));
  }

  await replaceUrlContextRules(contexts);
  if (entries.length) {
    await clearBrowserTarget().catch(() => {});
  }
  if (removeRuleIds.length || addRules.length) {
    await chrome.declarativeNetRequest.updateSessionRules({
      removeRuleIds,
      addRules
    });
  }
  const replacedTabKeys = entries
    .filter((entry) => entry.sourceKey !== tabKey(entry.tab.id))
    .map((entry) => entry.sourceKey);
  if (replacedTabKeys.length) {
    await chrome.storage.local.remove(replacedTabKeys);
  }
  if (Object.keys(selectionUpdates).length) {
    await chrome.storage.local.set(selectionUpdates);
  }

  for (const entry of entries) {
    await chrome.action.setBadgeText({ tabId: entry.tab.id, text: "DW" }).catch(() => {});
    await chrome.action.setBadgeBackgroundColor({ tabId: entry.tab.id, color: "#2563eb" }).catch(() => {});
  }

  const managedGroupKeys = Object.keys(values)
    .filter((key) => key.startsWith(MANAGED_GROUP_KEY_PREFIX));
  if (entries.length && managedGroupKeys.length) {
    await chrome.storage.local.remove(managedGroupKeys);
  }

  const settings = await getExtensionSettings();
  if (entries.length && settings.groupTabsByContext) {
    await groupAllSelectedTabs();
  }
  if (entries.length && settings.showContextInTabTitle) {
    await updateSelectedTabTitles(true);
  }
}

function schedulePersistedStateRestore(delayMilliseconds = 100) {
  if (persistedStateRestoreTimer) {
    clearTimeout(persistedStateRestoreTimer);
  }

  persistedStateRestoreTimer = setTimeout(() => {
    persistedStateRestoreTimer = null;
    restorePersistedState().catch(() => {});
  }, delayMilliseconds);
}

chrome.runtime.onStartup.addListener(() => {
  schedulePersistedStateRestore(0);
});

chrome.runtime.onInstalled.addListener(() => {
  schedulePersistedStateRestore(0);
});

chrome.webNavigation.onBeforeNavigate.addListener((details) => {
  captureUrlContextSelection(details).catch(() => {});
});

async function captureUrlContextSelection(details) {
  if (!Number.isInteger(details?.tabId) || details.tabId < 0 || details.frameId !== 0) {
    return false;
  }

  const endpoint = parseLocalhostEndpoint(details.url);
  if (!endpoint) {
    return false;
  }

  const url = new URL(details.url);
  const contextId = url.searchParams.get("devwt-context");
  if (!contextId) {
    return false;
  }

  const status = await fetchStatus();
  const context = (status.contexts || []).find((item) => item.id === contextId);
  if (!context) {
    return false;
  }

  const tab = await chrome.tabs.get(details.tabId).catch(() => null);
  if (!tab) {
    return false;
  }

  await applyContextToTab(
    { ...tab, url: details.url },
    contextId,
    endpoint.port,
    null,
    false);
  return true;
}

chrome.tabs.onRemoved.addListener((tabId, removeInfo) => {
  handleTabRemoved(tabId, removeInfo).catch(() => {});
});

async function handleTabRemoved(tabId, removeInfo) {
  if (removeInfo?.isWindowClosing) {
    return false;
  }

  await clearTab(tabId);
  return true;
}

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.groupId === undefined
    && changeInfo.url === undefined
    && changeInfo.status !== "complete") {
    return;
  }

  if (changeInfo.url !== undefined) {
    updatePersistedTabIdentity(tabId).catch(() => {});
  }
  if (changeInfo.url !== undefined || changeInfo.status === "complete") {
    schedulePersistedStateRestore();
  }
  scheduleGroupedTabContextUpdate(tabId);
});

chrome.tabs.onCreated.addListener(() => {
  schedulePersistedStateRestore();
});

chrome.tabs.onMoved.addListener((tabId) => {
  updatePersistedTabIdentity(tabId).catch(() => {});
});

chrome.tabs.onAttached.addListener((tabId) => {
  updatePersistedTabIdentity(tabId).catch(() => {});
});

chrome.tabGroups.onRemoved.addListener((group) => {
  chrome.storage.local.remove(managedGroupKey(group.id)).catch(() => {});
});

async function updatePersistedTabIdentity(tabId) {
  const key = tabKey(tabId);
  const tab = await chrome.tabs.get(tabId).catch(() => null);
  if (!tab) {
    return;
  }
  const storedSelection = (await chrome.storage.local.get(key))[key];
  const selection = normalizeTabSelection(storedSelection, tab);
  if (!selection.contextId) {
    return;
  }

  await chrome.storage.local.set({
    [key]: {
      ...selection,
      url: tab.url || selection.url || null,
      windowId: tab.windowId,
      index: tab.index,
      updatedAt: Date.now()
    }
  });
}
