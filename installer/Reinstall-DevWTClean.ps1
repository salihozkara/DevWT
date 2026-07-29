#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $InstallRoot = "$env:ProgramFiles\DevWT",
    [string] $StateRoot = "$env:ProgramData\DevWT",
    [switch] $RemoveState
)

$ErrorActionPreference = "Stop"

function Stop-DevwtRuntimeProcesses {
    $currentPid = $PID
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ProcessId -ne $currentPid -and
                (
                    ($_.ExecutablePath -and
                        [System.IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
                            [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\',
                            [StringComparison]::OrdinalIgnoreCase)) -or
                    $_.Name -ieq 'Devwt.Cli.exe' -or
                    $_.Name -ieq 'devwt-hook-launcher.exe' -or
                    $_.Name -ieq 'devwt-folder-watcher.exe' -or
                    $_.Name -like '*--DevWT-Proxy--*'
                )
            })

        $launchers = @($processes | Where-Object {
            $_.Name -ieq 'devwt-hook-launcher.exe' -or
            $_.Name -like '*--DevWT-Proxy--*'
        })
        $others = @($processes | Where-Object {
            $_.Name -ine 'devwt-hook-launcher.exe' -and
            $_.Name -notlike '*--DevWT-Proxy--*'
        })
        foreach ($process in @($launchers) + @($others)) {
            Write-Host "Stopping DevWT runtime $($process.Name) PID $($process.ProcessId)."
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }

        Start-Sleep -Milliseconds 500
    }
}

$uninstaller = Join-Path $PSScriptRoot "Uninstall-DevWT.ps1"
$installer = Join-Path $PSScriptRoot "Install-DevWT.ps1"
if (-not (Test-Path -LiteralPath $uninstaller) -or -not (Test-Path -LiteralPath $installer)) {
    throw "Run this script from a complete DevWT installer bundle."
}

Stop-DevwtRuntimeProcesses

& $uninstaller `
    -InstallRoot $InstallRoot `
    -StateRoot $StateRoot `
    -KillHookedApplications `
    -RemoveState:$RemoveState
if ($LASTEXITCODE -ne 0) {
    throw "DevWT uninstall failed with exit code $LASTEXITCODE."
}

& $installer `
    -InstallRoot $InstallRoot `
    -StateRoot $StateRoot `
    -KillHookedApplications
if ($LASTEXITCODE -ne 0) {
    throw "DevWT install failed with exit code $LASTEXITCODE."
}

Write-Host "DevWT clean reinstall completed."
if (-not $RemoveState) {
    Write-Host "Existing context and routing state was preserved."
}
