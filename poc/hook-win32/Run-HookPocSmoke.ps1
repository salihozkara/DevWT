param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55251
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-bind-probe.exe"

foreach ($path in @($launcher, $hookDll, $probe)) {
    if (-not (Test-Path $path)) {
        throw "Missing POC artifact: $path"
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
        [string[]] $ArgumentList
    )

    $stdout = [System.IO.Path]::GetTempFileName()
    $stderr = [System.IO.Path]::GetTempFileName()
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    [pscustomobject]@{
        Process = $process
        StdOut = $stdout
        StdErr = $stderr
    }
}

$rawA = Start-CapturedProcess $probe @("--port", "$Port", "--label", "raw-a", "--hold-ms", "4000")
Start-Sleep -Milliseconds 500
$rawB = Invoke-CapturedProcess $probe @("--port", "$Port", "--label", "raw-b", "--hold-ms", "100")
if ($rawB.ExitCode -eq 0) {
    $rawA.Process.Kill()
    throw "Expected raw duplicate bind to fail, but it succeeded."
}

$rawA.Process.Kill()
$rawA.Process.WaitForExit()

$hookedA = Start-CapturedProcess $launcher @("--bind-ip", "127.80.0.10", "--dll", $hookDll, "--", $probe, "--port", "$Port", "--label", "hook-a", "--hold-ms", "4000")
Start-Sleep -Milliseconds 500
$hookedB = Invoke-CapturedProcess $launcher @("--bind-ip", "127.80.0.11", "--dll", $hookDll, "--", $probe, "--port", "$Port", "--label", "hook-b", "--hold-ms", "100")

$hookedA.Process.Kill()
$hookedA.Process.WaitForExit()
$hookedAOut = Get-Content $hookedA.StdOut -Raw
$hookedAErr = Get-Content $hookedA.StdErr -Raw

if ($hookedB.ExitCode -ne 0) {
    throw "Expected second hooked bind to succeed. Exit=$($hookedB.ExitCode) Out=$($hookedB.Output) Err=$($hookedB.Error)"
}

if ($hookedAOut -notmatch "BOUND hook-a 127\.0\.0\.1:$Port") {
    throw "First hooked bind did not report localhost to the app. Out=$hookedAOut Err=$hookedAErr"
}

if ($hookedB.Output -notmatch "BOUND hook-b 127\.0\.0\.1:$Port") {
    throw "Second hooked bind did not report localhost to the app. Out=$($hookedB.Output) Err=$($hookedB.Error)"
}

"hook-poc-smoke ok"
