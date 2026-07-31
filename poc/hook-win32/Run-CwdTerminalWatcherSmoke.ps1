param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55292
)

$ErrorActionPreference = "Stop"

$watcher = Join-Path $ArtifactsPath "devwt-folder-watcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-bind-probe.exe"
$spawner = Join-Path $ArtifactsPath "devwt-child-spawner.exe"

foreach ($path in @($watcher, $hookDll, $probe, $spawner)) {
    if (-not (Test-Path $path)) {
        throw "Missing CWD watcher POC artifact: $path"
    }
}

$forceRoot = Join-Path $ArtifactsPath "cwd-terminal-folders"
$folderA = Join-Path $forceRoot "ctx-a"
$folderB = Join-Path $forceRoot "ctx-b"
New-Item -ItemType Directory -Path $folderA, $folderB -Force | Out-Null

$watchLog = Join-Path $ArtifactsPath "cwd-terminal-watcher.log"
$watchOut = Join-Path $ArtifactsPath "cwd-terminal-watcher.out"
$watchErr = Join-Path $ArtifactsPath "cwd-terminal-watcher.err"
$outA = Join-Path $ArtifactsPath "cwd-terminal-a.out"
$outB = Join-Path $ArtifactsPath "cwd-terminal-b.out"
$errA = Join-Path $ArtifactsPath "cwd-terminal-a.err"
$errB = Join-Path $ArtifactsPath "cwd-terminal-b.err"
Remove-Item $watchLog, $watchOut, $watchErr, $outA, $outB, $errA, $errB -ErrorAction SilentlyContinue

$watcherProcess = Start-Process $watcher -ArgumentList @(
    "--dll", $hookDll,
    "--map", "$folderA=127.80.0.70",
    "--map", "$folderB=127.80.0.71",
    "--poll-ms", "20",
    "--duration-ms", "15000",
    "--log", $watchLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watchOut -RedirectStandardError $watchErr

function New-TerminalCommand {
    param(
        [string] $Folder,
        [string] $Label
    )

    $quotedFolder = '"' + $Folder + '"'
    $quotedSpawner = '"' + $spawner + '"'
    $quotedProbe = '"' + $probe + '"'
    "cd /d $quotedFolder && ping -n 3 127.0.0.1 >nul && $quotedSpawner --spawn-delay-ms 1500 -- $quotedProbe --port $Port --label $Label --hold-ms 3000"
}

Start-Sleep -Milliseconds 300
$cmdA = New-TerminalCommand -Folder $folderA -Label "cwd-a"
$cmdB = New-TerminalCommand -Folder $folderB -Label "cwd-b"

$a = Start-Process -FilePath $env:ComSpec -WorkingDirectory $ArtifactsPath -ArgumentList @("/d", "/s", "/c", $cmdA) -PassThru -NoNewWindow -RedirectStandardOutput $outA -RedirectStandardError $errA
Start-Sleep -Milliseconds 500
$b = Start-Process -FilePath $env:ComSpec -WorkingDirectory $ArtifactsPath -ArgumentList @("/d", "/s", "/c", $cmdB) -PassThru -NoNewWindow -RedirectStandardOutput $outB -RedirectStandardError $errB

try {
    if (-not $a.WaitForExit(9000)) {
        throw "cwd terminal A timed out"
    }

    if (-not $b.WaitForExit(9000)) {
        throw "cwd terminal B timed out"
    }

    $aOut = Get-Content $outA -Raw -ErrorAction SilentlyContinue
    $bOut = Get-Content $outB -Raw -ErrorAction SilentlyContinue
    $aErr = Get-Content $errA -Raw -ErrorAction SilentlyContinue
    $bErr = Get-Content $errB -Raw -ErrorAction SilentlyContinue
    $log = Get-Content $watchLog -Raw -ErrorAction SilentlyContinue

    if ([string]::IsNullOrWhiteSpace($aOut) -or $aOut -notmatch "BOUND cwd-a 127\.0\.0\.1:$($Port)") {
        throw "cwd terminal A did not route by current directory. Out=$aOut Err=$aErr Log=$log"
    }

    if ([string]::IsNullOrWhiteSpace($bOut) -or $bOut -notmatch "BOUND cwd-b 127\.0\.0\.1:$($Port)") {
        throw "cwd terminal B did not route by current directory. Out=$bOut Err=$bErr Log=$log"
    }

    "cwd-terminal-watcher-smoke ok"
    "cwd-a: $($aOut -replace '\s+$', '')"
    "cwd-b: $($bOut -replace '\s+$', '')"
}
finally {
    foreach ($process in @($a, $b, $watcherProcess)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}
