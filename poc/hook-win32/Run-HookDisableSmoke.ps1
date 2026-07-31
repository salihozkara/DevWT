param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55313
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$server = Join-Path $ArtifactsPath "devwt-bind-probe.exe"
$client = Join-Path $ArtifactsPath "devwt-connect-probe.exe"

foreach ($path in @($launcher, $hookDll, $server, $client)) {
    if (-not (Test-Path $path)) {
        throw "Missing hook-disable POC artifact: $path"
    }
}

$serverOut = Join-Path $ArtifactsPath "disable-server.out"
$serverErr = Join-Path $ArtifactsPath "disable-server.err"
$clientOut = Join-Path $ArtifactsPath "disable-client.out"
$clientErr = Join-Path $ArtifactsPath "disable-client.err"
Remove-Item $serverOut, $serverErr, $clientOut, $clientErr -ErrorAction SilentlyContinue

$serverProcess = Start-Process -FilePath $launcher -ArgumentList @("--bind-ip", "127.80.0.90", "--dll", $hookDll, "--", $server, "--port", "$Port", "--label", "disable-server", "--hold-ms", "5000") -PassThru -NoNewWindow -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr
Start-Sleep -Milliseconds 800

$previousDisable = $env:DEVWT_HOOK_DISABLE
$env:DEVWT_HOOK_DISABLE = "1"
try {
    $clientProcess = Start-Process -FilePath $launcher -ArgumentList @("--bind-ip", "127.80.0.91", "--connect-ip", "127.80.0.90", "--dll", $hookDll, "--", $client, "--port", "$Port", "--label", "disable-client") -PassThru -NoNewWindow -RedirectStandardOutput $clientOut -RedirectStandardError $clientErr
} finally {
    if ($null -eq $previousDisable) {
        Remove-Item Env:\DEVWT_HOOK_DISABLE -ErrorAction SilentlyContinue
    } else {
        $env:DEVWT_HOOK_DISABLE = $previousDisable
    }
}

try {
    if (-not $clientProcess.WaitForExit(5000)) {
        throw "disable-client timed out"
    }

    $serverText = Get-Content $serverOut -Raw -ErrorAction SilentlyContinue
    $serverError = Get-Content $serverErr -Raw -ErrorAction SilentlyContinue
    $clientText = Get-Content $clientOut -Raw -ErrorAction SilentlyContinue
    $clientError = Get-Content $clientErr -Raw -ErrorAction SilentlyContinue

    if ([string]::IsNullOrWhiteSpace($serverText) -or $serverText -notmatch "BOUND disable-server 127\.0\.0\.1:$($Port)") {
        throw "disable server did not report localhost to the app. Out=$serverText Err=$serverError"
    }

    if ($clientProcess.ExitCode -eq 0 -or $clientText -match "CONNECTED disable-client 127\.80\.0\.90:$($Port)") {
        throw "DEVWT_HOOK_DISABLE did not bypass connect rewrite. Exit=$($clientProcess.ExitCode) Out=$clientText Err=$clientError"
    }

    "hook-disable-smoke ok"
    "server: $($serverText -replace '\s+$', '')"
    "client failed without rewrite as expected: $($clientError -replace '\s+$', '')"
}
finally {
    foreach ($process in @($clientProcess, $serverProcess)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}
