param(
    [string]$ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int]$Port = 55421
)

$ErrorActionPreference = "Stop"

$artifacts = [System.IO.Path]::GetFullPath($ArtifactsPath)
$launcher = Join-Path $artifacts "devwt-hook-launcher.exe"
$watcher = Join-Path $artifacts "devwt-folder-watcher.exe"
$sourceHook = Join-Path $artifacts "devwt-hook.dll"
$probe = Join-Path $artifacts "devwt-bind-probe.exe"
$versionRoot = [System.IO.Path]::GetFullPath((Join-Path $artifacts "cross-version-hooks"))
$versionA = Join-Path $versionRoot "version-a"
$versionB = Join-Path $versionRoot "version-b"
$hookA = Join-Path $versionA "devwt-hook.dll"
$hookB = Join-Path $versionB "devwt-hook.dll"
$stdout = Join-Path $artifacts "cross-version-hook.out"
$stderr = Join-Path $artifacts "cross-version-hook.err"

foreach ($path in @($launcher, $watcher, $sourceHook, $probe)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing cross-version hook smoke artifact: $path"
    }
}
if (-not $versionRoot.StartsWith("$artifacts\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Cross-version hook test directory escaped the artifacts root: $versionRoot"
}

Remove-Item -LiteralPath $versionRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $versionA, $versionB -Force | Out-Null
Copy-Item -LiteralPath $sourceHook -Destination $hookA -Force
Copy-Item -LiteralPath $sourceHook -Destination $hookB -Force
Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue

$launcherProcess = Start-Process `
    -FilePath $launcher `
    -ArgumentList @(
        "--children-only",
        "--dll", $hookA,
        "--",
        $probe,
        "--bind-ip", "127.0.0.1",
        "--port", "$Port",
        "--label", "cross-version-hook",
        "--hold-ms", "10000"
    ) `
    -PassThru `
    -NoNewWindow `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr

try {
    $targetPid = 0
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        [string]$out = if (Test-Path -LiteralPath $stdout) { Get-Content -Raw -LiteralPath $stdout } else { "" }
        if ($out -match "BOUND cross-version-hook 127\.0\.0\.1:${Port} pid=(\d+)") {
            $targetPid = [int]$Matches[1]
            break
        }
        if ($launcherProcess.HasExited) {
            break
        }

        Start-Sleep -Milliseconds 100
    }
    if ($targetPid -eq 0) {
        [string]$out = if (Test-Path -LiteralPath $stdout) { Get-Content -Raw -LiteralPath $stdout } else { "" }
        [string]$err = if (Test-Path -LiteralPath $stderr) { Get-Content -Raw -LiteralPath $stderr } else { "" }
        throw "Hooked probe did not start. Out=$out Err=$err"
    }

    $before = @(Get-Process -Id $targetPid -ErrorAction Stop).Modules |
        Where-Object { $_.ModuleName -ieq "devwt-hook.dll" }
    if ($before.Count -ne 1 -or
        -not $before[0].FileName.Equals($hookA, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Probe did not start with exactly the first hook runtime. Modules=$($before.FileName -join ', ')"
    }

    $reinjectionOutput = & $watcher `
        --dll $hookB `
        --children-only-pid $targetPid
    $reinjectionExitCode = $LASTEXITCODE
    Start-Sleep -Milliseconds 250

    $target = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
    if ($null -eq $target) {
        throw "Cross-version reinjection terminated the hooked application. WatcherExit=$reinjectionExitCode Output=$reinjectionOutput"
    }

    $after = @($target.Modules | Where-Object { $_.ModuleName -ieq "devwt-hook.dll" })
    if ($after.Count -ne 1 -or
        -not $after[0].FileName.Equals($hookA, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cross-version reinjection loaded a second hook runtime. WatcherExit=$reinjectionExitCode Output=$reinjectionOutput Modules=$($after.FileName -join ', ')"
    }
    if ($reinjectionExitCode -ne 0) {
        throw "Watcher did not reload the existing hook runtime. Exit=$reinjectionExitCode Output=$reinjectionOutput"
    }

    "cross-version-hook-reinjection-smoke ok pid=$targetPid preserved=$($after[0].FileName)"
}
finally {
    if (-not $launcherProcess.HasExited) {
        Stop-Process -Id $launcherProcess.Id -Force
    }
}
