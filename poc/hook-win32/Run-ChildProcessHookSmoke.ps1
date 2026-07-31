param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55291
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-bind-probe.exe"
$spawner = Join-Path $ArtifactsPath "devwt-child-spawner.exe"

foreach ($path in @($launcher, $hookDll, $probe, $spawner)) {
    if (-not (Test-Path $path)) {
        throw "Missing child-hook POC artifact: $path"
    }
}

function Start-CapturedProcess {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList,
        [string] $StdOut,
        [string] $StdErr
    )

    Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -PassThru -NoNewWindow -RedirectStandardOutput $StdOut -RedirectStandardError $StdErr
}

$outA = Join-Path $ArtifactsPath "child-a.out"
$outB = Join-Path $ArtifactsPath "child-b.out"
$errA = Join-Path $ArtifactsPath "child-a.err"
$errB = Join-Path $ArtifactsPath "child-b.err"
Remove-Item $outA, $outB, $errA, $errB -ErrorAction SilentlyContinue

$a = Start-CapturedProcess -FilePath $launcher -ArgumentList @("--bind-ip", "127.80.0.60", "--dll", $hookDll, "--", $spawner, $probe, "--port", "$Port", "--label", "child-a", "--hold-ms", "4000") -StdOut $outA -StdErr $errA
Start-Sleep -Milliseconds 800
$b = Start-CapturedProcess -FilePath $launcher -ArgumentList @("--bind-ip", "127.80.0.61", "--dll", $hookDll, "--", $spawner, $probe, "--port", "$Port", "--label", "child-b", "--hold-ms", "4000") -StdOut $outB -StdErr $errB

try {
    if (-not $a.WaitForExit(7000)) {
        throw "child-a timed out"
    }

    if (-not $b.WaitForExit(7000)) {
        throw "child-b timed out"
    }

    $aOut = Get-Content $outA -Raw -ErrorAction SilentlyContinue
    $bOut = Get-Content $outB -Raw -ErrorAction SilentlyContinue
    $aErr = Get-Content $errA -Raw -ErrorAction SilentlyContinue
    $bErr = Get-Content $errB -Raw -ErrorAction SilentlyContinue

    if ([string]::IsNullOrWhiteSpace($aOut) -or $aOut -notmatch "BOUND child-a 127\.0\.0\.1:$($Port)") {
        throw "child-a did not inherit hook. Exit=$($a.ExitCode) Out=$aOut Err=$aErr"
    }

    if ([string]::IsNullOrWhiteSpace($bOut) -or $bOut -notmatch "BOUND child-b 127\.0\.0\.1:$($Port)") {
        throw "child-b did not inherit hook. Exit=$($b.ExitCode) Out=$bOut Err=$bErr"
    }

    "child-process-hook-smoke ok"
    "child-a: $($aOut -replace '\s+$', '')"
    "child-b: $($bOut -replace '\s+$', '')"
}
finally {
    foreach ($process in @($a, $b)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}
