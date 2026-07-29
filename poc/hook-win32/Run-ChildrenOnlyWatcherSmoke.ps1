param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55389
)

$ErrorActionPreference = "Stop"

$watcher = Join-Path $ArtifactsPath "devwt-folder-watcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-bind-probe.exe"
$spawner = Join-Path $ArtifactsPath "devwt-child-spawner.exe"

foreach ($path in @($watcher, $hookDll, $probe, $spawner)) {
    if (-not (Test-Path $path)) {
        throw "Missing children-only watcher artifact: $path"
    }
}

$root = Join-Path $ArtifactsPath "children-only-watcher-root"
New-Item -ItemType Directory -Path $root -Force | Out-Null
$mapFile = Join-Path $ArtifactsPath "children-only-watcher-map.tsv"
Set-Content -Path $mapFile -Encoding ascii -Value "$root`t127.80.0.86`t127.80.0.86"

$watcherOut = Join-Path $ArtifactsPath "children-only-watcher.out"
$watcherErr = Join-Path $ArtifactsPath "children-only-watcher.err"
$watcherLog = Join-Path $ArtifactsPath "children-only-watcher.log"
$spawnerOut = Join-Path $ArtifactsPath "children-only-watcher-spawner.out"
$spawnerErr = Join-Path $ArtifactsPath "children-only-watcher-spawner.err"
Remove-Item $watcherOut, $watcherErr, $watcherLog, $spawnerOut, $spawnerErr -ErrorAction SilentlyContinue

$watcherProcess = Start-Process -FilePath $watcher -ArgumentList @(
    "--dll", $hookDll,
    "--map", "$root=127.80.0.86,127.80.0.86",
    "--map-file", $mapFile,
    "--children-only-image", $spawner,
    "--poll-ms", "50",
    "--duration-ms", "6000",
    "--log", $watcherLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watcherOut -RedirectStandardError $watcherErr

Start-Sleep -Milliseconds 300
$spawnerProcess = Start-Process -FilePath $spawner -ArgumentList @(
    "--spawn-delay-ms", "1000",
    "--",
    $probe,
    "--port", "$Port",
    "--label", "children-only-watcher-child",
    "--hold-ms", "4000"
) -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput $spawnerOut -RedirectStandardError $spawnerErr

try {
    Start-Sleep -Milliseconds 2200
    $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object LocalAddress,LocalPort,OwningProcess

    if (-not ($listeners | Where-Object { $_.LocalAddress -eq "127.80.0.86" })) {
        $out = Get-Content $spawnerOut -Raw -ErrorAction SilentlyContinue
        $err = Get-Content $spawnerErr -Raw -ErrorAction SilentlyContinue
        $log = Get-Content $watcherLog -Raw -ErrorAction SilentlyContinue
        throw "children-only watcher child was not hooked at OS level. Listeners=$($listeners | Out-String) Out=$out Err=$err Watcher=$log"
    }

    if (-not $spawnerProcess.WaitForExit(8000)) {
        throw "children-only watcher spawner timed out"
    }

    $out = Get-Content $spawnerOut -Raw -ErrorAction SilentlyContinue
    $err = Get-Content $spawnerErr -Raw -ErrorAction SilentlyContinue
    $log = Get-Content $watcherLog -Raw -ErrorAction SilentlyContinue

    if ([string]::IsNullOrWhiteSpace($out) -or $out -notmatch "BOUND children-only-watcher-child 127\.0\.0\.1:$($Port)") {
        throw "children-only watcher child did not report localhost to the app. Exit=$($spawnerProcess.ExitCode) Out=$out Err=$err Watcher=$log"
    }

    if ($log -notmatch "INJECTED_CHILDREN_ONLY") {
        throw "watcher did not report children-only parent injection. Watcher=$log"
    }

    "children-only-watcher-smoke ok"
    "spawner: $($out -replace '\s+$', '')"
    "os-listener: 127.80.0.86:$Port"
    "watcher: $($log -replace '\s+$', '')"
}
finally {
    foreach ($process in @($spawnerProcess, $watcherProcess)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}
