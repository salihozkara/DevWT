param(
    [int]$Port = 55283,
    [int]$PortOffset = 24000,
    [string]$BindIp = "127.0.0.2"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifacts = Join-Path $root "artifacts"
$launcher = Join-Path $artifacts "devwt-hook-launcher.exe"
$hookDll = Join-Path $artifacts "devwt-hook.dll"
$probe = Join-Path $artifacts "devwt-bind-probe.exe"
$bindings = Join-Path $artifacts "port-shift-bind-ip-bindings.tsv"
$stdout = Join-Path $artifacts "port-shift-bind-ip.out"
$stderr = Join-Path $artifacts "port-shift-bind-ip.err"

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

$escapedBindIp = [regex]::Escape($BindIp)
$process = Start-Process `
    -FilePath $launcher `
    -ArgumentList @(
        "--context-id", "ctx-port-shift-bind-ip",
        "--bind-ip", "127.0.0.1",
        "--connect-ip", "127.0.0.1",
        "--port-offset", "$PortOffset",
        "--port-bindings-file", $bindings,
        "--dll", $hookDll,
        "--",
        $probe,
        "--bind-ip", $BindIp,
        "--port", "$Port",
        "--label", "port-shift-bind-ip",
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
        if ($out -match "BOUND port-shift-bind-ip $escapedBindIp`:$Port") {
            break
        }

        Start-Sleep -Milliseconds 100
    }

    $out = if (Test-Path $stdout) { Get-Content -Raw $stdout } else { "" }
    $err = if (Test-Path $stderr) { Get-Content -Raw $stderr } else { "" }
    if ($out -notmatch "BOUND port-shift-bind-ip $escapedBindIp`:$Port") {
        throw "hooked process did not report original bind endpoint. Out=$out Err=$err"
    }

    $bindingText = if (Test-Path $bindings) { Get-Content -Raw $bindings } else { "" }
    if ($bindingText -notmatch "ctx-port-shift-bind-ip`t$escapedBindIp`t$Port`t$escapedBindIp`t$targetPort`t\d+`ttcp") {
        throw "binding map did not preserve bind IP. Expected ${BindIp}:$Port -> ${BindIp}:$targetPort Bindings=$bindingText"
    }

    $listener = Get-NetTCPConnection -State Listen -LocalAddress $BindIp -LocalPort $targetPort -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $listener) {
        throw "shifted TCP listener was not observed on ${BindIp}:$targetPort. Bindings=$bindingText Out=$out Err=$err"
    }

    "port-shift-bind-ip-hook-smoke ok ip=$BindIp original=$Port target=$targetPort"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
