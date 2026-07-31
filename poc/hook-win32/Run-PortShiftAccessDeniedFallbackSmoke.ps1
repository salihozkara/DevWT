param(
    [int]$MaxBindAttempts = 512,
    [switch]$ExpectLimitReached,
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
$bindings = Join-Path $artifacts "port-shift-access-denied-bindings.tsv"
$stdout = Join-Path $artifacts "port-shift-access-denied.out"
$stderr = Join-Path $artifacts "port-shift-access-denied.err"

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

$selected = $null
foreach ($range in @($exclusions | Sort-Object Start -Descending)) {
    if ($range.Start -lt 10000 -or $range.End -ge 59999) {
        continue
    }

    $attemptCount = $range.End - $range.Start + 2
    if ($ExpectLimitReached) {
        if (($range.End - $range.Start + 1) -lt $MaxBindAttempts) {
            continue
        }

        $selected = [pscustomobject]@{
            Start = $range.Start
            ExpectedTarget = 0
            ExpectedAttempts = $MaxBindAttempts
        }
        break
    }

    $candidate = $range.End + 1
    if ($attemptCount -gt $MaxBindAttempts -or
        $candidate -eq 17776 -or
        (Test-IsExcludedPort $candidate)) {
        continue
    }

    $listener = Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort $candidate -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $listener) {
        continue
    }

    $selected = [pscustomobject]@{
        Start = $range.Start
        ExpectedTarget = $candidate
        ExpectedAttempts = $attemptCount
    }
    break
}
if ($null -eq $selected) {
    throw "No suitable excluded TCP range was available for a bounded fallback smoke test."
}

$originalPort = 15000
$portOffset = $selected.Start - 10000 - $originalPort
while ($portOffset -lt 10000) {
    $portOffset += 50000
}
while ($portOffset -gt 59999) {
    $portOffset -= 50000
}

$initialTarget = 10000 + (($originalPort + $portOffset) % 50000)
if ($initialTarget -ne $selected.Start) {
    throw "Test setup produced target $initialTarget instead of excluded port $($selected.Start)."
}

Remove-Item -Force -ErrorAction SilentlyContinue $bindings, $stdout, $stderr

$maxAttemptsVariable = "DEVWT_HOOK_BIND_MAX_ATTEMPTS"
$hadPreviousMaxAttempts = Test-Path "Env:$maxAttemptsVariable"
$previousMaxAttempts = [Environment]::GetEnvironmentVariable($maxAttemptsVariable, "Process")
[Environment]::SetEnvironmentVariable($maxAttemptsVariable, "$MaxBindAttempts", "Process")
$process = $null

try {
    $process = Start-Process `
        -FilePath $launcher `
        -ArgumentList @(
            "--context-id", "ctx-port-shift-access-denied",
            "--bind-ip", "127.0.0.1",
            "--connect-ip", "127.0.0.1",
            "--port-offset", "$portOffset",
            "--port-bindings-file", $bindings,
            "--dll", $hookDll,
            "--",
            $probe,
            "--port", "$originalPort",
            "--label", "port-shift-access-denied",
            "--startup-delay-ms", "250",
            "--hold-ms", "6000"
        ) `
        -PassThru `
        -NoNewWindow `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        [string]$out = if (Test-Path -LiteralPath $stdout) { Get-Content -Raw -LiteralPath $stdout } else { "" }
        if ($out -match "BOUND port-shift-access-denied 127\.0\.0\.1:${originalPort}") {
            break
        }
        if ($process.HasExited) {
            break
        }

        Start-Sleep -Milliseconds 100
    }

    [string]$out = if (Test-Path -LiteralPath $stdout) { Get-Content -Raw -LiteralPath $stdout } else { "" }
    [string]$err = if (Test-Path -LiteralPath $stderr) { Get-Content -Raw -LiteralPath $stderr } else { "" }
    if ($ExpectLimitReached) {
        if ($out -match "BOUND port-shift-access-denied 127\.0\.0\.1:${originalPort}") {
            throw "hook exceeded the configured $MaxBindAttempts bind attempts. InitialTarget=$initialTarget Out=$out Err=$err"
        }
        if (-not $process.HasExited) {
            throw "hook did not stop after the configured $MaxBindAttempts bind attempts."
        }
        if ($err -notmatch "bind failed: 10013") {
            throw "hook did not preserve WSAEACCES after reaching the configured attempt limit. Out=$out Err=$err"
        }

        $bindingText = if (Test-Path -LiteralPath $bindings) { Get-Content -Raw -LiteralPath $bindings } else { "" }
        if (-not [string]::IsNullOrWhiteSpace($bindingText)) {
            throw "hook recorded a binding after reaching the configured attempt limit. Bindings=$bindingText"
        }

        "port-shift-access-denied-limit-smoke ok original=$originalPort initial=$initialTarget attempts=$MaxBindAttempts"
        return
    }

    if (-not ($out -match "BOUND port-shift-access-denied 127\.0\.0\.1:${originalPort}")) {
        throw "hook did not retry the excluded shifted port. InitialTarget=$initialTarget ExpectedTarget=$($selected.ExpectedTarget) Out=$out Err=$err"
    }

    $bindingText = if (Test-Path -LiteralPath $bindings) { Get-Content -Raw -LiteralPath $bindings } else { "" }
    $expectedTarget = $selected.ExpectedTarget
    if ($bindingText -notmatch "ctx-port-shift-access-denied`t127\.0\.0\.1`t$originalPort`t127\.0\.0\.1`t$expectedTarget`t\d+`ttcp") {
        throw "binding map did not contain the fallback target. Expected target=$expectedTarget Bindings=$bindingText"
    }

    $listener = Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort $expectedTarget -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $listener) {
        throw "fallback TCP listener was not observed on 127.0.0.1:$expectedTarget. Bindings=$bindingText Out=$out Err=$err"
    }

    "port-shift-access-denied-fallback-smoke ok original=$originalPort initial=$initialTarget target=$expectedTarget attempts=$($selected.ExpectedAttempts)"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    if ($hadPreviousMaxAttempts) {
        [Environment]::SetEnvironmentVariable($maxAttemptsVariable, $previousMaxAttempts, "Process")
    }
    else {
        [Environment]::SetEnvironmentVariable($maxAttemptsVariable, $null, "Process")
    }
}
