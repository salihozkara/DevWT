import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

function fakeNode() {
  return {
    checked: false,
    className: "",
    disabled: false,
    hidden: false,
    innerHTML: "",
    textContent: "",
    value: "",
    addEventListener() {},
    focus() {},
    querySelectorAll() {
      return [];
    }
  };
}

const nodes = new Map();
const document = {
  activeElement: null,
  addEventListener() {},
  getElementById(id) {
    if (!nodes.has(id)) {
      nodes.set(id, fakeNode());
    }
    return nodes.get(id);
  }
};
const event = { addListener() {} };
const sandbox = {
  HTMLInputElement: class {},
  HTMLTextAreaElement: class {},
  clearTimeout,
  console,
  document,
  setTimeout,
  window: { close() {} },
  chrome: {
    runtime: {
      connect() {
        return { onMessage: event };
      },
      async sendMessage(message) {
        if (message?.type === "extension-settings") {
          return {
            ok: true,
            value: {
              groupTabsByContext: false,
              showContextInTabTitle: false
            }
          };
        }
        return {
          ok: true,
          value: {
            status: { contexts: [], routes: [] },
            tab: { endpoint: null, selection: null }
          }
        };
      }
    }
  }
};
vm.createContext(sandbox);
vm.runInContext(
  readFileSync(new URL("../extension/devwt-browser/popup.js", import.meta.url), "utf8"),
  sandbox);

const status = {
  contexts: [
    {
      id: "ctx-active",
      repositoryId: "repo-1",
      name: "Active worktree",
      description: "Assigned context",
      gitRef: "feature/active",
      worktreeRootPath: "D:\\worktrees\\active"
    },
    {
      id: "ctx-provider",
      repositoryId: "repo-1",
      name: "Provider worktree",
      description: "Shared AuthServer",
      gitRef: "feature/provider",
      worktreeRootPath: "D:\\worktrees\\provider"
    }
  ],
  routes: [
    {
      contextId: "ctx-active",
      repositoryId: "repo-1",
      port: 44334,
      protocol: "Tcp",
      listenIp: "127.0.0.1",
      targetPort: 50100,
      listenerProcessId: 10,
      processName: "App.Host.exe",
      worktreeRootPath: "D:\\worktrees\\active"
    },
    {
      contextId: "ctx-provider",
      repositoryId: "repo-1",
      port: 44373,
      protocol: "Tcp",
      listenIp: "127.0.0.1",
      targetPort: 50200,
      listenerProcessId: 20,
      processName: "App.AuthServer.exe",
      worktreeRootPath: "D:\\worktrees\\provider"
    }
  ],
  runtimeSettings: {
    browserFallbackOnMissingPort: false,
    browserMissingPortPolicies: []
  }
};
const selection = {
  contextId: "ctx-active",
  label: "Assigned context",
  portRedirects: {}
};

test("assigned context remains visible when it has no listener on the tab port", () => {
  const listeningRoutes = sandbox.uniqueRoutes(status.routes)
    .filter((route) => Number(route.port) === 44373);
  const assigned = sandbox.buildAssignedMissingRoute(
    status,
    selection,
    { hostname: "localhost", port: 44373 },
    listeningRoutes);
  const cards = sandbox.sortRoutes([assigned, ...listeningRoutes], selection);
  const html = sandbox.renderRoute(
    status,
    assigned,
    selection,
    { scheme: "https", hostname: "localhost", port: 44373 });

  assert.equal(assigned.missingListener, true);
  assert.equal(cards[0].contextId, "ctx-active");
  assert.match(html, /This worktree is assigned to the tab, but this port is closed/);
  assert.match(html, /Automatic fallback · always/);
  assert.match(html, /Console default · currently off/);
  assert.match(html, /Applies to every extension tab that uses this active worktree/);
  assert.match(html, /Shared AuthServer · ctx-provider/);
  assert.match(html, /<details class="other-ports"[^>]* open>/);
});

test("Automatic, Console default, and No redirect remain distinct worktree policies", () => {
  const defaultPolicy = sandbox.missingPortRedirectOptions(
    status,
    "ctx-active",
    selection,
    44373).find((entry) => entry.port === 44373);
  const automatic = sandbox.missingPortRedirectOptions(
    {
      ...status,
      runtimeSettings: {
        ...status.runtimeSettings,
        browserMissingPortPolicies: [{
          contextId: "ctx-active",
          port: 44373,
          mode: "Automatic"
        }]
      }
    },
    "ctx-active",
    selection,
    44373).find((entry) => entry.port === 44373);
  const disabled = sandbox.missingPortRedirectOptions(
    {
      ...status,
      runtimeSettings: {
        ...status.runtimeSettings,
        browserMissingPortPolicies: [{
          contextId: "ctx-active",
          port: 44373,
          mode: "Disabled"
        }]
      }
    },
    "ctx-active",
    selection,
    44373).find((entry) => entry.port === 44373);

  assert.equal(defaultPolicy.mode, "default");
  assert.equal(automatic.mode, "automatic");
  assert.equal(automatic.globalFallbackEnabled, false);
  assert.equal(disabled.mode, "none");
});

test("live status redraw is deferred while a missing-port select has focus", () => {
  document.activeElement = {
    matches(selector) {
      return selector === "select[data-missing-port-redirect]";
    }
  };
  assert.equal(sandbox.isMissingPortSelectActive(), true);

  document.activeElement = null;
  assert.equal(sandbox.isMissingPortSelectActive(), false);
});
