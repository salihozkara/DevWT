#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $InstallRoot = "$env:ProgramFiles\DevWT",
    [string] $StateRoot = "$env:ProgramData\DevWT",
    [switch] $KeepInstalledFiles,
    [switch] $DisconnectOnly,
    [switch] $RemoveState,
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

function Remove-DevwtMachinePath([string] $PathToRemove) {
    $normalized = Normalize-DevwtPathForCompare $PathToRemove
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $entries = @($machinePath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $remaining = @()
    foreach ($entry in $entries) {
        $candidate = Normalize-DevwtPathForCompare $entry
        if (-not ($candidate -and $candidate.Equals($normalized, [StringComparison]::OrdinalIgnoreCase))) {
            $remaining += $entry
        }
    }

    [Environment]::SetEnvironmentVariable('Path', ($remaining -join ';'), 'Machine')
}

function Stop-DevwtServiceForUninstall {
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
            Write-Warning "DevWTService did not stop in time; terminating process $($cimService.ProcessId) for uninstall."
            Stop-Process -Id $cimService.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $service = Get-Service -Name 'DevWTService' -ErrorAction SilentlyContinue
    } while ($service -and $service.Status -ne 'Stopped' -and [DateTimeOffset]::UtcNow -lt $deadline)
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
    throw "Stale DevWT hook injector process(es) could not be stopped: $remainingIds. Close them manually and retry uninstall."
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
    throw "DevWT-hooked application process(es) could not be killed: $remainingIds. Close them manually and retry uninstall."
}

function Remove-DevwtDirectoryWithRetry([string] $PathToRemove, [string] $Description) {
    if (-not (Test-Path $PathToRemove)) {
        return $true
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $PathToRemove -Recurse -Force -ErrorAction Stop
            return $true
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds (500 * $attempt)
        }
    }

    Write-Warning "Could not remove $Description at '$PathToRemove'. Running hooked applications may still have DevWT files loaded. Close those applications or reboot, then rerun uninstall. Last error: $($lastError.Exception.Message)"
    return $false
}

$installedCli = Join-Path (Join-Path $InstallRoot 'app') 'Devwt.Cli.exe'
Stop-DevwtServiceForUninstall
if (Test-Path $installedCli) {
    & $installedCli service uninstall --yes
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "DevWT service uninstall failed with exit code $LASTEXITCODE; continuing."
    }
}
else {
    sc.exe delete DevWTService | ForEach-Object { Write-Host $_ }
}

Stop-DevwtHookInjectors
Stop-DevwtHookedApplications -Kill:$KillHookedApplications

$installAppRoot = Join-Path $InstallRoot 'app'
Remove-DevwtMachinePath $installAppRoot

$machineStateRoot = [Environment]::GetEnvironmentVariable('DEVWT_STATE_ROOT', 'Machine')
$configuredStateRoot = Normalize-DevwtPathForCompare $machineStateRoot
$targetStateRoot = Normalize-DevwtPathForCompare $StateRoot
if ($configuredStateRoot -and $targetStateRoot -and $configuredStateRoot.Equals($targetStateRoot, [StringComparison]::OrdinalIgnoreCase)) {
    [Environment]::SetEnvironmentVariable('DEVWT_STATE_ROOT', $null, 'Machine')
    Remove-Item Env:\DEVWT_STATE_ROOT -ErrorAction SilentlyContinue
}

if ($DisconnectOnly) {
    Write-Host "DevWT disconnected. Running applications were not terminated; installed files were left in place."
    return
}

if (-not $KeepInstalledFiles) {
    [void](Remove-DevwtDirectoryWithRetry $InstallRoot "installed files")
}

if ($RemoveState -and (Test-Path $StateRoot)) {
    [void](Remove-DevwtDirectoryWithRetry $StateRoot "state")
}

Write-Host "DevWT uninstalled."
