[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$BundlePath
)

$ErrorActionPreference = 'Stop'
$resolvedBundle = (Resolve-Path -LiteralPath $BundlePath).Path

if ([IO.Path]::GetExtension($resolvedBundle) -ne '.zip') {
    throw "Release bundle must be a ZIP file: $resolvedBundle"
}

$platformRoot = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
$defenderCommand = Get-ChildItem -LiteralPath $platformRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if (-not $defenderCommand) {
    $fallbackCommand = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    if (Test-Path -LiteralPath $fallbackCommand -PathType Leaf) {
        $defenderCommand = $fallbackCommand
    }
}

if (-not $defenderCommand) {
    throw 'Microsoft Defender command-line scanner was not found. Run this release gate on a Windows host with Defender available.'
}

Write-Host "Scanning release bundle without remediation: $resolvedBundle" -ForegroundColor Cyan
& $defenderCommand `
    -Scan `
    -ScanType 3 `
    -File $resolvedBundle `
    -DisableRemediation `
    -ReturnHR

$scanExitCode = $LASTEXITCODE
if ($scanExitCode -ne 0) {
    throw "Microsoft Defender rejected the release bundle (exit code $scanExitCode). Do not publish this artifact."
}

$hash = (Get-FileHash -LiteralPath $resolvedBundle -Algorithm SHA256).Hash
Write-Host "Microsoft Defender scan passed." -ForegroundColor Green
Write-Host "SHA-256: $hash"
