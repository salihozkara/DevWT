param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55341
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-bind-probe.exe"
$spawner = Join-Path $ArtifactsPath "devwt-child-spawner.exe"

foreach ($path in @($launcher, $hookDll, $probe, $spawner)) {
    if (-not (Test-Path $path)) {
        throw "Missing children-only POC artifact: $path"
    }
}

function Invoke-CapturedProcess {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList,
        [int] $TimeoutMs = 10000
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.Arguments = Join-CommandLine $ArgumentList
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    if (-not $process.WaitForExit($TimeoutMs)) {
        $process.Kill()
        throw "Process timed out: $FilePath $($ArgumentList -join ' ')"
    }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $process.StandardOutput.ReadToEnd()
        Error = $process.StandardError.ReadToEnd()
    }
}

function Join-CommandLine {
    param([string[]] $Arguments)

    ($Arguments | ForEach-Object {
        if ($_ -notmatch '[\s"]') {
            $_
        } else {
            '"' + ($_ -replace '"', '\"') + '"'
        }
    }) -join ' '
}

function Start-CapturedProcess {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList,
        [string] $StdOut,
        [string] $StdErr,
        [string] $WorkingDirectory = ""
    )

    $parameters = @{
        FilePath = $FilePath
        ArgumentList = $ArgumentList
        PassThru = $true
        NoNewWindow = $true
        RedirectStandardOutput = $StdOut
        RedirectStandardError = $StdErr
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $parameters.WorkingDirectory = $WorkingDirectory
    }

    Start-Process @parameters
}

$rawOut = Join-Path $ArtifactsPath "children-only-raw.out"
$rawErr = Join-Path $ArtifactsPath "children-only-raw.err"
$parentOut = Join-Path $ArtifactsPath "children-only-parent.out"
$parentErr = Join-Path $ArtifactsPath "children-only-parent.err"
$childAOut = Join-Path $ArtifactsPath "children-only-child-a.out"
$childBOut = Join-Path $ArtifactsPath "children-only-child-b.out"
$childAErr = Join-Path $ArtifactsPath "children-only-child-a.err"
$childBErr = Join-Path $ArtifactsPath "children-only-child-b.err"
$mapAOut = Join-Path $ArtifactsPath "children-only-map-a.out"
$mapBOut = Join-Path $ArtifactsPath "children-only-map-b.out"
$mapAErr = Join-Path $ArtifactsPath "children-only-map-a.err"
$mapBErr = Join-Path $ArtifactsPath "children-only-map-b.err"
Remove-Item $rawOut, $rawErr, $parentOut, $parentErr, $childAOut, $childBOut, $childAErr, $childBErr, $mapAOut, $mapBOut, $mapAErr, $mapBErr -ErrorAction SilentlyContinue

$raw = Start-CapturedProcess -FilePath $probe -ArgumentList @("--port", "$Port", "--label", "children-only-raw", "--hold-ms", "4000") -StdOut $rawOut -StdErr $rawErr
Start-Sleep -Milliseconds 500
$parent = Invoke-CapturedProcess -FilePath $launcher -ArgumentList @("--children-only", "--bind-ip", "127.80.0.70", "--dll", $hookDll, "--", $probe, "--port", "$Port", "--label", "children-only-parent", "--hold-ms", "100")

if ($parent.ExitCode -eq 0) {
    if (-not $raw.HasExited) {
        $raw.Kill()
    }

    throw "children-only parent rewrote its own bind; duplicate localhost bind unexpectedly succeeded. Out=$($parent.Output) Err=$($parent.Error)"
}

if (-not $raw.HasExited) {
    $raw.Kill()
    $raw.WaitForExit()
}

$a = Start-CapturedProcess -FilePath $launcher -ArgumentList @("--children-only", "--bind-ip", "127.80.0.71", "--dll", $hookDll, "--", $spawner, $probe, "--port", "$Port", "--label", "children-only-child-a", "--hold-ms", "4000") -StdOut $childAOut -StdErr $childAErr
Start-Sleep -Milliseconds 800
$b = Start-CapturedProcess -FilePath $launcher -ArgumentList @("--children-only", "--bind-ip", "127.80.0.72", "--dll", $hookDll, "--", $spawner, $probe, "--port", "$Port", "--label", "children-only-child-b", "--hold-ms", "4000") -StdOut $childBOut -StdErr $childBErr

try {
    if (-not $a.WaitForExit(7000)) {
        throw "children-only-child-a timed out"
    }

    if (-not $b.WaitForExit(7000)) {
        throw "children-only-child-b timed out"
    }

    $aOut = Get-Content $childAOut -Raw -ErrorAction SilentlyContinue
    $bOut = Get-Content $childBOut -Raw -ErrorAction SilentlyContinue
    $aErr = Get-Content $childAErr -Raw -ErrorAction SilentlyContinue
    $bErr = Get-Content $childBErr -Raw -ErrorAction SilentlyContinue

    if ([string]::IsNullOrWhiteSpace($aOut) -or $aOut -notmatch "BOUND children-only-child-a 127\.0\.0\.1:$($Port)") {
        throw "children-only child A was not hooked. Exit=$($a.ExitCode) Out=$aOut Err=$aErr"
    }

    if ([string]::IsNullOrWhiteSpace($bOut) -or $bOut -notmatch "BOUND children-only-child-b 127\.0\.0\.1:$($Port)") {
        throw "children-only child B was not hooked. Exit=$($b.ExitCode) Out=$bOut Err=$bErr"
    }

    "children-only-hook-smoke ok"
    "parent duplicate failed without rewrite as expected: $($parent.Error -replace '\s+$', '')"
    "child-a: $($aOut -replace '\s+$', '')"
    "child-b: $($bOut -replace '\s+$', '')"
}
finally {
    foreach ($process in @($a, $b, $raw)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}

$mapRootA = Join-Path $ArtifactsPath "children-only-map-a"
$mapRootB = Join-Path $ArtifactsPath "children-only-map-b"
New-Item -ItemType Directory -Path $mapRootA, $mapRootB -Force | Out-Null
$mapFile = Join-Path $ArtifactsPath "children-only-contexts.tsv"
@(
    "$mapRootA`t127.80.0.73`t127.80.0.73",
    "$mapRootB`t127.80.0.74`t127.80.0.74"
) | Set-Content -Path $mapFile -Encoding ascii

$previousMapFile = $env:DEVWT_HOOK_MAP_FILE
$env:DEVWT_HOOK_MAP_FILE = $mapFile
$mapA = $null
$mapB = $null
try {
    $mapA = Start-CapturedProcess -FilePath $launcher -ArgumentList @("--children-only", "--dll", $hookDll, "--", $spawner, $probe, "--port", "$Port", "--label", "children-only-map-a", "--hold-ms", "4000") -StdOut $mapAOut -StdErr $mapAErr -WorkingDirectory $mapRootA
    Start-Sleep -Milliseconds 800
    $mapB = Start-CapturedProcess -FilePath $launcher -ArgumentList @("--children-only", "--dll", $hookDll, "--", $spawner, $probe, "--port", "$Port", "--label", "children-only-map-b", "--hold-ms", "4000") -StdOut $mapBOut -StdErr $mapBErr -WorkingDirectory $mapRootB

    if (-not $mapA.WaitForExit(7000)) {
        throw "children-only-map-a timed out"
    }

    if (-not $mapB.WaitForExit(7000)) {
        throw "children-only-map-b timed out"
    }

    $mapAText = Get-Content $mapAOut -Raw -ErrorAction SilentlyContinue
    $mapBText = Get-Content $mapBOut -Raw -ErrorAction SilentlyContinue
    $mapAError = Get-Content $mapAErr -Raw -ErrorAction SilentlyContinue
    $mapBError = Get-Content $mapBErr -Raw -ErrorAction SilentlyContinue

    if ([string]::IsNullOrWhiteSpace($mapAText) -or $mapAText -notmatch "BOUND children-only-map-a 127\.0\.0\.1:$($Port)") {
        throw "children-only map child A was not hooked. Exit=$($mapA.ExitCode) Out=$mapAText Err=$mapAError"
    }

    if ([string]::IsNullOrWhiteSpace($mapBText) -or $mapBText -notmatch "BOUND children-only-map-b 127\.0\.0\.1:$($Port)") {
        throw "children-only map child B was not hooked. Exit=$($mapB.ExitCode) Out=$mapBText Err=$mapBError"
    }

    "children-only-map-smoke ok"
    "map-a: $($mapAText -replace '\s+$', '')"
    "map-b: $($mapBText -replace '\s+$', '')"
}
finally {
    if ($null -eq $previousMapFile) {
        Remove-Item Env:\DEVWT_HOOK_MAP_FILE -ErrorAction SilentlyContinue
    } else {
        $env:DEVWT_HOOK_MAP_FILE = $previousMapFile
    }

    foreach ($process in @($mapA, $mapB)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}
