# Playwright MCP Routing

Use this workflow only after `devwt status` confirms that the target worktree is
registered and identifies its context ID. A normal Playwright MCP session does
not need the DevWT browser extension for HTTP or inspected HTTPS.

## Select A Context Before Navigation

Use the Playwright MCP code-execution tool and perform the header assignment and
first navigation in one call:

```javascript
async (page) => {
  const contextId = "ctx-replace-with-the-intended-context";
  await page.setExtraHTTPHeaders({
    "X-DevWT-Context": contextId
  });

  const response = await page.goto("http://localhost:44334/", {
    waitUntil: "domcontentloaded"
  });

  return {
    status: response?.status(),
    selectedContext: response?.headers()["x-devwt-context"],
    description: response?.headers()["x-devwt-description"],
    routeReason: response?.headers()["x-devwt-route-reason"],
    finalUrl: page.url()
  };
}
```

The header remains active for later navigation, redirects, and subresources on
that page. It is removed before the request reaches the backend application.
Use a dedicated MCP page for one DevWT context. When a new page is created,
apply the header before that page's first navigation.

Do not navigate first and set the header afterward. The initial request can
otherwise establish an incorrect fallback or remembered route.

## HTTP And HTTPS

- HTTP can inspect `X-DevWT-Context` directly.
- HTTPS can inspect it only when the endpoint uses `Auto` with a trusted DevWT
  gateway root, or explicitly uses `HTTP Inspect`.
- HTTP/1.1, HTTP/2, redirects, WebSocket handshakes, and page subresources retain
  the page-level selection when they use inspected HTTP/HTTPS.
- `TLS Tunnel` and `Raw` cannot read the encrypted header. Use a process,
  session, or per-port target for those modes.

For an HTTPS certificate error, inspect state without changing it:

```powershell
devwt gateway-cert status
```

Do not trust or replace certificates automatically. Only after explicit user
authorization may the user-level trust command be used:

```powershell
devwt gateway-cert trust --user
```

The endpoint handling mode is selected in Console Routing for the relevant
IP/port. Do not force `HTTP Inspect` for custom TLS or non-HTTP protocols.

## Verify The Result

Compare the response headers with the requested context:

- `X-DevWT-Context` must equal the intended context ID.
- `X-DevWT-Description` should identify the intended worktree when configured.
- `X-DevWT-Route-Reason` is normally `browser-active`; it can be
  `single-target` when only one eligible route exists.

A compact verification call is:

```javascript
async (page) => {
  const response = await page.reload({ waitUntil: "domcontentloaded" });
  return {
    context: response?.headers()["x-devwt-context"],
    reason: response?.headers()["x-devwt-route-reason"]
  };
}
```

## Clear Or Switch

Switch the same page by replacing the header and then reloading:

```javascript
async (page) => {
  await page.setExtraHTTPHeaders({
    "X-DevWT-Context": "ctx-next-context"
  });
  return (await page.reload({ waitUntil: "domcontentloaded" }))
    ?.headers()["x-devwt-context"];
}
```

After the task, close the dedicated page. If the current page must be reused and
this workflow was the only source of extra headers, clear them:

```javascript
async (page) => {
  await page.setExtraHTTPHeaders({});
}
```

Do not clear page headers when another tool or test configured unrelated extra
headers; close the dedicated page instead.

## Fallback When Headers Cannot Be Inspected

Use the narrowest available identity:

1. Route a known Playwright controller or browser PID:

   ```powershell
   devwt proxy process target --pid <pid> --context <context-id>
   ```

2. Launch the listener and Playwright root with the same
   `DEVWT_SESSION_ID` when their launch is under your control.
3. Configure a per-port target:

   ```powershell
   devwt proxy target --context <context-id> --port <port>
   ```

Clear temporary process or port overrides after the test. Do not use a global
context merely because an MCP browser PID is unavailable.
