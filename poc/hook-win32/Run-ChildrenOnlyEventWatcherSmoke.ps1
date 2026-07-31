param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55399
)

$ErrorActionPreference = "Stop"

$watcher = Join-Path $ArtifactsPath "devwt-folder-watcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-bind-probe.exe"
$spawner = Join-Path $ArtifactsPath "devwt-child-spawner.exe"

foreach ($path in @($watcher, $hookDll, $probe, $spawner)) {
    if (-not (Test-Path $path)) {
        throw "Missing children-only event watcher artifact: $path"
    }
}

$root = Join-Path $ArtifactsPath "children-only-event-watcher-root"
New-Item -ItemType Directory -Path $root -Force | Out-Null
$mapFile = Join-Path $ArtifactsPath "children-only-event-watcher-map.tsv"
Set-Content -Path $mapFile -Encoding ascii -Value "$root`t127.80.0.87`t127.80.0.87"

$watcherOut = Join-Path $ArtifactsPath "children-only-event-watcher.out"
$watcherErr = Join-Path $ArtifactsPath "children-only-event-watcher.err"
$watcherLog = Join-Path $ArtifactsPath "children-only-event-watcher.log"
$spawnerOut = Join-Path $ArtifactsPath "children-only-event-watcher-spawner.out"
$spawnerErr = Join-Path $ArtifactsPath "children-only-event-watcher-spawner.err"
Remove-Item $watcherOut, $watcherErr, $watcherLog, $spawnerOut, $spawnerErr -ErrorAction SilentlyContinue

$watcherProcess = Start-Process -FilePath $watcher -ArgumentList @(
    "--process-events",
    "--dll", $hookDll,
    "--map", "$root=127.80.0.87,127.80.0.87",
    "--map-file", $mapFile,
    "--children-only-image", $spawner,
    "--poll-ms", "10000",
    "--duration-ms", "7000",
    "--log", $watcherLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watcherOut -RedirectStandardError $watcherErr

Start-Sleep -Milliseconds 400
$spawnerProcess = Start-Process -FilePath $spawner -ArgumentList @(
    "--spawn-delay-ms", "1200",
    "--",
    $probe,
    "--port", "$Port",
    "--label", "children-only-event-watcher-child",
    "--hold-ms", "4000"
) -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput $spawnerOut -RedirectStandardError $spawnerErr

try {
    Start-Sleep -Milliseconds 2200
    $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object LocalAddress,LocalPort,OwningProcess

    if (-not ($listeners | Where-Object { $_.LocalAddress -eq "127.80.0.87" })) {
        $out = Get-Content $spawnerOut -Raw -ErrorAction SilentlyContinue
        $err = Get-Content $spawnerErr -Raw -ErrorAction SilentlyContinue
        $log = Get-Content $watcherLog -Raw -ErrorAction SilentlyContinue
        $watcherError = Get-Content $watcherErr -Raw -ErrorAction SilentlyContinue
        if ($log -match "PROCESS_EVENTS_UNAVAILABLE") {
            "children-only-event-watcher-smoke skipped: process events unavailable in this session. Watcher=$($log -replace '\s+$', '')"
            return
        }

        throw "event watcher did not hook child before bind. Listeners=$($listeners | Out-String) Out=$out Err=$err WatcherLog=$log WatcherErr=$watcherError"
    }

    if (-not $spawnerProcess.WaitForExit(8000)) {
        throw "children-only event watcher spawner timed out"
    }

    $out = Get-Content $spawnerOut -Raw -ErrorAction SilentlyContinue
    $log = Get-Content $watcherLog -Raw -ErrorAction SilentlyContinue
    if ($log -notmatch "INJECTED_CHILDREN_ONLY") {
        throw "event watcher did not report children-only parent injection. Watcher=$log"
    }

    "children-only-event-watcher-smoke ok"
    "spawner: $($out -replace '\s+$', '')"
    "watcher: $($log -replace '\s+$', '')"
}
finally {
    foreach ($process in @($spawnerProcess, $watcherProcess)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}
