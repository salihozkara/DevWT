param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $PortBase = 55300,
    [int] $Iterations = 10
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

$forceRoot = Join-Path $ArtifactsPath "force-fast-folders"
$folderA = Join-Path $forceRoot "ctx-a"
$folderB = Join-Path $forceRoot "ctx-b"
New-Item -ItemType Directory -Path $folderA, $folderB -Force | Out-Null

$bothHooked = 0
$raceLost = 0

for ($i = 0; $i -lt $Iterations; $i++) {
    $port = $PortBase + $i
    $watchLog = Join-Path $ArtifactsPath "force-fast-$i-watcher.log"
    $watchOut = Join-Path $ArtifactsPath "force-fast-$i-watcher.out"
    $watchErr = Join-Path $ArtifactsPath "force-fast-$i-watcher.err"
    $outA = Join-Path $ArtifactsPath "force-fast-$i-a.out"
    $outB = Join-Path $ArtifactsPath "force-fast-$i-b.out"
    $errA = Join-Path $ArtifactsPath "force-fast-$i-a.err"
    $errB = Join-Path $ArtifactsPath "force-fast-$i-b.err"
    Remove-Item $watchLog, $watchOut, $watchErr, $outA, $outB, $errA, $errB -ErrorAction SilentlyContinue

    $watcherProcess = Start-Process $watcher -ArgumentList @(
        "--dll", $hookDll,
        "--map", "$folderA=127.80.0.50",
        "--map", "$folderB=127.80.0.51",
        "--poll-ms", "1",
        "--duration-ms", "5000",
        "--log", $watchLog
    ) -PassThru -NoNewWindow -RedirectStandardOutput $watchOut -RedirectStandardError $watchErr

    Start-Sleep -Milliseconds 100
    $a = Start-Process $probe -WorkingDirectory $folderA -ArgumentList @("--startup-delay-ms", "0", "--port", "$port", "--label", "fast-a", "--hold-ms", "1500") -PassThru -NoNewWindow -RedirectStandardOutput $outA -RedirectStandardError $errA
    $b = Start-Process $probe -WorkingDirectory $folderB -ArgumentList @("--startup-delay-ms", "0", "--port", "$port", "--label", "fast-b", "--hold-ms", "1500") -PassThru -NoNewWindow -RedirectStandardOutput $outB -RedirectStandardError $errB

    try {
        [void] $a.WaitForExit(4000)
        [void] $b.WaitForExit(4000)
        $aOut = Get-Content $outA -Raw -ErrorAction SilentlyContinue
        $bOut = Get-Content $outB -Raw -ErrorAction SilentlyContinue
        $aErr = Get-Content $errA -Raw -ErrorAction SilentlyContinue
        $bErr = Get-Content $errB -Raw -ErrorAction SilentlyContinue

        $okA = $aOut -match "BOUND fast-a 127\.0\.0\.1:$($port)"
        $okB = $bOut -match "BOUND fast-b 127\.0\.0\.1:$($port)"
        if ($okA -and $okB) {
            $bothHooked++
            "iteration ${i}: both hooked"
        } else {
            $raceLost++
            "iteration ${i}: race lost; aOut=$($aOut -replace '\s+$', '') aErr=$($aErr -replace '\s+$', '') bOut=$($bOut -replace '\s+$', '') bErr=$($bErr -replace '\s+$', '')"
        }
    }
    finally {
        foreach ($process in @($a, $b, $watcherProcess)) {
            if ($process -and -not $process.HasExited) {
                $process.Kill()
            }
        }
    }
}

"force-folder-fast-race summary: bothHooked=$bothHooked raceLost=$raceLost iterations=$Iterations"
