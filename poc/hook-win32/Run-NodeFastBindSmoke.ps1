param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55272
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$script = Join-Path $PSScriptRoot "lab\fast-bind-node.js"

foreach ($path in @($launcher, $hookDll, $script)) {
    if (-not (Test-Path $path)) {
        throw "Missing Node smoke artifact: $path"
    }
}

$out1 = Join-Path $ArtifactsPath "node-a.out"
$out2 = Join-Path $ArtifactsPath "node-b.out"
$err1 = Join-Path $ArtifactsPath "node-a.err"
$err2 = Join-Path $ArtifactsPath "node-b.err"
Remove-Item $out1, $out2, $err1, $err2 -ErrorAction SilentlyContinue

$a = Start-Process $launcher -ArgumentList @("--bind-ip", "127.80.0.20", "--dll", $hookDll, "--", "node", $script, "$Port", "node-a", "8000") -PassThru -NoNewWindow -RedirectStandardOutput $out1 -RedirectStandardError $err1
Start-Sleep -Milliseconds 1000
$b = Start-Process $launcher -ArgumentList @("--bind-ip", "127.80.0.21", "--dll", $hookDll, "--", "node", $script, "$Port", "node-b", "8000") -PassThru -NoNewWindow -RedirectStandardOutput $out2 -RedirectStandardError $err2
Start-Sleep -Milliseconds 1500

try {
    $aOut = Get-Content $out1 -Raw -ErrorAction SilentlyContinue
    $bOut = Get-Content $out2 -Raw -ErrorAction SilentlyContinue
    $aErr = Get-Content $err1 -Raw -ErrorAction SilentlyContinue
    $bErr = Get-Content $err2 -Raw -ErrorAction SilentlyContinue

    if ($a.HasExited -or $b.HasExited) {
        throw "Node process exited early. a=$($a.HasExited) b=$($b.HasExited) aOut=$aOut bOut=$bOut aErr=$aErr bErr=$bErr"
    }

    if ($aOut -notmatch "NODE_BOUND node-a 127\.0\.0\.1:$Port") {
        throw "Node A did not report localhost to the app. Out=$aOut Err=$aErr"
    }

    if ($bOut -notmatch "NODE_BOUND node-b 127\.0\.0\.1:$Port") {
        throw "Node B did not report localhost to the app. Out=$bOut Err=$bErr"
    }

    $r1 = (Invoke-WebRequest -UseBasicParsing "http://127.80.0.20:$($Port)/").Content
    $r2 = (Invoke-WebRequest -UseBasicParsing "http://127.80.0.21:$($Port)/").Content

    if ($r1 -notmatch "node-a 127\.0\.0\.1:$Port") {
        throw "Node A returned unexpected response: $r1"
    }

    if ($r2 -notmatch "node-b 127\.0\.0\.1:$Port") {
        throw "Node B returned unexpected response: $r2"
    }

    "node-fast-bind-smoke ok"
    "node-a: $($r1.Trim())"
    "node-b: $($r2.Trim())"
}
finally {
    if ($a -and -not $a.HasExited) {
        $a.Kill()
    }

    if ($b -and -not $b.HasExited) {
        $b.Kill()
    }
}
