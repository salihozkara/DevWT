#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $InstallRoot = "$env:ProgramFiles\DevWT",
    [string] $StateRoot = "$env:ProgramData\DevWT",
    [switch] $UpdateHookRuntime
)

$ErrorActionPreference = "Stop"
$logPath = Join-Path $StateRoot 'managed-update.log'

function Write-DevwtUpdateLog([string] $Message) {
    $line = '{0:o} {1}' -f [DateTimeOffset]::Now, $Message
    Add-Content -LiteralPath $logPath -Encoding utf8 -Value $line
}

function Wait-DevwtServiceState([string] $ExpectedState, [TimeSpan] $Timeout) {
    $deadline = [DateTimeOffset]::UtcNow.Add($Timeout)
    do {
        $service = Get-Service -Name 'DevWTService' -ErrorAction SilentlyContinue
        if ($service -and $service.Status.ToString().Equals($ExpectedState, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "DevWTService did not reach state $ExpectedState within $($Timeout.TotalSeconds) seconds."
}

function Set-DevwtServiceBinary([string] $PathName) {
    $service = Get-CimInstance Win32_Service -Filter "Name='DevWTService'"
    $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{
        PathName = $PathName
    }
    if ([int]$result.ReturnValue -ne 0) {
        throw "Could not update DevWTService binary path. Win32_Service.Change returned $($result.ReturnValue)."
    }
}

function Stop-DevwtHookInjectors {
    $currentPid = $PID
    $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            [System.IO.Path]::GetFileName($_.Name).Equals(
                'devwt-folder-watcher.exe',
                [StringComparison]::OrdinalIgnoreCase) -and
            $_.ProcessId -ne $currentPid
        })

    foreach ($process in $processes) {
        Write-DevwtUpdateLog "Stopping stale DevWT hook injector $($process.ProcessId) before service switch."
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    do {
        $remaining = @($processes | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
        if ($remaining.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Stale DevWT hook injector process(es) could not be stopped: $(($remaining.ProcessId | Sort-Object) -join ', ')."
}

function Test-DevwtTcpListener([string] $Address, [int] $Port) {
    return $null -ne (Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object {
            $_.LocalPort -eq $Port -and
            $_.LocalAddress.Equals($Address, [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1)
}

function Test-DevwtUdpListener([string] $Address, [int] $Port) {
    return $null -ne (Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
        Where-Object {
            $_.LocalPort -eq $Port -and
            $_.LocalAddress.Equals($Address, [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1)
}

function Wait-DevwtGatewayEndpoints(
    [object[]] $Routes,
    [System.Collections.Generic.HashSet[int]] $BackendProcessIds,
    [TimeSpan] $Timeout) {
    $deadline = [DateTimeOffset]::UtcNow.Add($Timeout)
    do {
        $missing = @()
        foreach ($route in $Routes) {
            $listenerPid = [int]$route.listenerProcessId
            if (-not $BackendProcessIds.Contains($listenerPid) -or
                -not (Get-Process -Id $listenerPid -ErrorAction SilentlyContinue)) {
                continue
            }

            $listenIp = [string]$route.listenIp
            $port = [int]$route.port
            $present = if ([string]$route.protocol -ieq 'Udp') {
                Test-DevwtUdpListener $listenIp $port
            }
            else {
                Test-DevwtTcpListener $listenIp $port
            }
            if (-not $present) {
                $missing += "$($route.protocol) $listenIp`:$port"
            }
        }

        if ($missing.Count -eq 0 -and (Test-DevwtTcpListener '127.0.0.1' 17776)) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Managed update did not restore gateway endpoint(s): $((@($missing) | Sort-Object -Unique) -join ', ')"
}

$bundleRoot = $PSScriptRoot
$appSource = Join-Path $bundleRoot 'app'
$hookSource = Join-Path $appSource 'hook'
$stableAppRoot = Join-Path $InstallRoot 'app'
$installedHookPointer = Join-Path $stableAppRoot 'hook-root.txt'
$routeSnapshotPath = Join-Path $StateRoot 'gateway-routes.json'
New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
Write-DevwtUpdateLog "Managed update started from $bundleRoot."

if (-not (Test-Path -LiteralPath (Join-Path $appSource 'Devwt.Cli.exe'))) {
    throw "Run this script from a complete DevWT installer bundle."
}
if (-not (Test-Path -LiteralPath $installedHookPointer)) {
    throw "The installed DevWT hook pointer is missing: $installedHookPointer"
}

$activeHookRoot = (Get-Content -Raw -LiteralPath $installedHookPointer).Trim()
foreach ($artifact in @('devwt-hook.dll', 'devwt-hook-launcher.exe', 'devwt-folder-watcher.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $activeHookRoot $artifact))) {
        throw "The active hook runtime is incomplete: $activeHookRoot"
    }
}

$hookRoot = $activeHookRoot
if ($UpdateHookRuntime) {
    foreach ($artifact in @('devwt-hook.dll', 'devwt-hook-launcher.exe', 'devwt-folder-watcher.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $hookSource $artifact))) {
            throw "The bundled hook runtime is incomplete: $hookSource"
        }
    }

    $hookVersion = [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmssfff')
    $hookRoot = Join-Path (Join-Path $InstallRoot 'hooks') $hookVersion
    New-Item -ItemType Directory -Path $hookRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $hookSource '*') -Destination $hookRoot -Recurse -Force
    Write-DevwtUpdateLog "Staged hook runtime $hookRoot. Existing hooked applications remain on $activeHookRoot."
}

$service = Get-CimInstance Win32_Service -Filter "Name='DevWTService'" -ErrorAction SilentlyContinue
if (-not $service) {
    throw "DevWTService is not installed. Use Install-DevWT.ps1 for the initial installation."
}
$previousServicePath = [string]$service.PathName

$routes = @()
if (Test-Path -LiteralPath $routeSnapshotPath) {
    $snapshot = Get-Content -Raw -LiteralPath $routeSnapshotPath | ConvertFrom-Json
    $routes = @($snapshot.routes)
}
$backendProcessIds = [System.Collections.Generic.HashSet[int]]::new()
foreach ($route in $routes) {
    $listenerPid = [int]$route.listenerProcessId
    if (Get-Process -Id $listenerPid -ErrorAction SilentlyContinue) {
        [void]$backendProcessIds.Add($listenerPid)
    }
}

$version = [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')
$versionRoot = Join-Path (Join-Path $InstallRoot 'app-versions') $version
New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
Get-ChildItem -LiteralPath $appSource -Force |
    Where-Object { -not $_.Name.Equals('hook', [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $versionRoot -Recurse -Force
    }
Set-Content -LiteralPath (Join-Path $versionRoot 'hook-root.txt') -Encoding ascii -Value $hookRoot
Set-Content -LiteralPath (Join-Path $versionRoot 'devwt.cmd') -Encoding ascii -Value @(
    '@echo off',
    '"%~dp0Devwt.Cli.exe" %*'
)

$newCli = Join-Path $versionRoot 'Devwt.Cli.exe'
$newServicePath = "`"$newCli`" service run"
$serviceSwitched = $false
try {
    Write-DevwtUpdateLog "Stopping DevWTService. Selected hook root $hookRoot."
    Stop-Service -Name 'DevWTService' -Force
    Wait-DevwtServiceState 'Stopped' (New-TimeSpan -Seconds 30)
    Stop-DevwtHookInjectors

    Set-DevwtServiceBinary $newServicePath
    $serviceSwitched = $true
    Write-DevwtUpdateLog "Starting DevWTService from $newServicePath."
    Start-Service -Name 'DevWTService'
    Wait-DevwtServiceState 'Running' (New-TimeSpan -Seconds 30)
    Wait-DevwtGatewayEndpoints $routes $backendProcessIds (New-TimeSpan -Seconds 30)
    if ($UpdateHookRuntime) {
        Set-Content -LiteralPath $installedHookPointer -Encoding ascii -Value $hookRoot
    }
    Write-DevwtUpdateLog "New service restored the Web UI and gateway endpoints."
}
catch {
    $updateError = $_
    Write-DevwtUpdateLog "Managed update failed: $($updateError.Exception.Message)"
    $rollbackError = $null
    try {
        Stop-Service -Name 'DevWTService' -Force -ErrorAction SilentlyContinue
        try {
            Wait-DevwtServiceState 'Stopped' (New-TimeSpan -Seconds 15)
        }
        catch {
        }
        Set-DevwtServiceBinary $previousServicePath
        Start-Service -Name 'DevWTService'
        Wait-DevwtServiceState 'Running' (New-TimeSpan -Seconds 30)
        if ($UpdateHookRuntime) {
            Set-Content -LiteralPath $installedHookPointer -Encoding ascii -Value $activeHookRoot
        }
        Write-DevwtUpdateLog "Previous service path restored: $previousServicePath"
    }
    catch {
        $rollbackError = $_
        Write-DevwtUpdateLog "Rollback failed: $($rollbackError.Exception.Message)"
    }

    if ($rollbackError) {
        throw "Managed DevWT update failed and rollback also failed. Update: $($updateError.Exception.Message) Rollback: $($rollbackError.Exception.Message)"
    }
    throw "Managed DevWT update failed and the previous service path was restored. $($updateError.Exception.Message)"
}

Set-Content -LiteralPath (Join-Path $stableAppRoot 'devwt.cmd') -Encoding ascii -Value @(
    '@echo off',
    "`"$newCli`" %*"
)
Set-Content -LiteralPath (Join-Path $stableAppRoot 'current-managed-app.txt') -Encoding ascii -Value $versionRoot

$extensionSource = Join-Path $bundleRoot 'extension\devwt-browser'
if (Test-Path -LiteralPath $extensionSource) {
    $extensionDestination = Join-Path $InstallRoot 'extension\devwt-browser'
    New-Item -ItemType Directory -Path $extensionDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $extensionSource '*') -Destination $extensionDestination -Recurse -Force
}

if ($UpdateHookRuntime) {
    Write-Host "DevWT managed service and hook runtime updated without terminating hooked applications."
}
else {
    Write-Host "DevWT managed service updated without replacing the active hook runtime."
}
Write-Host "Managed app: $versionRoot"
Write-Host "Selected hook: $hookRoot"
if ($UpdateHookRuntime) {
    Write-Host "Existing applications keep their previously loaded hook until they are restarted."
}
Write-Host "Preserved backend process IDs: $(([int[]]$backendProcessIds | Sort-Object) -join ', ')"
Write-Host "Gateway endpoints and Web UI are listening again."
