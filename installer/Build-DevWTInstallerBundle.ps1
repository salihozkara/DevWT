[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [string] $OutputRoot
)

$ErrorActionPreference = "Stop"

$installerRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $installerRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\installer'
}

$bundleRoot = Join-Path $OutputRoot 'DevWT'
$appRoot = Join-Path $bundleRoot 'app'
$hookRoot = Join-Path $appRoot 'hook'
$extensionRoot = Join-Path $bundleRoot 'extension'
$zipPath = Join-Path $OutputRoot 'DevWT-installer.zip'

Remove-Item -Path $bundleRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
New-Item -ItemType Directory -Path $hookRoot -Force | Out-Null
New-Item -ItemType Directory -Path $extensionRoot -Force | Out-Null

$cliProject = Join-Path $repoRoot 'src\Devwt.Cli\Devwt.Cli.csproj'
& dotnet publish $cliProject -c $Configuration -o $appRoot --no-restore `
    -p:ContinuousIntegrationBuild=true `
    "-p:PathMap=$repoRoot=/_/" `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$hookPocRoot = Join-Path $repoRoot 'poc\hook-win32'
$hookArtifacts = Join-Path $OutputRoot 'hook-build'
Remove-Item -Path $hookArtifacts -Recurse -Force -ErrorAction SilentlyContinue
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $hookPocRoot 'Build-HookPoc.ps1') -Configuration $Configuration -ArtifactsPath $hookArtifacts -RuntimeOnly
if ($LASTEXITCODE -ne 0) {
    throw "hook runtime build failed with exit code $LASTEXITCODE."
}

foreach ($artifact in @('devwt-hook.dll', 'devwt-hook-launcher.exe', 'devwt-folder-watcher.exe')) {
    Copy-Item -Path (Join-Path $hookArtifacts $artifact) -Destination $hookRoot -Force
}

Copy-Item -Path (Join-Path $installerRoot 'Install-DevWT.ps1') -Destination $bundleRoot -Force
Copy-Item -Path (Join-Path $installerRoot 'Uninstall-DevWT.ps1') -Destination $bundleRoot -Force
Copy-Item -Path (Join-Path $installerRoot 'Reinstall-DevWTClean.ps1') -Destination $bundleRoot -Force
Copy-Item -Path (Join-Path $installerRoot 'Update-DevWTManaged.ps1') -Destination $bundleRoot -Force
Copy-Item -Path (Join-Path $repoRoot 'extension\devwt-browser') -Destination $extensionRoot -Recurse -Force

Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $zipPath -Force

Write-Host "DevWT installer bundle:"
Write-Host "  $bundleRoot"
Write-Host "Zip:"
Write-Host "  $zipPath"
