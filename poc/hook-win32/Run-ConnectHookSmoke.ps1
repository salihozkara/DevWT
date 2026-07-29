param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55293
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$server = Join-Path $ArtifactsPath "devwt-bind-probe.exe"
$client = Join-Path $ArtifactsPath "devwt-connect-probe.exe"

foreach ($path in @($launcher, $hookDll, $server, $client)) {
    if (-not (Test-Path $path)) {
        throw "Missing connect-hook POC artifact: $path"
    }
}

$serverOut = Join-Path $ArtifactsPath "connect-server.out"
$serverErr = Join-Path $ArtifactsPath "connect-server.err"
$clientOut = Join-Path $ArtifactsPath "connect-client.out"
$clientErr = Join-Path $ArtifactsPath "connect-client.err"
Remove-Item $serverOut, $serverErr, $clientOut, $clientErr -ErrorAction SilentlyContinue

$serverProcess = Start-Process -FilePath $launcher -ArgumentList @("--bind-ip", "127.80.0.80", "--dll", $hookDll, "--", $server, "--port", "$Port", "--label", "connect-server", "--hold-ms", "5000") -PassThru -NoNewWindow -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr
Start-Sleep -Milliseconds 800
$clientProcess = Start-Process -FilePath $launcher -ArgumentList @("--bind-ip", "127.80.0.81", "--connect-ip", "127.80.0.80", "--dll", $hookDll, "--", $client, "--port", "$Port", "--label", "connect-client") -PassThru -NoNewWindow -RedirectStandardOutput $clientOut -RedirectStandardError $clientErr

try {
    if (-not $clientProcess.WaitForExit(5000)) {
        throw "connect-client timed out"
    }

    $serverText = Get-Content $serverOut -Raw -ErrorAction SilentlyContinue
    $serverError = Get-Content $serverErr -Raw -ErrorAction SilentlyContinue
    $clientText = Get-Content $clientOut -Raw -ErrorAction SilentlyContinue
    $clientError = Get-Content $clientErr -Raw -ErrorAction SilentlyContinue

    if ([string]::IsNullOrWhiteSpace($serverText) -or $serverText -notmatch "BOUND connect-server 127\.0\.0\.1:$($Port)") {
        throw "connect server did not report localhost to the app. Out=$serverText Err=$serverError"
    }

    if ([string]::IsNullOrWhiteSpace($clientText) -or $clientText -notmatch "CONNECTED connect-client 127\.80\.0\.80:$($Port)") {
        throw "connect client did not rewrite localhost target. Exit=$($clientProcess.ExitCode) Out=$clientText Err=$clientError"
    }

    "connect-hook-smoke ok"
    "server: $($serverText -replace '\s+$', '')"
    "client: $($clientText -replace '\s+$', '')"
}
finally {
    foreach ($process in @($clientProcess, $serverProcess)) {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
        }
    }
}
