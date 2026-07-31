import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const event = { addListener() {} };
const sandbox = {
  URL,
  clearTimeout,
  console,
  crypto: globalThis.crypto,
  setTimeout,
  chrome: {
    runtime: {
      onConnect: event,
      onInstalled: event,
      onMessage: event,
      onStartup: event
    },
    webNavigation: {
      onBeforeNavigate: event
    },
    tabGroups: {
      onRemoved: event
    },
    tabs: {
      onAttached: event,
      onCreated: event,
      onMoved: event,
      onRemoved: event,
      onUpdated: event
    }
  }
};
vm.createContext(sandbox);
vm.runInContext(
  readFileSync(new URL("../extension/devwt-browser/background.js", import.meta.url), "utf8"),
  sandbox);

const status = {
  contexts: [
    { id: "ctx-active", repositoryId: "repo-1" },
    { id: "ctx-provider", repositoryId: "repo-1" },
    { id: "ctx-provider-alt", repositoryId: "repo-1" },
    { id: "ctx-other-repo", repositoryId: "repo-2" }
  ],
  routes: [
    { contextId: "ctx-active", port: 44334 },
    { contextId: "ctx-provider", port: 44364 },
    { contextId: "ctx-provider-alt", port: 44364 },
    { contextId: "ctx-other-repo", port: 44373 }
  ],
  runtimeSettings: {
    browserFallbackOnMissingPort: false,
    browserMissingPortPolicies: []
  }
};

const selection = {
  contextId: "ctx-active",
  label: "Active worktree",
  port: 44334,
  token: "test-token",
  portRedirects: {}
};

function withPolicy(mode, port = 44364, targetContextId = null, globalDefault = false) {
  return {
    ...status,
    runtimeSettings: {
      browserFallbackOnMissingPort: globalDefault,
      browserMissingPortPolicies: [{
        contextId: "ctx-active",
        port,
        mode,
        targetContextId
      }]
    }
  };
}

test("active worktree rule marks every localhost request as extension-managed", () => {
  const rules = sandbox.buildTabContextRules(42, selection, status);

  assert.equal(rules.length, 1);
  assert.equal(rules[0].priority, 1);
  assert.equal(rules[0].action.requestHeaders[0].value, "ctx-active");
  assert.match(rules[0].condition.regexFilter, /localhost/);
  assert.equal(rules[0].action.requestHeaders.some((header) =>
    header.header === "X-DevWT-Allow-Fallback"
      && header.value === "1"), true);
});

test("same-repository redirect is resolved from the worktree-port policy", () => {
  const policyStatus = withPolicy("Redirect", 44364, "ctx-provider");
  const action = sandbox.effectiveMissingPortAction(selection, policyStatus, 44364);
  const notice = sandbox.buildRoutingNotice(
    { url: "https://localhost:44364/" },
    selection,
    policyStatus);

  assert.equal(action.kind, "redirect");
  assert.equal(action.contextId, "ctx-provider");
  assert.equal(action.source, "worktree");
  assert.equal(notice.providerContextId, "ctx-provider");
  assert.equal(notice.source, "worktree");
});

test("a listener in the active worktree disables a saved worktree policy", () => {
  const policyStatus = {
    ...withPolicy("Redirect", 44364, "ctx-provider"),
    routes: [
      ...status.routes,
      { contextId: "ctx-active", port: 44364 }
    ]
  };

  assert.equal(sandbox.effectiveMissingPortAction(selection, policyStatus, 44364), null);
  assert.equal(sandbox.buildRoutingNotice(
    { url: "https://localhost:44364/" },
    selection,
    policyStatus), null);
});

test("a redirect to another repository is never effective", () => {
  const policyStatus = withPolicy("Redirect", 44373, "ctx-other-repo");
  assert.equal(sandbox.effectiveMissingPortAction(selection, policyStatus, 44373), null);
});

test("normalization discards legacy tab-scoped port choices", () => {
  const normalized = sandbox.normalizeTabSelection({
    contextId: "ctx-active",
    label: "Active worktree",
    port: 44334,
    portContexts: {
      "44334": { contextId: "ctx-active" },
      "44364": { contextId: "ctx-provider" }
    },
    portRedirects: {
      "44364": { contextId: "ctx-provider" }
    }
  });

  assert.equal(normalized.contextId, "ctx-active");
  assert.equal(Object.hasOwn(normalized, "portRedirects"), false);
  assert.equal(Object.hasOwn(normalized, "portContexts"), false);
});

test("global Console fallback is the default when no worktree policy exists", () => {
  const fallbackStatus = {
    ...status,
    runtimeSettings: {
      browserFallbackOnMissingPort: true,
      browserMissingPortPolicies: []
    }
  };
  const action = sandbox.effectiveMissingPortAction(selection, fallbackStatus, 44364);
  const notice = sandbox.buildRoutingNotice(
    { url: "https://localhost:44364/account" },
    selection,
    fallbackStatus);

  assert.equal(action.kind, "fallback");
  assert.equal(action.source, "global");
  assert.equal(notice.kind, "fallback");
  assert.equal(notice.source, "global");
});

test("Automatic worktree policy overrides a disabled Console default", () => {
  const policyStatus = withPolicy("Automatic");
  const action = sandbox.effectiveMissingPortAction(selection, policyStatus, 44364);
  const notice = sandbox.buildRoutingNotice(
    { url: "https://localhost:44364/" },
    selection,
    policyStatus);

  assert.equal(action.kind, "fallback");
  assert.equal(action.source, "worktree");
  assert.equal(notice.source, "worktree");
});

test("Disabled worktree policy overrides an enabled Console default", () => {
  const policyStatus = withPolicy("Disabled", 44364, null, true);

  assert.equal(sandbox.effectiveMissingPortAction(selection, policyStatus, 44364), null);
  assert.equal(sandbox.buildRoutingNotice(
    { url: "https://localhost:44364/" },
    selection,
    policyStatus), null);
});

test("an explicit URL selector removes the extension-managed fallback opt-in", () => {
  const rules = sandbox.buildUrlContextRules([
    { id: "ctx-provider" }
  ]);
  const headerRule = rules.find((rule) => rule.priority === 10);

  assert.equal(Object.hasOwn(headerRule.condition, "tabIds"), false);
  assert.equal(headerRule.action.requestHeaders.some((header) =>
    header.header === "X-DevWT-Allow-Fallback"
      && header.operation === "remove"), true);
});

test("URL selector rules cover every reported context before a tab is selected", () => {
  const contexts = Array.from({ length: 51 }, (_, index) => ({
    id: `ctx-${index}`
  }));
  const rules = sandbox.buildUrlContextRules(contexts);

  assert.equal(rules.length, 102);
  assert.equal(rules.every((rule) => !Object.hasOwn(rule.condition, "tabIds")), true);
  assert.match(rules.at(-1).condition.regexFilter, /ctx-50/);
});

test("worktree policy choices update without reloading the active tab", () => {
  assert.doesNotMatch(sandbox.setPortRedirect.toString(), /tabs\.reload/);
  assert.doesNotMatch(sandbox.setPortFallback.toString(), /tabs\.reload/);
  assert.doesNotMatch(sandbox.clearPortRedirect.toString(), /tabs\.reload/);
  assert.doesNotMatch(sandbox.disablePortRedirect.toString(), /tabs\.reload/);
  assert.match(sandbox.setPortFallback.toString(), /set-browser-missing-port-policy/);
  assert.match(sandbox.clearPortRedirect.toString(), /clear-browser-missing-port-policy/);
});
