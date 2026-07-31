# Roadmap

## Near Term

- Expand automated Windows coverage for same-port TCP/UDP, IPv4/IPv6, HTTP/2,
  HTTPS inspection, and managed rollback.
- Add browser-extension integration tests for tab rules, hard reload, popup lifecycle, and stale unpacked versions.
- Harden process-start event recovery and measure the remaining best-effort injection race when polling is required.
- Add retention and cleanup policy for old `app-versions` and immutable hook-version directories.
- Surface unrelated host port owners and destructive proxy-owner shutdown impact more explicitly in Console.
- Expand route-decision latency and cache-invalidation diagnostics without growing persistent history.

## Later

- MSI/MSIX or winget packaging.
- Native IDE integrations beyond the generic watcher.
- Better Git hook coverage for unusual worktree flows.
- Optional browser profile management.
- SQL Server and LocalDB compatibility documentation.
