[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'salihozkara/DevWT',

    [string]$Version,

    [switch]$DownloadOnly,

    [string]$Destination = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([Net.ServicePointManager]::SecurityProtocol -band [Net.SecurityProtocolType]::Tls12) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
else {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

function Invoke-GitHubApi {
    param([Parameter(Mandatory)][string]$Uri)

    Invoke-RestMethod `
        -Uri $Uri `
        -Headers @{
            Accept = 'application/vnd.github+json'
            'User-Agent' = 'DevWT-Installer'
        } `
        -UseBasicParsing
}

function Get-DevWTRelease {
    $apiRoot = "https://api.github.com/repos/$Repository"
    if ($Version) {
        $encodedVersion = [Uri]::EscapeDataString($Version)
        return Invoke-GitHubApi -Uri "$apiRoot/releases/tags/$encodedVersion"
    }

    $releaseResponse = Invoke-GitHubApi -Uri "$apiRoot/releases?per_page=20"
    # Windows PowerShell 5.1 preserves a JSON array as one pipeline object when
    # it crosses the helper-function boundary. Re-enumerate it explicitly.
    $releases = @($releaseResponse | ForEach-Object { $_ })
    $release = $releases |
        Where-Object { -not $_.draft -and $_.published_at } |
        Sort-Object { [DateTimeOffset]$_.published_at } -Descending |
        Select-Object -First 1

    if (-not $release) {
        throw "No published release was found for $Repository."
    }

    return $release
}

function Get-SingleReleaseAsset {
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][scriptblock]$Predicate,
        [Parameter(Mandatory)][string]$Description
    )

    $matches = @($Release.assets | Where-Object $Predicate)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Description asset, but found $($matches.Count)."
    }

    return $matches[0]
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("devwt-install-" + [Guid]::NewGuid().ToString('N'))
$archivePath = $null

try {
    $release = Get-DevWTRelease
    $archiveAsset = Get-SingleReleaseAsset `
        -Release $release `
        -Description 'installer ZIP' `
        -Predicate { $_.name -match '^DevWT-v.+-installer\.zip$' }
    $checksumName = "$($archiveAsset.name).sha256"
    $checksumAsset = Get-SingleReleaseAsset `
        -Release $release `
        -Description 'SHA-256 checksum' `
        -Predicate { $_.name -eq $checksumName }

    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $archivePath = Join-Path $tempRoot $archiveAsset.name
    $checksumPath = Join-Path $tempRoot $checksumAsset.name

    Write-Host "Downloading DevWT $($release.tag_name)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $archiveAsset.browser_download_url -OutFile $archivePath -UseBasicParsing
    Invoke-WebRequest -Uri $checksumAsset.browser_download_url -OutFile $checksumPath -UseBasicParsing

    $checksumText = Get-Content -LiteralPath $checksumPath -Raw
    $checksumMatch = [Regex]::Match($checksumText, '(?i)\b([a-f0-9]{64})\b')
    if (-not $checksumMatch.Success) {
        throw "The checksum asset does not contain a SHA-256 value."
    }

    $expectedHash = $checksumMatch.Groups[1].Value.ToUpperInvariant()
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Installer checksum verification failed. Expected $expectedHash but received $actualHash."
    }

    Write-Host "SHA-256 verified: $actualHash" -ForegroundColor Green

    if ($DownloadOnly) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        $downloadPath = Join-Path (Resolve-Path -LiteralPath $Destination).Path $archiveAsset.name
        Copy-Item -LiteralPath $archivePath -Destination $downloadPath -Force
        Write-Host "Verified installer saved to $downloadPath" -ForegroundColor Green
        return
    }

    $extractRoot = Join-Path $tempRoot 'package'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
    $installers = @(Get-ChildItem -LiteralPath $extractRoot -Filter 'Install-DevWT.ps1' -File -Recurse)
    if ($installers.Count -ne 1) {
        throw "Expected exactly one Install-DevWT.ps1 in the verified release, but found $($installers.Count)."
    }

    $installerArguments = @(
        '-NoProfile'
        '-ExecutionPolicy'
        'Bypass'
        '-File'
        ('"{0}"' -f $installers[0].FullName)
    )

    Write-Host "Starting the verified DevWT installer..." -ForegroundColor Cyan
    if (Test-IsAdministrator) {
        & powershell.exe @installerArguments
        $exitCode = $LASTEXITCODE
    }
    else {
        $process = Start-Process `
            -FilePath 'powershell.exe' `
            -ArgumentList $installerArguments `
            -Verb RunAs `
            -Wait `
            -PassThru
        $exitCode = $process.ExitCode
    }

    if ($exitCode -ne 0) {
        throw "DevWT installer exited with code $exitCode."
    }

    Write-Host "DevWT $($release.tag_name) installed successfully." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
