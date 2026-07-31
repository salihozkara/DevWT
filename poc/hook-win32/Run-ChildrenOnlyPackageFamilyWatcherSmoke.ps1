param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts")
)

$ErrorActionPreference = "Stop"

$watcher = Join-Path $ArtifactsPath "devwt-folder-watcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"

foreach ($path in @($watcher, $hookDll)) {
    if (-not (Test-Path $path)) {
        throw "Missing package-family watcher artifact: $path"
    }
}

$root = Join-Path $ArtifactsPath "package-family-watcher-root"
New-Item -ItemType Directory -Path $root -Force | Out-Null
$mapFile = Join-Path $ArtifactsPath "package-family-watcher-map.tsv"
Set-Content -Path $mapFile -Encoding ascii -Value "$root`t127.80.0.87`t127.80.0.87"

$watcherOut = Join-Path $ArtifactsPath "package-family-watcher.out"
$watcherErr = Join-Path $ArtifactsPath "package-family-watcher.err"
$watcherLog = Join-Path $ArtifactsPath "package-family-watcher.log"
Remove-Item $watcherOut, $watcherErr, $watcherLog -ErrorAction SilentlyContinue

$watcherProcess = Start-Process -FilePath $watcher -ArgumentList @(
    "--dll", $hookDll,
    "--map", "$root=127.80.0.87,127.80.0.87",
    "--map-file", $mapFile,
    "--children-only-package-family", "DevWT.NoSuchPackage_0000000000000",
    "--poll-ms", "50",
    "--duration-ms", "200",
    "--log", $watcherLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watcherOut -RedirectStandardError $watcherErr

if (-not $watcherProcess.WaitForExit(5000)) {
    $watcherProcess.Kill()
    throw "package-family watcher smoke timed out"
}

$out = Get-Content $watcherOut -Raw -ErrorAction SilentlyContinue
$err = Get-Content $watcherErr -Raw -ErrorAction SilentlyContinue
$log = Get-Content $watcherLog -Raw -ErrorAction SilentlyContinue
if ($watcherProcess.ExitCode -ne 0) {
    throw "package-family watcher exited with $($watcherProcess.ExitCode). Out=$out Err=$err Log=$log"
}

"children-only-package-family-watcher-smoke ok"
