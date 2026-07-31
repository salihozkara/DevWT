param(
    [int]$Port = 55283,
    [int]$PortOffset = 24000
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifacts = Join-Path $root "artifacts"
$launcher = Join-Path $artifacts "devwt-hook-launcher.exe"
$hookDll = Join-Path $artifacts "devwt-hook.dll"
$probe = Join-Path $artifacts "devwt-bind-probe.exe"
$bindings = Join-Path $artifacts "same-context-port-bindings.tsv"
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

foreach ($path in @($launcher, $hookDll, $probe)) {
    if (-not (Test-Path $path)) {
        throw "Missing hook artifact: $path"
    }
}

if ($Port -le 0 -or $Port -gt 65535) {
    throw "Port must be between 1 and 65535."
}

$targetPort = 10000 + (($Port + $PortOffset) % 50000)
if ($targetPort -eq 17776) {
    $targetPort++
}

function Start-HookedProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Label,
        [Parameter(Mandatory)]
        [string]$SocketOption,
        [Parameter(Mandatory)]
        [string]$OutputPath,
        [Parameter(Mandatory)]
        [string]$ErrorPath
    )

    $process = Start-Process `
        -FilePath $launcher `
        -ArgumentList @(
            "--context-id", "ctx-same-context-port",
            "--bind-ip", "127.0.0.1",
            "--connect-ip", "127.0.0.1",
            "--port-offset", "$PortOffset",
            "--port-bindings-file", $bindings,
            "--dll", $hookDll,
            "--",
            $probe,
            "--udp",
            $SocketOption,
            "--port", "$Port",
            "--label", $Label,
            "--startup-delay-ms", "250",
            "--hold-ms", "10000"
        ) `
        -PassThru `
        -NoNewWindow `
        -RedirectStandardOutput $OutputPath `
        -RedirectStandardError $ErrorPath
    $started.Add($process)
    return $process
}

function Wait-ForText {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Pattern
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        $content = if (Test-Path $Path) { Get-Content -Raw $Path } else { "" }
        if ($content -match $Pattern) {
            return $content
        }

        Start-Sleep -Milliseconds 100
    }

    return if (Test-Path $Path) { Get-Content -Raw $Path } else { "" }
}

function Stop-StartedProcesses {
    foreach ($process in $started) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }

    $started.Clear()
}

try {
    $reuseAOut = Join-Path $artifacts "same-context-reuse-a.out"
    $reuseAErr = Join-Path $artifacts "same-context-reuse-a.err"
    $reuseBOut = Join-Path $artifacts "same-context-reuse-b.out"
    $reuseBErr = Join-Path $artifacts "same-context-reuse-b.err"
    Remove-Item -Force -ErrorAction SilentlyContinue $bindings, $reuseAOut, $reuseAErr, $reuseBOut, $reuseBErr

    $reuseA = Start-HookedProbe "reuse-a" "--reuse-address" $reuseAOut $reuseAErr
    $reuseAContent = Wait-ForText $reuseAOut "BOUND reuse-a 127\.0\.0\.1:$Port"
    if ($reuseAContent -notmatch "BOUND reuse-a 127\.0\.0\.1:$Port") {
        throw "First shared bind did not start. Out=$reuseAContent Err=$(Get-Content -Raw -ErrorAction SilentlyContinue $reuseAErr)"
    }

    $reuseB = Start-HookedProbe "reuse-b" "--reuse-address" $reuseBOut $reuseBErr
    $reuseBContent = Wait-ForText $reuseBOut "BOUND reuse-b 127\.0\.0\.1:$Port"
    if ($reuseBContent -notmatch "BOUND reuse-b 127\.0\.0\.1:$Port") {
        throw "Second shared bind did not use the same target port. Out=$reuseBContent Err=$(Get-Content -Raw -ErrorAction SilentlyContinue $reuseBErr)"
    }

    $reuseRows = @(Get-Content $bindings | Where-Object {
        $_ -match "^ctx-same-context-port`t127\.0\.0\.1`t$Port`t127\.0\.0\.1`t$targetPort`t\d+`tudp$"
    })
    if ($reuseRows.Count -ne 2) {
        throw "Expected two successful shared binds on the same target port $targetPort. Bindings=$(Get-Content -Raw $bindings)"
    }

    Stop-StartedProcesses
    Start-Sleep -Milliseconds 250

    $exclusiveAOut = Join-Path $artifacts "same-context-exclusive-a.out"
    $exclusiveAErr = Join-Path $artifacts "same-context-exclusive-a.err"
    $exclusiveBOut = Join-Path $artifacts "same-context-exclusive-b.out"
    $exclusiveBErr = Join-Path $artifacts "same-context-exclusive-b.err"
    Remove-Item -Force -ErrorAction SilentlyContinue $bindings, $exclusiveAOut, $exclusiveAErr, $exclusiveBOut, $exclusiveBErr

    $exclusiveA = Start-HookedProbe "exclusive-a" "--exclusive-address-use" $exclusiveAOut $exclusiveAErr
    $exclusiveAContent = Wait-ForText $exclusiveAOut "BOUND exclusive-a 127\.0\.0\.1:$Port"
    if ($exclusiveAContent -notmatch "BOUND exclusive-a 127\.0\.0\.1:$Port") {
        throw "Exclusive owner did not start. Out=$exclusiveAContent Err=$(Get-Content -Raw -ErrorAction SilentlyContinue $exclusiveAErr)"
    }

    $exclusiveB = Start-HookedProbe "exclusive-b" "--exclusive-address-use" $exclusiveBOut $exclusiveBErr
    if (-not $exclusiveB.WaitForExit(5000)) {
        throw "Second exclusive bind unexpectedly remained alive."
    }

    $exclusiveBError = if (Test-Path $exclusiveBErr) { Get-Content -Raw $exclusiveBErr } else { "" }
    if ($exclusiveB.ExitCode -eq 0 -or $exclusiveBError -notmatch "bind failed:") {
        throw "Second exclusive bind unexpectedly succeeded, which indicates a different target port was used. Exit=$($exclusiveB.ExitCode) Err=$exclusiveBError"
    }

    $exclusiveRows = @(Get-Content $bindings | Where-Object {
        $_ -match "^ctx-same-context-port`t127\.0\.0\.1`t$Port`t127\.0\.0\.1`t$targetPort`t\d+`tudp$"
    })
    if ($exclusiveRows.Count -ne 1) {
        throw "Expected one successful exclusive bind on target port $targetPort. Bindings=$(Get-Content -Raw $bindings)"
    }

    "same-context-port-reuse-hook-smoke ok original=$Port target=$targetPort shared=2 exclusive=blocked"
}
finally {
    Stop-StartedProcesses
}
