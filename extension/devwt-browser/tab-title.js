let contextLabel = "";
let pageTitle = document.title;
let titleObserver = null;

chrome.runtime.onMessage.addListener((message) => {
  if (message?.type === "devwt-tab-label") {
    setContextLabel(message.label);
  }
});

chrome.runtime.sendMessage({ type: "tab-context-label" })
  .then((response) => {
    if (response?.ok) {
      setContextLabel(response.value);
    }
  })
  .catch(() => {});

function setContextLabel(value) {
  const nextLabel = typeof value === "string" ? value.trim() : "";
  const currentTitle = document.title;
  const currentPrefix = titlePrefix(contextLabel);
  pageTitle = contextLabel && currentTitle.startsWith(currentPrefix)
    ? currentTitle.slice(currentPrefix.length)
    : currentTitle;
  contextLabel = nextLabel;

  if (!contextLabel) {
    stopObservingTitle();
    if (document.title !== pageTitle) {
      document.title = pageTitle;
    }
    return;
  }

  startObservingTitle();
  applyContextTitle();
}

function startObservingTitle() {
  if (titleObserver || !document.documentElement) {
    return;
  }

  titleObserver = new MutationObserver(() => applyContextTitle());
  titleObserver.observe(document.documentElement, {
    subtree: true,
    childList: true,
    characterData: true
  });
}

function stopObservingTitle() {
  titleObserver?.disconnect();
  titleObserver = null;
}

function applyContextTitle() {
  if (!contextLabel) {
    return;
  }

  const prefix = titlePrefix(contextLabel);
  const currentTitle = document.title;
  const expectedTitle = `${prefix}${pageTitle}`;
  if (currentTitle === expectedTitle) {
    return;
  }

  pageTitle = currentTitle.startsWith(prefix)
    ? currentTitle.slice(prefix.length)
    : currentTitle;
  document.title = `${prefix}${pageTitle}`;
}

function titlePrefix(label) {
  return label ? `[${label}] ` : "";
}
