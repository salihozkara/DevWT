using System.Text.Json;

namespace Devwt.Service.Tests;

public sealed class BrowserExtensionAssetTests
{
    [Fact]
    public void Browser_extension_uses_gateway_mode_without_direct_ip_redirects()
    {
        var extensionRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(extensionRoot, "extension", "devwt-browser", "manifest.json");
        var backgroundPath = Path.Combine(extensionRoot, "extension", "devwt-browser", "background.js");
        var popupPath = Path.Combine(extensionRoot, "extension", "devwt-browser", "popup.js");
        var tabTitlePath = Path.Combine(extensionRoot, "extension", "devwt-browser", "tab-title.js");
        var routingNoticePath = Path.Combine(extensionRoot, "extension", "devwt-browser", "routing-notice.js");
        var installerPath = Path.Combine(extensionRoot, "installer", "Install-DevWT.ps1");

        Assert.True(File.Exists(manifestPath), $"Missing extension manifest: {manifestPath}");
        Assert.True(File.Exists(tabTitlePath), $"Missing extension tab title script: {tabTitlePath}");
        Assert.True(File.Exists(routingNoticePath), $"Missing extension routing notice script: {routingNoticePath}");
        Assert.True(File.Exists(installerPath), $"Missing installer: {installerPath}");
        var manifest = File.ReadAllText(manifestPath);
        using var manifestJson = JsonDocument.Parse(manifest);
        Assert.Equal(3, manifestJson.RootElement.GetProperty("manifest_version").GetInt32());
        Assert.Equal("0.3.22", manifestJson.RootElement.GetProperty("version").GetString());
        Assert.Contains("http://127.0.0.1:17776/*", manifest);
        Assert.Contains("ws://127.0.0.1:17776/*", manifest);
        Assert.Contains("http://[::1]/*", manifest);
        Assert.Contains("https://[::1]/*", manifest);
        Assert.Contains("declarativeNetRequestWithHostAccess", manifest, StringComparison.Ordinal);
        Assert.Contains("\"tabGroups\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"tab-title.js\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"routing-notice.js\"", manifest, StringComparison.Ordinal);

        var background = File.ReadAllText(backgroundPath);
        var popup = File.ReadAllText(popupPath);
        var tabTitle = File.ReadAllText(tabTitlePath);
        var routingNotice = File.ReadAllText(routingNoticePath);
        var installer = File.ReadAllText(installerPath);
        Assert.Contains("chrome.declarativeNetRequest.updateSessionRules", background, StringComparison.Ordinal);
        Assert.Contains("tabIds", background, StringComparison.Ordinal);
        Assert.Contains("modifyHeaders", background, StringComparison.Ordinal);
        Assert.Contains("x-devwt-context", background, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("devwt-context", background, StringComparison.Ordinal);
        Assert.Contains("queryTransform", background, StringComparison.Ordinal);
        Assert.Contains("removeParams", background, StringComparison.Ordinal);
        Assert.Contains("buildUrlContextRules", background, StringComparison.Ordinal);
        Assert.Contains("chrome.webNavigation.onBeforeNavigate", background, StringComparison.Ordinal);
        Assert.Contains("\"webNavigation\"", manifest, StringComparison.Ordinal);
        Assert.Contains("RULE_ID_SLOT_COUNT", background, StringComparison.Ordinal);
        Assert.Contains("replaceUrlContextRules", background, StringComparison.Ordinal);
        Assert.Contains("wss?", background, StringComparison.Ordinal);
        Assert.Contains("[::1]", background, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:17776/api/status", background, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:17776/api/action", background, StringComparison.Ordinal);
        Assert.Contains("ws://127.0.0.1:17776/hubs/status", background, StringComparison.Ordinal);
        Assert.Contains("new WebSocket", background, StringComparison.Ordinal);
        Assert.Contains("ensureStatusSocket", background, StringComparison.Ordinal);
        Assert.Contains("SIGNALR_RECORD_SEPARATOR", background, StringComparison.Ordinal);
        Assert.Contains("chrome.runtime.onConnect", background, StringComparison.Ordinal);
        Assert.Contains("devwt-status", background, StringComparison.Ordinal);
        Assert.Contains("clear-active-target", background, StringComparison.Ordinal);
        Assert.Contains("browserScoped", background, StringComparison.Ordinal);
        Assert.Contains("case \"set-port-redirect\"", background, StringComparison.Ordinal);
        Assert.Contains("case \"set-port-fallback\"", background, StringComparison.Ordinal);
        Assert.Contains("case \"disable-port-redirect\"", background, StringComparison.Ordinal);
        Assert.Contains("case \"clear-port-redirect\"", background, StringComparison.Ordinal);
        Assert.Contains("case \"routing-notice\"", background, StringComparison.Ordinal);
        Assert.Contains("_storedPortRedirects", background, StringComparison.Ordinal);
        Assert.DoesNotContain("portRedirects: {}", background, StringComparison.Ordinal);
        Assert.Contains("buildTabContextRules", background, StringComparison.Ordinal);
        Assert.Contains("browserFallbackOnMissingPort", background, StringComparison.Ordinal);
        Assert.Contains("browserMissingPortPolicies", background, StringComparison.Ordinal);
        Assert.Contains("set-browser-missing-port-policy", background, StringComparison.Ordinal);
        Assert.Contains("clear-browser-missing-port-policy", background, StringComparison.Ordinal);
        Assert.Contains("sendDevwtAction", background, StringComparison.Ordinal);
        Assert.Contains("X-DevWT-Allow-Fallback", background, StringComparison.Ordinal);
        Assert.DoesNotContain("function buildTabFallbackRule", background, StringComparison.Ordinal);
        Assert.Contains("buildRoutingNotice", background, StringComparison.Ordinal);
        Assert.Contains("buildTabHeaderRule", background, StringComparison.Ordinal);
        Assert.Contains("PORT_RULE_IDS_PER_TAB", background, StringComparison.Ordinal);
        Assert.Contains("route.contextId === activeContext.id && Number(route.port) === port", background, StringComparison.Ordinal);
        Assert.Contains("activeContext.repositoryId !== redirectContext?.repositoryId", background, StringComparison.Ordinal);
        Assert.Contains("The active worktree already listens", background, StringComparison.Ordinal);
        Assert.Contains("scheduleTabRuleReconcile", background, StringComparison.Ordinal);
        Assert.DoesNotContain("function buildTabPortRules", background, StringComparison.Ordinal);
        Assert.DoesNotContain("setBrowserTarget", background, StringComparison.Ordinal);
        Assert.Contains("case \"open-context\"", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabs.create({ url: \"about:blank\", active: true })", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabs.update(tab.id", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabs.remove(tab.id)", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabs.reload(tab.id, { bypassCache: true })", background, StringComparison.Ordinal);
        Assert.Contains("context?.description", background, StringComparison.Ordinal);
        Assert.Contains("context?.gitRef", background, StringComparison.Ordinal);
        Assert.Contains("tab-context-label", background, StringComparison.Ordinal);
        Assert.Contains("groupTabsByContext", background, StringComparison.Ordinal);
        Assert.Contains("showContextInTabTitle", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabs.group", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabs.ungroup", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabGroups.update", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabs.onUpdated", background, StringComparison.Ordinal);
        Assert.Contains("changeInfo.groupId", background, StringComparison.Ordinal);
        Assert.Contains("changeInfo.url", background, StringComparison.Ordinal);
        Assert.Contains("syncTabContextFromGroup", background, StringComparison.Ordinal);
        Assert.Contains("applyContextToTab", background, StringComparison.Ordinal);
        Assert.Contains("MANAGED_GROUP_KEY_PREFIX", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabGroups.onRemoved", background, StringComparison.Ordinal);
        Assert.Contains("chrome.storage.local.get", background, StringComparison.Ordinal);
        Assert.Contains("chrome.storage.local.set", background, StringComparison.Ordinal);
        Assert.Contains("restorePersistedState", background, StringComparison.Ordinal);
        Assert.Contains("chrome.runtime.onStartup", background, StringComparison.Ordinal);
        Assert.Contains("chrome.runtime.onInstalled", background, StringComparison.Ordinal);
        Assert.Contains("chrome.tabs.onCreated", background, StringComparison.Ordinal);
        Assert.Contains("handleTabRemoved(tabId, removeInfo)", background, StringComparison.Ordinal);
        Assert.Contains("removeInfo?.isWindowClosing", background, StringComparison.Ordinal);
        Assert.Contains("schedulePersistedStateRestore", background, StringComparison.Ordinal);
        Assert.Contains("entry.sourceKey !== tabKey(entry.tab.id)", background, StringComparison.Ordinal);
        Assert.Contains("entries.length && managedGroupKeys.length", background, StringComparison.Ordinal);
        Assert.Contains("chrome.declarativeNetRequest.getSessionRules", background, StringComparison.Ordinal);
        Assert.Contains("persisted.selection.url !== tab.url", background, StringComparison.Ordinal);
        Assert.Contains("groupTitles.get(tab.groupId) === persisted.selection.label", background, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.storage.session", background, StringComparison.Ordinal);
        Assert.Contains("[GROUP_TABS_SETTING_KEY]: false", background, StringComparison.Ordinal);
        Assert.Contains("[TAB_TITLE_SETTING_KEY]: false", background, StringComparison.Ordinal);
        Assert.DoesNotContain("127.80.", background, StringComparison.Ordinal);
        Assert.Contains("localhost", popup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("context?.description", popup, StringComparison.Ordinal);
        Assert.Contains("context?.gitRef", popup, StringComparison.Ordinal);
        Assert.Contains("<span class=\"muted\">Context</span>", popup, StringComparison.Ordinal);
        Assert.Contains("${escapeHtml(route.contextId)}</code>", popup, StringComparison.Ordinal);
        Assert.Contains("routeAddressFamilies", popup, StringComparison.Ordinal);
        Assert.Contains("variants: [route]", popup, StringComparison.Ordinal);
        Assert.Contains("chrome.runtime.connect", popup, StringComparison.Ordinal);
        Assert.Contains("window.close()", popup, StringComparison.Ordinal);
        Assert.Contains("set-tab-grouping", popup, StringComparison.Ordinal);
        Assert.Contains("set-tab-title", popup, StringComparison.Ordinal);
        Assert.Contains("filterRoutes", popup, StringComparison.Ordinal);
        Assert.Contains("normalizeSearch", popup, StringComparison.Ordinal);
        Assert.Contains("context?.description", popup, StringComparison.Ordinal);
        Assert.Contains("context?.gitRef", popup, StringComparison.Ordinal);
        Assert.Contains("route.worktreeRootPath", popup, StringComparison.Ordinal);
        Assert.Contains("data-open-context", popup, StringComparison.Ordinal);
        Assert.Contains("renderProtocolButton", popup, StringComparison.Ordinal);
        Assert.Contains("type: \"open-context\"", popup, StringComparison.Ordinal);
        Assert.Contains("type: \"set-port-redirect\"", popup, StringComparison.Ordinal);
        Assert.Contains("type: \"set-port-fallback\"", popup, StringComparison.Ordinal);
        Assert.Contains("type: \"disable-port-redirect\"", popup, StringComparison.Ordinal);
        Assert.Contains("type: \"clear-port-redirect\"", popup, StringComparison.Ordinal);
        Assert.Contains("Automatic fallback · always", popup, StringComparison.Ordinal);
        Assert.Contains("Console default ·", popup, StringComparison.Ordinal);
        Assert.Contains("Worktree missing-port policy", popup, StringComparison.Ordinal);
        Assert.Contains("renderMissingPortRedirects", popup, StringComparison.Ordinal);
        Assert.Contains("missingPortRedirectOptions", popup, StringComparison.Ordinal);
        Assert.Contains("buildAssignedMissingRoute", popup, StringComparison.Ordinal);
        Assert.Contains("missing-listener", popup, StringComparison.Ordinal);
        Assert.Contains("isMissingPortSelectActive", popup, StringComparison.Ordinal);
        Assert.Contains("Saved worktree policy", popup, StringComparison.Ordinal);
        Assert.Contains("No redirect · stay here", popup, StringComparison.Ordinal);
        Assert.Contains("Applies to every extension tab that uses this active worktree.", popup, StringComparison.Ordinal);
        Assert.Contains("otherRoutesForContext", popup, StringComparison.Ordinal);
        Assert.Contains("renderOtherPorts", popup, StringComparison.Ordinal);
        Assert.Contains("Other ports", popup, StringComparison.Ordinal);
        Assert.Contains("contextRoutes.flatMap", popup, StringComparison.Ordinal);
        Assert.Contains("expandedRouteDetails", popup, StringComparison.Ordinal);
        Assert.Contains("data-detail-key", popup, StringComparison.Ordinal);
        Assert.Contains("bindRouteDetails", popup, StringComparison.Ordinal);
        Assert.Contains("details.open", popup, StringComparison.Ordinal);
        Assert.Contains("routeProcessLabel", popup, StringComparison.Ordinal);
        Assert.Contains("shortProcessName", popup, StringComparison.Ordinal);
        Assert.Contains("item.processName", popup, StringComparison.Ordinal);
        Assert.Contains("<span class=\"muted\">Process</span>", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("route.contextId}:${route.listenIp", popup, StringComparison.Ordinal);
        Assert.Contains("document.title", tabTitle, StringComparison.Ordinal);
        Assert.Contains("MutationObserver", tabTitle, StringComparison.Ordinal);
        Assert.Contains("devwt-tab-label", tabTitle, StringComparison.Ordinal);
        Assert.Contains("devwt-routing-notice", routingNotice, StringComparison.Ordinal);
        Assert.Contains("attachShadow", routingNotice, StringComparison.Ordinal);
        Assert.Contains("Source: Global Console default", routingNotice, StringComparison.Ordinal);
        Assert.Contains("Source: Worktree port policy", routingNotice, StringComparison.Ordinal);
        Assert.Contains("DevWT automatic fallback", routingNotice, StringComparison.Ordinal);
        Assert.Contains("Requests may return 502.", routingNotice, StringComparison.Ordinal);
        Assert.Contains("extension\\devwt-browser", installer, StringComparison.Ordinal);
        Assert.Contains("$extensionDestination", installer, StringComparison.Ordinal);

        var popupHtmlPath = Path.Combine(extensionRoot, "extension", "devwt-browser", "popup.html");
        var popupHtml = File.ReadAllText(popupHtmlPath);
        Assert.Contains("Group tabs by context", popupHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"group-tabs\"", popupHtml, StringComparison.Ordinal);
        Assert.Contains("Show context in tab title", popupHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"show-tab-title\"", popupHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"context-search\"", popupHtml, StringComparison.Ordinal);
        Assert.Contains("Search contexts, branches, paths", popupHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"results-summary\"", popupHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Port routing for this tab", popupHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"port-routing-list\"", popupHtml, StringComparison.Ordinal);
        Assert.Contains("Clear active worktree", popupHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_extension_documentation_explains_yarp_inspection_protocols()
    {
        var extensionRoot = FindRepositoryRoot();
        var readmePath = Path.Combine(extensionRoot, "extension", "devwt-browser", "README.md");

        Assert.True(File.Exists(readmePath), $"Missing extension README: {readmePath}");
        var readme = File.ReadAllText(readmePath);
        Assert.Contains("Gateway mode", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localhost", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-DevWT-Context", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-DevWT-Description", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HTTPS", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Auto", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Inspect", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tunnel", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HTTP/2", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("YARP", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-HTTP TLS", readme, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Devwt.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Devwt.slnx from test output directory.");
    }
}
