param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55274
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$project = Join-Path $PSScriptRoot "lab\DotnetFastBind\DotnetFastBind.csproj"
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"

foreach ($path in @($launcher, $hookDll, $project, $dotnet)) {
    if (-not (Test-Path $path)) {
        throw "Missing dotnet-run child smoke artifact: $path"
    }
}

& $dotnet build $project -v:q | Out-Host

$out = Join-Path $ArtifactsPath "dotnet-run-child.out"
$err = Join-Path $ArtifactsPath "dotnet-run-child.err"
Remove-Item $out, $err -ErrorAction SilentlyContinue

$bindIp = "127.80.0.40"
$process = Start-Process `
    -FilePath $launcher `
    -ArgumentList @("--bind-ip", $bindIp, "--dll", $hookDll, "--", $dotnet, "run", "--project", $project, "--no-build", "--", "$Port", "dotnet-run-child", "12000") `
    -PassThru `
    -NoNewWindow `
    -RedirectStandardOutput $out `
    -RedirectStandardError $err

try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $text = Get-Content $out -Raw -ErrorAction SilentlyContinue
        if ($text -match "DOTNET_BOUND dotnet-run-child 127\.0\.0\.1:$Port") {
            break
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $process.HasExited)

    $stdout = Get-Content $out -Raw -ErrorAction SilentlyContinue
    $stderr = Get-Content $err -Raw -ErrorAction SilentlyContinue
    if ($stdout -notmatch "DOTNET_BOUND dotnet-run-child 127\.0\.0\.1:$Port") {
        throw "dotnet run child did not report localhost. Exit=$($process.HasExited) Out=$stdout Err=$stderr"
    }

    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -ne 1) {
        throw "expected one OS listener on port $Port, found $($listeners.Count). Out=$stdout Err=$stderr"
    }

    $listener = $listeners[0]
    if ($listener.LocalAddress -ne $bindIp) {
        throw "dotnet run child was not hooked at OS level. Expected ${bindIp}:$Port, got $($listener.LocalAddress):$($listener.LocalPort) pid=$($listener.OwningProcess). Out=$stdout Err=$stderr"
    }

    "dotnet-run-child-hook-smoke ok"
    "dotnet-run-child: $($stdout -replace '\s+$', '')"
    "os-listener: $($listener.LocalAddress):$($listener.LocalPort) pid=$($listener.OwningProcess)"
}
finally {
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        Stop-Process -Id $listener.OwningProcess -Force -ErrorAction SilentlyContinue
    }

    if ($process -and -not $process.HasExited) {
        $process.Kill()
    }
}
