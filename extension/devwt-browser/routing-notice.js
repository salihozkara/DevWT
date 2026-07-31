const DEVWT_ROUTING_NOTICE_HOST_ID = "devwt-routing-notice-host";
let routingNoticeDismissed = false;

chrome.runtime.onMessage.addListener((message) => {
  if (message?.type === "devwt-routing-notice") {
    updateRoutingNotice(message.value);
  }
});

chrome.runtime.sendMessage({ type: "routing-notice" })
  .then((response) => {
    if (response?.ok) {
      updateRoutingNotice(response.value);
    }
  })
  .catch(() => {});

function updateRoutingNotice(notice) {
  const existing = document.getElementById(DEVWT_ROUTING_NOTICE_HOST_ID);
  if (!notice) {
    existing?.remove();
    return;
  }
  if (routingNoticeDismissed || !document.documentElement) {
    return;
  }

  const host = existing || document.createElement("div");
  host.id = DEVWT_ROUTING_NOTICE_HOST_ID;
  if (!existing) {
    document.documentElement.appendChild(host);
  }

  const shadow = host.shadowRoot || host.attachShadow({ mode: "open" });
  shadow.replaceChildren();

  const style = document.createElement("style");
  style.textContent = `
    :host {
      all: initial;
      position: fixed;
      inset: 16px 16px auto auto;
      z-index: 2147483647;
      max-width: min(420px, calc(100vw - 32px));
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      color-scheme: light;
    }
    .notice {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) auto;
      gap: 10px;
      align-items: start;
      padding: 12px 13px;
      border: 1px solid #93c5fd;
      border-radius: 12px;
      background: #eff6ff;
      color: #172554;
      box-shadow: 0 14px 35px rgba(15, 23, 42, .22);
      font-size: 13px;
      line-height: 1.4;
    }
    .notice.unavailable {
      border-color: #fdba74;
      background: #fff7ed;
      color: #7c2d12;
    }
    .mark {
      display: grid;
      place-items: center;
      width: 27px;
      height: 27px;
      border-radius: 8px;
      background: #2563eb;
      color: white;
      font-size: 11px;
      font-weight: 800;
    }
    .unavailable .mark { background: #ea580c; }
    strong { display: block; margin-bottom: 2px; font-weight: 750; }
    p { margin: 0; }
    small { display: block; margin-top: 4px; opacity: .72; }
    button {
      all: unset;
      cursor: pointer;
      display: grid;
      place-items: center;
      width: 24px;
      height: 24px;
      border-radius: 7px;
      font-size: 18px;
      line-height: 1;
    }
    button:hover { background: rgba(15, 23, 42, .08); }
    button:focus-visible { outline: 2px solid currentColor; outline-offset: 1px; }
  `;

  const container = document.createElement("section");
  container.className = `notice${notice.available ? "" : " unavailable"}`;
  container.setAttribute("role", "status");
  container.setAttribute("aria-live", "polite");

  const mark = document.createElement("span");
  mark.className = "mark";
  mark.textContent = "DW";

  const copy = document.createElement("div");
  const title = document.createElement("strong");
  const detail = document.createElement("p");
  const source = document.createElement("small");
  if (notice.kind === "fallback") {
    title.textContent = `DevWT automatic fallback · localhost:${notice.port}`;
    detail.textContent = `${notice.activeLabel} is not listening, so DevWT is using its normal routing decision chain.`;
    source.textContent = notice.source === "worktree"
      ? "Source: Worktree port policy"
      : "Source: Global Console default";
  } else {
    title.textContent = notice.available
      ? `DevWT redirect active · localhost:${notice.port}`
      : `DevWT redirect unavailable · localhost:${notice.port}`;
    detail.textContent = notice.available
      ? `${notice.activeLabel} is not listening, so requests target ${notice.providerLabel}.`
      : `${notice.providerLabel} is configured, but it is not listening on this port. Requests may return 502.`;
    source.textContent = "Source: Worktree port policy";
  }
  copy.append(title, detail, source);

  const close = document.createElement("button");
  close.type = "button";
  close.setAttribute("aria-label", "Dismiss DevWT routing notice");
  close.title = "Dismiss";
  close.textContent = "×";
  close.addEventListener("click", () => {
    routingNoticeDismissed = true;
    host.remove();
  });

  container.append(mark, copy, close);
  shadow.append(style, container);
}
