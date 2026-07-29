param(
    [int]$MaxBindAttempts = 512,
    [string]$ArtifactsPath
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    Join-Path $root "artifacts"
}
else {
    $ArtifactsPath
}
$launcher = Join-Path $artifacts "devwt-hook-launcher.exe"
$hookDll = Join-Path $artifacts "devwt-hook.dll"
$probe = Join-Path $artifacts "devwt-bind-probe.exe"
$bindings = Join-Path $artifacts "port-shift-address-in-use-bindings.tsv"
$ownerOut = Join-Path $artifacts "port-shift-address-in-use-owner.out"
$ownerErr = Join-Path $artifacts "port-shift-address-in-use-owner.err"
$fallbackOut = Join-Path $artifacts "port-shift-address-in-use-fallback.out"
$fallbackErr = Join-Path $artifacts "port-shift-address-in-use-fallback.err"

foreach ($path in @($launcher, $hookDll, $probe)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing hook artifact: $path"
    }
}

$exclusions = @(
    & netsh interface ipv4 show excludedportrange protocol=tcp |
        ForEach-Object {
            if ($_ -match '^\s*(\d+)\s+(\d+)(?:\s+\*)?\s*$') {
                [pscustomobject]@{
                    Start = [int]$Matches[1]
                    End = [int]$Matches[2]
                }
            }
        }
)
if ($exclusions.Count -eq 0) {
    throw "Windows did not report an excluded IPv4 TCP port range."
}

function Test-IsExcludedPort([int]$Port) {
    return $null -ne ($exclusions |
        Where-Object { $Port -ge $_.Start -and $Port -le $_.End } |
        Select-Object -First 1)
}

function Test-IsListening([int]$Port) {
    return $null -ne (Get-NetTCPConnection `
        -State Listen `
        -LocalAddress 127.0.0.1 `
        -LocalPort $Port `
        -ErrorAction SilentlyContinue |
        Select-Object -First 1)
}

$originalFallbackPort = 15000
$selected = $null
foreach ($range in @($exclusions | Sort-Object Start -Descending)) {
    $occupiedTarget = $range.End + 1
    $expectedTarget = $range.End + 2
    $attemptCount = $range.End - $range.Start + 3
    $originalOwnerPort = $originalFallbackPort + ($occupiedTarget - $range.Start)
    if ($range.Start -lt 10000 -or
        $expectedTarget -gt 59999 -or
        $attemptCount -gt $MaxBindAttempts -or
        $originalOwnerPort -gt 65535 -or
        @($range.Start, $occupiedTarget, $expectedTarget) -contains 17776 -or
        (Test-IsExcludedPort $occupiedTarget) -or
        (Test-IsExcludedPort $expectedTarget) -or
        (Test-IsListening $occupiedTarget) -or
        (Test-IsListening $expectedTarget)) {
        continue
    }

    $selected = [pscustomobject]@{
        Start = $range.Start
        OccupiedTarget = $occupiedTarget
        ExpectedTarget = $expectedTarget
        OriginalOwnerPort = $originalOwnerPort
        ExpectedAttempts = $attemptCount
    }
    break
}
if ($null -eq $selected) {
    throw "No suitable excluded TCP range was available for an address-in-use fallback smoke test."
}

$portOffset = $selected.Start - 10000 - $originalFallbackPort
while ($portOffset -lt 10000) {
    $portOffset += 50000
}
while ($portOffset -gt 59999) {
    $portOffset -= 50000
}

$initialFallbackTarget = 10000 + (($originalFallbackPort + $portOffset) % 50000)
$initialOwnerTarget = 10000 + (($selected.OriginalOwnerPort + $portOffset) % 50000)
if ($initialFallbackTarget -ne $selected.Start -or
    $initialOwnerTarget -ne $selected.OccupiedTarget) {
    throw "Test setup produced unexpected targets fallback=$initialFallbackTarget owner=$initialOwnerTarget."
}

Remove-Item -Force -ErrorAction SilentlyContinue `
    $bindings, $ownerOut, $ownerErr, $fallbackOut, $fallbackErr

$maxAttemptsVariable = "DEVWT_HOOK_BIND_MAX_ATTEMPTS"
$hadPreviousMaxAttempts = Test-Path "Env:$maxAttemptsVariable"
$previousMaxAttempts = [Environment]::GetEnvironmentVariable($maxAttemptsVariable, "Process")
[Environment]::SetEnvironmentVariable($maxAttemptsVariable, "$MaxBindAttempts", "Process")
$owner = $null
$fallback = $null

function Start-HookedProbe {
    param(
        [Parameter(Mandatory)]
        [int]$Port,
        [Parameter(Mandatory)]
        [string]$Label,
        [Parameter(Mandatory)]
        [string]$OutputPath,
        [Parameter(Mandatory)]
        [string]$ErrorPath
    )

    return Start-Process `
        -FilePath $launcher `
        -ArgumentList @(
            "--context-id", "ctx-port-shift-address-in-use",
            "--bind-ip", "127.0.0.1",
            "--connect-ip", "127.0.0.1",
            "--port-offset", "$portOffset",
            "--port-bindings-file", $bindings,
            "--dll", $hookDll,
            "--",
            $probe,
            "--port", "$Port",
            "--label", $Label,
            "--startup-delay-ms", "250",
            "--hold-ms", "10000"
        ) `
        -PassThru `
        -NoNewWindow `
        -RedirectStandardOutput $OutputPath `
        -RedirectStandardError $ErrorPath
}

function Wait-ForText {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Pattern,
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        $content = if (Test-Path -LiteralPath $Path) {
            Get-Content -Raw -LiteralPath $Path
        }
        else {
            ""
        }
        if ($content -match $Pattern -or $Process.HasExited) {
            return $content
        }

        Start-Sleep -Milliseconds 100
    }

    return if (Test-Path -LiteralPath $Path) {
        Get-Content -Raw -LiteralPath $Path
    }
    else {
        ""
    }
}

try {
    $owner = Start-HookedProbe `
        -Port $selected.OriginalOwnerPort `
        -Label "address-in-use-owner" `
        -OutputPath $ownerOut `
        -ErrorPath $ownerErr
    [string]$ownerContent = Wait-ForText `
        -Path $ownerOut `
        -Pattern "BOUND address-in-use-owner 127\.0\.0\.1:$($selected.OriginalOwnerPort)" `
        -Process $owner
    if ($ownerContent -notmatch "BOUND address-in-use-owner 127\.0\.0\.1:$($selected.OriginalOwnerPort)") {
        throw "Collision owner did not start. Out=$ownerContent Err=$(Get-Content -Raw -ErrorAction SilentlyContinue $ownerErr)"
    }

    $fallback = Start-HookedProbe `
        -Port $originalFallbackPort `
        -Label "address-in-use-fallback" `
        -OutputPath $fallbackOut `
        -ErrorPath $fallbackErr
    [string]$fallbackContent = Wait-ForText `
        -Path $fallbackOut `
        -Pattern "BOUND address-in-use-fallback 127\.0\.0\.1:$originalFallbackPort" `
        -Process $fallback
    $fallbackError = if (Test-Path -LiteralPath $fallbackErr) {
        Get-Content -Raw -LiteralPath $fallbackErr
    }
    else {
        ""
    }
    if ($fallbackContent -notmatch "BOUND address-in-use-fallback 127\.0\.0\.1:$originalFallbackPort") {
        throw "Hook did not skip a shifted port owned by a different natural port. Out=$fallbackContent Err=$fallbackError"
    }

    $bindingText = if (Test-Path -LiteralPath $bindings) {
        Get-Content -Raw -LiteralPath $bindings
    }
    else {
        ""
    }
    if ($bindingText -notmatch "ctx-port-shift-address-in-use`t127\.0\.0\.1`t$($selected.OriginalOwnerPort)`t127\.0\.0\.1`t$($selected.OccupiedTarget)`t\d+`ttcp") {
        throw "Binding map did not contain the collision owner. Bindings=$bindingText"
    }
    if ($bindingText -notmatch "ctx-port-shift-address-in-use`t127\.0\.0\.1`t$originalFallbackPort`t127\.0\.0\.1`t$($selected.ExpectedTarget)`t\d+`ttcp") {
        throw "Binding map did not contain the address-in-use fallback target. Bindings=$bindingText"
    }

    foreach ($process in @($fallback, $owner)) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit(5000) | Out-Null
        }
    }
    $fallback = $null
    $owner = $null
    Remove-Item -Force -ErrorAction SilentlyContinue `
        $bindings, $ownerOut, $ownerErr, $fallbackOut, $fallbackErr

    $owner = Start-HookedProbe `
        -Port $selected.OriginalOwnerPort `
        -Label "same-natural-port-owner" `
        -OutputPath $ownerOut `
        -ErrorPath $ownerErr
    [string]$sameOwnerContent = Wait-ForText `
        -Path $ownerOut `
        -Pattern "BOUND same-natural-port-owner 127\.0\.0\.1:$($selected.OriginalOwnerPort)" `
        -Process $owner
    if ($sameOwnerContent -notmatch "BOUND same-natural-port-owner 127\.0\.0\.1:$($selected.OriginalOwnerPort)") {
        throw "Same-natural-port owner did not start. Out=$sameOwnerContent Err=$(Get-Content -Raw -ErrorAction SilentlyContinue $ownerErr)"
    }

    $fallback = Start-HookedProbe `
        -Port $selected.OriginalOwnerPort `
        -Label "same-natural-port-second" `
        -OutputPath $fallbackOut `
        -ErrorPath $fallbackErr
    $null = Wait-ForText `
        -Path $fallbackOut `
        -Pattern "BOUND same-natural-port-second 127\.0\.0\.1:$($selected.OriginalOwnerPort)" `
        -Process $fallback
    if (-not $fallback.WaitForExit(5000)) {
        throw "Second same-natural-port bind unexpectedly remained alive."
    }
    $samePortError = if (Test-Path -LiteralPath $fallbackErr) {
        Get-Content -Raw -LiteralPath $fallbackErr
    }
    else {
        ""
    }
    if ($fallback.ExitCode -eq 0 -or $samePortError -notmatch "bind failed: 10048") {
        throw "Same-natural-port WSAADDRINUSE semantics changed. Exit=$($fallback.ExitCode) Err=$samePortError"
    }

    "port-shift-address-in-use-fallback-smoke ok original=$originalFallbackPort occupied=$($selected.OccupiedTarget) target=$($selected.ExpectedTarget) attempts=$($selected.ExpectedAttempts) same-natural-port=preserved"
}
finally {
    foreach ($process in @($fallback, $owner)) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }
    if ($hadPreviousMaxAttempts) {
        [Environment]::SetEnvironmentVariable($maxAttemptsVariable, $previousMaxAttempts, "Process")
    }
    else {
        [Environment]::SetEnvironmentVariable($maxAttemptsVariable, $null, "Process")
    }
}
