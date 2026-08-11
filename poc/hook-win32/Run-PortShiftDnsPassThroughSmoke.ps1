param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $PortOffset = 24000
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-dns-probe.exe"
$stdout = Join-Path $ArtifactsPath "port-shift-dns.out"
$stderr = Join-Path $ArtifactsPath "port-shift-dns.err"
$stateRoot = Join-Path $ArtifactsPath ("dns-smoke-state-" + [guid]::NewGuid().ToString("N"))
$programData = Join-Path $stateRoot "ProgramData"
$temp = Join-Path $stateRoot "Temp"

foreach ($path in @($launcher, $hookDll, $probe)) {
    if (-not (Test-Path $path)) {
        throw "Missing port-shift DNS POC artifact: $path"
    }
}

Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
$null = New-Item -ItemType Directory -Path $programData, $temp -Force

$originalProgramData = [Environment]::GetEnvironmentVariable("ProgramData", "Process")
$originalTemp = [Environment]::GetEnvironmentVariable("TEMP", "Process")
$originalTmp = [Environment]::GetEnvironmentVariable("TMP", "Process")
$process = $null

try {
    $env:ProgramData = $programData
    $env:TEMP = $temp
    $env:TMP = $temp

    $process = Start-Process `
        -FilePath $launcher `
        -ArgumentList @(
            "--context-id", "ctx-port-shift-dns",
            "--bind-ip", "127.0.0.1",
            "--connect-ip", "127.0.0.1",
            "--port-offset", "$PortOffset",
            "--dll", $hookDll,
            "--",
            $probe
        ) `
        -PassThru `
        -NoNewWindow `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr

    if (-not $process.WaitForExit(5000)) {
        throw "port-shift DNS probe timed out"
    }

    $out = Get-Content $stdout -Raw -ErrorAction SilentlyContinue
    $err = Get-Content $stderr -Raw -ErrorAction SilentlyContinue
    if ($out -notmatch "RESOLVER getaddrinfo owner=ws2_32\.dll" `
        -or $out -notmatch "RESOLVER GetAddrInfoW owner=ws2_32\.dll") {
        throw "port-shift mode intercepted Winsock resolver functions. Out=$out Err=$err"
    }

    "port-shift-dns-pass-through-smoke ok"
    $out.TrimEnd()
}
finally {
    [Environment]::SetEnvironmentVariable("ProgramData", $originalProgramData, "Process")
    [Environment]::SetEnvironmentVariable("TEMP", $originalTemp, "Process")
    [Environment]::SetEnvironmentVariable("TMP", $originalTmp, "Process")

    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
