param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55281
)

$ErrorActionPreference = "Stop"

$watcher = Join-Path $ArtifactsPath "devwt-folder-watcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-bind-probe.exe"

foreach ($path in @($watcher, $hookDll, $probe)) {
    if (-not (Test-Path $path)) {
        throw "Missing force-folder POC artifact: $path"
    }
}

$forceRoot = Join-Path $ArtifactsPath "force-folders"
$folderA = Join-Path $forceRoot "ctx-a"
$folderB = Join-Path $forceRoot "ctx-b"
New-Item -ItemType Directory -Path $folderA, $folderB -Force | Out-Null

$watchLog = Join-Path $ArtifactsPath "force-folder-watcher.log"
$watchOut = Join-Path $ArtifactsPath "force-folder-watcher.out"
$watchErr = Join-Path $ArtifactsPath "force-folder-watcher.err"
$outA = Join-Path $ArtifactsPath "force-a.out"
$outB = Join-Path $ArtifactsPath "force-b.out"
$errA = Join-Path $ArtifactsPath "force-a.err"
$errB = Join-Path $ArtifactsPath "force-b.err"
Remove-Item $watchLog, $watchOut, $watchErr, $outA, $outB, $errA, $errB -ErrorAction SilentlyContinue

$watcherProcess = Start-Process $watcher -ArgumentList @(
    "--dll", $hookDll,
    "--map", "$folderA=127.80.0.40",
    "--map", "$folderB=127.80.0.41",
    "--poll-ms", "20",
    "--duration-ms", "12000",
    "--log", $watchLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watchOut -RedirectStandardError $watchErr

Start-Sleep -Milliseconds 400
$a = Start-Process $probe -WorkingDirectory $folderA -ArgumentList @("--startup-delay-ms", "1200", "--port", "$Port", "--label", "force-a", "--hold-ms", "4000") -PassThru -NoNewWindow -RedirectStandardOutput $outA -RedirectStandardError $errA
$b = Start-Process $probe -WorkingDirectory $folderB -ArgumentList @("--startup-delay-ms", "1200", "--port", "$Port", "--label", "force-b", "--hold-ms", "4000") -PassThru -NoNewWindow -RedirectStandardOutput $outB -RedirectStandardError $errB

try {
    if (-not $a.WaitForExit(7000)) {
        throw "force-a timed out"
    }

    if (-not $b.WaitForExit(7000)) {
        throw "force-b timed out"
    }

    $a.Refresh()
    $b.Refresh()

    $aOut = Get-Content $outA -Raw -ErrorAction SilentlyContinue
    $bOut = Get-Content $outB -Raw -ErrorAction SilentlyContinue
    $aErr = Get-Content $errA -Raw -ErrorAction SilentlyContinue
    $bErr = Get-Content $errB -Raw -ErrorAction SilentlyContinue
    $log = Get-Content $watchLog -Raw -ErrorAction SilentlyContinue

    if ($aOut -notmatch "BOUND force-a 127\.0\.0\.1:$($Port)") {
        throw "force-a did not report localhost to the app. Exit=$($a.ExitCode) Out=$aOut Err=$aErr Log=$log"
    }

    if ($bOut -notmatch "BOUND force-b 127\.0\.0\.1:$($Port)") {
        throw "force-b did not report localhost to the app. Exit=$($b.ExitCode) Out=$bOut Err=$bErr Log=$log"
    }

    "force-folder-watcher-smoke ok"
    "force-a: $($aOut -replace '\s+$', '')"
    "force-b: $($bOut -replace '\s+$', '')"
}
finally {
    foreach ($process in @($a, $b, $watcherProcess)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}
