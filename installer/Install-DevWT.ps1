#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $InstallRoot = "$env:ProgramFiles\DevWT",
    [string] $StateRoot = "$env:ProgramData\DevWT",
    [switch] $KillHookedApplications
)

$ErrorActionPreference = "Stop"

function Normalize-DevwtPathForCompare([string] $PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $null
    }

    return [System.IO.Path]::GetFullPath($PathValue).TrimEnd('\')
}

function Test-DevwtPathUnderRoot([string] $PathValue, [string] $RootPath) {
    $normalizedPath = Normalize-DevwtPathForCompare $PathValue
    $normalizedRoot = Normalize-DevwtPathForCompare $RootPath
    if (-not $normalizedPath -or -not $normalizedRoot) {
        return $false
    }

    if ($normalizedPath.Equals($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $normalizedPath.StartsWith("$normalizedRoot\", [StringComparison]::OrdinalIgnoreCase)
}

function Add-DevwtMachinePath([string] $PathToAdd) {
    $normalized = Normalize-DevwtPathForCompare $PathToAdd
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $entries = @($machinePath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($entry in $entries) {
        $candidate = Normalize-DevwtPathForCompare $entry
        if ($candidate -and $candidate.Equals($normalized, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    [Environment]::SetEnvironmentVariable('Path', (($entries + $normalized) -join ';'), 'Machine')
    $env:Path = "$normalized;$env:Path"
}

function Stop-DevwtServiceForUpgrade {
    $service = Get-Service -Name 'DevWTService' -ErrorAction SilentlyContinue
    if (-not $service) {
        return
    }

    if ($service.Status -ne 'Stopped') {
        sc.exe stop DevWTService | ForEach-Object { Write-Host $_ }
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $service = Get-Service -Name 'DevWTService' -ErrorAction SilentlyContinue
    } while ($service -and $service.Status -ne 'Stopped' -and [DateTimeOffset]::UtcNow -lt $deadline)

    if ($service -and $service.Status -ne 'Stopped') {
        $cimService = Get-CimInstance Win32_Service -Filter "Name='DevWTService'" -ErrorAction SilentlyContinue
        if ($cimService -and $cimService.ProcessId -and $cimService.ProcessId -ne 0) {
            Write-Warning "DevWTService did not stop in time; terminating process $($cimService.ProcessId) for upgrade."
            Stop-Process -Id $cimService.ProcessId -Force -ErrorAction SilentlyContinue
        }

        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 250
            $service = Get-Service -Name 'DevWTService' -ErrorAction SilentlyContinue
        } while ($service -and $service.Status -ne 'Stopped' -and [DateTimeOffset]::UtcNow -lt $deadline)
    }

    if ($service -and $service.Status -ne 'Stopped') {
        throw "DevWTService could not be stopped for upgrade. Stop it manually and retry."
    }
}

function Stop-DevwtHookInjectors {
    $injectorNames = @('devwt-folder-watcher.exe')
    $currentPid = $PID
    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $name = [System.IO.Path]::GetFileName($_.Name)
            $injectorNames -contains $name -and $_.ProcessId -ne $currentPid
        }

    foreach ($process in @($processes)) {
        Write-Host "Stopping stale DevWT hook injector $($process.Name) PID $($process.ProcessId)."
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

    $remainingIds = ($remaining | ForEach-Object { $_.ProcessId }) -join ', '
    throw "Stale DevWT hook injector process(es) could not be stopped: $remainingIds. Close them manually and retry install."
}

function Get-DevwtHookedApplications {
    $currentPid = $PID
    $result = @()

    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $currentPid })) {
        $loadedHookPath = $null
        try {
            foreach ($module in @($process.Modules)) {
                if (-not $module.ModuleName.Equals('devwt-hook.dll', [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                if (Test-DevwtPathUnderRoot $module.FileName $InstallRoot) {
                    $loadedHookPath = $module.FileName
                    break
                }
            }
        }
        catch {
            continue
        }

        if ($loadedHookPath) {
            $result += [pscustomobject]@{
                Id = $process.Id
                Name = $process.ProcessName
                HookPath = $loadedHookPath
            }
        }
    }

    return $result
}

function Stop-DevwtHookedApplications([switch] $Kill) {
    $applications = @(Get-DevwtHookedApplications)
    if ($applications.Count -eq 0) {
        return
    }

    if (-not $Kill) {
        Write-Warning "Found applications with DevWT hook DLL loaded. KillHookedApplications was not specified; they were not terminated. Restart them to unload the old DLL, or rerun with -KillHookedApplications."
        foreach ($application in $applications) {
            Write-Warning "Hooked application: $($application.Name) PID $($application.Id) ($($application.HookPath))"
        }

        return
    }

    foreach ($application in $applications) {
        Write-Host "Killing DevWT-hooked application $($application.Name) PID $($application.Id)."
        Stop-Process -Id $application.Id -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $remaining = @($applications | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue })
        if ($remaining.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $remainingIds = ($remaining | ForEach-Object { $_.Id }) -join ', '
    throw "DevWT-hooked application process(es) could not be killed: $remainingIds. Close them manually and retry install."
}

$bundleRoot = $PSScriptRoot
$appSource = Join-Path $bundleRoot 'app'
$hookSource = Join-Path $appSource 'hook'
$installAppRoot = Join-Path $InstallRoot 'app'
$installHookRoot = Join-Path $InstallRoot 'hooks'
$installedCli = Join-Path $installAppRoot 'Devwt.Cli.exe'

if (-not (Test-Path (Join-Path $appSource 'Devwt.Cli.exe'))) {
    throw "Required bundle app is missing: $appSource"
}

if (-not (Test-Path (Join-Path $hookSource 'devwt-hook.dll')) -or
    -not (Test-Path (Join-Path $hookSource 'devwt-hook-launcher.exe')) -or
    -not (Test-Path (Join-Path $hookSource 'devwt-folder-watcher.exe'))) {
    throw "Required hook runtime artifacts are missing under: $hookSource"
}

Stop-DevwtServiceForUpgrade
Stop-DevwtHookInjectors
Stop-DevwtHookedApplications -Kill:$KillHookedApplications

New-Item -ItemType Directory -Path $installAppRoot -Force | Out-Null
New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null

Get-ChildItem -LiteralPath $appSource -Force |
    Where-Object { -not $_.Name.Equals('hook', [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $installAppRoot -Recurse -Force
    }

$extensionSource = Join-Path $bundleRoot 'extension\devwt-browser'
if (Test-Path -LiteralPath $extensionSource) {
    $extensionDestination = Join-Path $InstallRoot 'extension\devwt-browser'
    New-Item -ItemType Directory -Path $extensionDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $extensionSource '*') -Destination $extensionDestination -Recurse -Force
}

$hookVersion = [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')
$installHookVersionRoot = Join-Path $installHookRoot $hookVersion
New-Item -ItemType Directory -Path $installHookVersionRoot -Force | Out-Null
Copy-Item -Path (Join-Path $hookSource '*') -Destination $installHookVersionRoot -Recurse -Force
Set-Content -LiteralPath (Join-Path $installAppRoot 'hook-root.txt') -Encoding ascii -Value $installHookVersionRoot

Set-Content -LiteralPath (Join-Path $installAppRoot 'devwt.cmd') -Encoding ascii -Value @(
    '@echo off',
    '"%~dp0Devwt.Cli.exe" %*'
)

[Environment]::SetEnvironmentVariable('DEVWT_STATE_ROOT', $StateRoot, 'Machine')
$env:DEVWT_STATE_ROOT = $StateRoot
Add-DevwtMachinePath $installAppRoot

& $installedCli service install --yes
if ($LASTEXITCODE -ne 0) {
    throw "DevWT service install failed with exit code $LASTEXITCODE."
}

Write-Host "DevWT installed."
Write-Host "CLI: $installedCli"
Write-Host "State: $StateRoot"
Write-Host "Web UI: http://127.0.0.1:17776/"
