# DevWT v0.1.0-preview.6

Public Windows preview with visual documentation, a checksum-verified
latest-release installer, and the current context-aware browser workflow.

> [!WARNING]
> This is an AI-developed MVP preview. The current codebase was written
> entirely by AI under human direction and validation. Features and behavior
> may change, and code quality, architecture, maintainability, documentation,
> and test depth will continue to improve in future iterations.

Preview.6 supersedes preview.5 and adds a self-update command. The complete
installer ZIP passes a no-remediation scan with current Microsoft Defender
security intelligence before publication.

## Included

- User-mode hook runtime for isolated localhost ports per worktree.
- Context-aware TCP/UDP gateway routing over IPv4 and IPv6.
- Redesigned Web Console with Overview, Routing, Contexts, Activity, Tools, and
  Settings workspaces.
- Web Console actions for repositories, context descriptions, port inspection,
  linked worktrees, IDE watchers, and proxy-child management.
- Chrome/Edge extension `0.3.22` with context search, optional tab grouping,
  optional context titles, HTTP/HTTPS open actions, other-port discovery,
  missing-port provider policies, and backend process labels.
- Dragging a localhost tab into a DevWT-created group changes that tab to the
  group context and preserves it across localhost ports.
- Managed service updater with endpoint verification and rollback.
- Installer deployment of the unpacked Chrome/Edge extension.
- Public extension and Web Console screenshots plus architecture and routing
  decision diagrams.
- Root `install.ps1` bootstrap that downloads the newest published release,
  verifies its SHA-256 checksum, and then starts the bundled installer.
- `devwt update` downloads and verifies the newest release, stages the new
  runtime, and preserves applications that are already running.
- `devwt update --stop-running-applications` explicitly terminates hooked
  applications before selecting the new runtime. It does not restart them.
- Windows PowerShell 5.1 compatibility when selecting the newest release from
  a repository with multiple published releases.

## Requirements

- Windows 10 or Windows 11, x64.
- Administrator rights for installation.
- Git for Windows.
- .NET 10 ASP.NET Core Runtime, x64.

## Install

Download and run the public bootstrap:

```powershell
Invoke-WebRequest https://raw.githubusercontent.com/salihozkara/DevWT/main/install.ps1 -OutFile .\install-devwt.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-devwt.ps1
```

Alternatively, extract `DevWT-v0.1.0-preview.6-installer.zip`, open PowerShell as
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

## Disclaimer

Use DevWT at your own risk. You are responsible for reviewing the software and
its scripts before use, maintaining appropriate backups, and any resulting
impact on your applications, development environments, data, or systems. The
software is provided under the MIT License, without warranty.
