param(
    [int]$Port = 55286,
    [int]$PortOffset = 24000
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifacts = Join-Path $root "artifacts"
$launcher = Join-Path $artifacts "devwt-hook-launcher.exe"
$hookDll = Join-Path $artifacts "devwt-hook.dll"
$probe = Join-Path $artifacts "devwt-bind-probe.exe"
$bindings = Join-Path $artifacts "port-shift-ipv6-bindings.tsv"
$stdout = Join-Path $artifacts "port-shift-ipv6.out"
$stderr = Join-Path $artifacts "port-shift-ipv6.err"

foreach ($path in @($launcher, $hookDll, $probe)) {
    if (-not (Test-Path $path)) {
        throw "Missing hook artifact: $path"
    }
}

Remove-Item -Force -ErrorAction SilentlyContinue $bindings, $stdout, $stderr

$targetPort = 10000 + (($Port + $PortOffset) % 50000)
if ($targetPort -eq 17776) {
    $targetPort++
}

$process = Start-Process `
    -FilePath $launcher `
    -ArgumentList @(
        "--context-id", "ctx-port-shift-ipv6",
        "--bind-ip", "127.0.0.1",
        "--connect-ip", "127.0.0.1",
        "--port-offset", "$PortOffset",
        "--port-bindings-file", $bindings,
        "--dll", $hookDll,
        "--",
        $probe,
        "--bind-ip", "::1",
        "--port", "$Port",
        "--label", "port-shift-ipv6",
        "--hold-ms", "6000"
    ) `
    -PassThru `
    -NoNewWindow `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr

try {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        $out = if (Test-Path $stdout) { Get-Content -Raw $stdout } else { "" }
        if ($out -match "BOUND port-shift-ipv6 ::1:$Port") {
            break
        }

        Start-Sleep -Milliseconds 100
    }

    $out = if (Test-Path $stdout) { Get-Content -Raw $stdout } else { "" }
    $err = if (Test-Path $stderr) { Get-Content -Raw $stderr } else { "" }
    if ($out -notmatch "BOUND port-shift-ipv6 ::1:$Port") {
        throw "hooked process did not report original IPv6 localhost port. Out=$out Err=$err"
    }

    $bindingText = if (Test-Path $bindings) { Get-Content -Raw $bindings } else { "" }
    if ($bindingText -notmatch "ctx-port-shift-ipv6`t::1`t$Port`t::1`t$targetPort`t\d+`ttcp") {
        throw "binding map did not contain expected shifted IPv6 port. Expected target=$targetPort Bindings=$bindingText"
    }

    $listener = Get-NetTCPConnection -State Listen -LocalAddress ::1 -LocalPort $targetPort -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $listener) {
        throw "shifted TCP listener was not observed on [::1]:$targetPort. Bindings=$bindingText Out=$out Err=$err"
    }

    "port-shift-ipv6-hook-smoke ok original=$Port target=$targetPort"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
