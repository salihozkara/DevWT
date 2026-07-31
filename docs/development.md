# Development

Requirements:

- Windows 11
- .NET SDK for the target framework in the project files
- Git for Windows
- Visual Studio C++ build tools for the hook runtime

Commands:

```powershell
dotnet build .\Devwt.slnx
dotnet test .\Devwt.slnx --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\poc\hook-win32\Build-HookPoc.ps1
dotnet run --project .\src\Devwt.Cli -- status
node --check .\extension\devwt-browser\background.js
node --check .\extension\devwt-browser\popup.js
node --check .\extension\devwt-browser\tab-title.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-DevWTInstallerBundle.ps1 -Configuration Release
```

For Console changes, run the service on a disposable machine or VM and verify
Routing, Contexts, Activity `Callers`/`Timeline`, and Settings at desktop and
mobile widths. Check that selecting a port or caller updates only the active
panel and that no page-level horizontal overflow appears.

For extension changes, increment `extension\devwt-browser\manifest.json`, run
`BrowserExtensionAssetTests`, rebuild the installer bundle, reload the unpacked
extension, and verify the first reloaded request carries the selected context.

Do not commit machine-specific files, generated bundles, logs, credentials, or
local test output.
