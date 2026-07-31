# Release Process

DevWT preview releases ship a zip installer bundle containing the user-mode
hook runtime, service, CLI, Web Console, and browser extension.

## Local Release Build

```powershell
dotnet restore Devwt.slnx
dotnet build Devwt.slnx --no-restore -warnaserror
dotnet test Devwt.slnx --no-restore
.\installer\Build-DevWTInstallerBundle.ps1 -Configuration Release
.\installer\Test-DevWTReleaseBundle.ps1 -BundlePath .\artifacts\installer\DevWT-installer.zip
```

Output:

```txt
artifacts\installer\DevWT-installer.zip
```

## Windows Validation

Copy the zip into a disposable Windows VM, extract it, and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-DevWT.ps1 -RemoveState
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-DevWT.ps1
devwt status
```

Then verify a two-repo linked worktree scenario with `devwt terminal` or `devwt run`.

Keep two contexts listening on the same natural port and verify:

- IPv4 and IPv6 candidates are presented as one localhost context in extension `0.3.22`.
- Tab/context and managed-group/context links are restored after a browser restart
  and after reloading or updating the extension.
- One candidate routes with `X-DevWT-Route-Reason: single-target`.
- `Use in this tab` selects the intended context, hard reloads the tab, and closes the popup.
- HTTP and HTTPS actions create a new tab with the selected context before
  navigating to that localhost scheme.
- `Other ports` lists the remaining listeners from the same context and opens
  any of them over HTTP or HTTPS in a correctly selected new tab.
- The active card lists same-repository ports that are missing locally;
  selecting Automatic, a provider, or No redirect persists one context+port
  policy across all tabs using that worktree, and starting the active listener
  makes the active worktree win without clearing the policy.
- `Settings > Browser Missing-Port Fallback` is the default for pairs without a
  worktree policy. Automatic overrides an Off default, Console default clears
  the policy, and No redirect overrides an On default.
- URL `devwt-context` selectors and ordinary explicit context headers remain
  fail-closed even while global browser fallback is enabled.
- Effective redirects display the active/provider worktrees in the extension's
  dismissible Shadow DOM notice without rewriting the proxied HTTPS body.
- An assigned context with no listener on the current tab port remains visible
  as the Active card; its missing-port selector is expanded, always includes
  `Automatic`, and live status updates do not collapse an open select.
- Changing a missing-port choice keeps the popup open and does not reload the
  tab. `Use in this tab` retains its existing reload-and-close behavior.
- Live status redraws preserve expanded `Other ports` and technical-detail
  panels.
- Main and additional port labels show the short backend process name, with a
  PID fallback.
- Context grouping is off on first use; enabling it groups existing selections,
  and changing one tab's context moves it to the new group.
- Dragging a localhost tab into a DevWT-created group selects that context as
  the active worktree; navigating the grouped tab to another localhost port
  keeps the group context.
- Context title prefixes are off on first use; enabling them updates existing
  selections with description or Git ref, and disabling them restores page titles.
- Popup search filters context name, description, branch, ID, worktree path,
  and all ports from that context; active matches sort first, technical details expand on demand, and
  the popup uses one page scrollbar instead of a nested context-list scrollbar.
- Inspected HTTP and HTTPS responses expose context, route-reason, and description headers.
- Plain-HTTP and HTTPS backends both work behind browser-side HTTPS.
- HTTP/2, WebSocket, and gRPC smoke cases use the Kestrel/YARP path.

While one hooked backend remains running, execute `Update-DevWTManaged.ps1`.
Confirm that its PID survives, the same original gateway endpoints return, the
hook root is unchanged, and `DevWTService` points to a new
`app-versions\<version>` directory. For a hook-changing release, run the same
check with `-UpdateHookRuntime`: the existing backend must survive on its loaded
DLL, the installed hook pointer must select a new immutable directory, and new
processes must use that directory.

## GitHub Release

```powershell
git tag v0.1.0-preview.6
git push origin v0.1.0-preview.6
gh release create v0.1.0-preview.6 `
  artifacts\installer\DevWT-v0.1.0-preview.6-installer.zip `
  artifacts\installer\DevWT-v0.1.0-preview.6-installer.zip.sha256 `
  --title "DevWT v0.1.0-preview.6" `
  --prerelease `
  --notes-file RELEASE_NOTES.md
```
