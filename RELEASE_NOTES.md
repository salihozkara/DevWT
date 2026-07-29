# DevWT v0.1.0-preview.3

Driver-free Windows preview with a redesigned management experience and
context-aware browser workflow.

## Included

- Driver-free user-mode hook runtime for isolated localhost ports per worktree.
- Context-aware TCP/UDP gateway routing over IPv4 and IPv6.
- Redesigned Web Console with Overview, Routing, Contexts, Activity, Tools, and
  Settings workspaces.
- Web Console actions for repositories, context descriptions, port inspection,
  linked worktrees, IDE watchers, and proxy-child management.
- Chrome/Edge extension `0.3.11` with context search, optional tab grouping,
  optional context titles, HTTP/HTTPS open actions, other-port discovery, and
  backend process labels.
- Dragging a localhost tab into a DevWT-created group changes that tab to the
  group context and preserves it across localhost ports.
- Managed service updater with endpoint verification and rollback.
- Installer deployment of the unpacked Chrome/Edge extension.

## Requirements

- Windows 10 or Windows 11, x64.
- Administrator rights for installation.
- Git for Windows.
- .NET 10 ASP.NET Core Runtime, x64.

## Install

Extract `DevWT-v0.1.0-preview.3-installer.zip`, open PowerShell as
Administrator in the extracted directory, and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-DevWT.ps1
```

Then open a new terminal and verify:

```powershell
devwt service status
devwt status
```

The Web Console listens at `http://127.0.0.1:17776/`.

To load the browser extension, enable Developer mode in Chrome or Edge, choose
**Load unpacked**, and select:

```text
C:\Program Files\DevWT\extension\devwt-browser
```

For managed CLI, service, Console, or extension-only upgrades:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Update-DevWTManaged.ps1
```

## Known Limitations

- Already-running, unhooked processes that bind immediately at startup can beat the folder watcher.
- VM smoke automation is still manual.
