param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55363
)

$ErrorActionPreference = "Stop"

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    throw "Node.js is required for this smoke."
}

$watcher = Join-Path $ArtifactsPath "devwt-folder-watcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$probe = Join-Path $ArtifactsPath "devwt-bind-probe.exe"

foreach ($path in @($watcher, $hookDll, $probe)) {
    if (-not (Test-Path $path)) {
        throw "Missing late-inject node localhost artifact: $path"
    }
}

$root = Join-Path $ArtifactsPath "existing-powershell-late-inject-node-localhost"
$folder = Join-Path $root "ctx-a"
New-Item -ItemType Directory -Path $folder -Force | Out-Null

$serverScript = Join-Path $folder "server.js"
@'
const http = require('http');
const port = Number(process.argv[2]);
const server = http.createServer((req, res) => {
  res.writeHead(200, { 'content-type': 'text/plain' });
  res.end('ok');
});

server.listen(port, '127.0.0.1', () => {
  const address = server.address();
  console.log(`BOUND ${address.address}:${address.port}`);
  setTimeout(() => server.close(() => process.exit(0)), 1000);
});
'@ | Set-Content -Path $serverScript -Encoding ascii

$trigger = Join-Path $folder "start-node.trigger"
$runnerScript = Join-Path $folder "runner.ps1"
@"
`$ErrorActionPreference = 'Stop'
while (-not (Test-Path '$trigger')) {
    Start-Sleep -Milliseconds 50
}
node .\server.js $Port
"@ | Set-Content -Path $runnerScript -Encoding ascii

$watchLog = Join-Path $ArtifactsPath "existing-powershell-late-inject-node-localhost-watcher.log"
$watchOut = Join-Path $ArtifactsPath "existing-powershell-late-inject-node-localhost-watcher.out"
$watchErr = Join-Path $ArtifactsPath "existing-powershell-late-inject-node-localhost-watcher.err"
$out = Join-Path $ArtifactsPath "existing-powershell-late-inject-node-localhost.out"
$err = Join-Path $ArtifactsPath "existing-powershell-late-inject-node-localhost.err"
$blockOut = Join-Path $ArtifactsPath "existing-powershell-late-inject-node-localhost-blocker.out"
$blockErr = Join-Path $ArtifactsPath "existing-powershell-late-inject-node-localhost-blocker.err"
Remove-Item $watchLog, $watchOut, $watchErr, $out, $err, $blockOut, $blockErr, $trigger -ErrorAction SilentlyContinue

$blocker = Start-Process $probe -ArgumentList @("--port", "$Port", "--label", "localhost-blocker", "--hold-ms", "12000") -PassThru -NoNewWindow -RedirectStandardOutput $blockOut -RedirectStandardError $blockErr
Start-Sleep -Milliseconds 600

$process = Start-Process powershell.exe -WorkingDirectory $folder -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $runnerScript) -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
Start-Sleep -Milliseconds 500

$watcherProcess = Start-Process $watcher -ArgumentList @(
    "--dll", $hookDll,
    "--pid", "$($process.Id)",
    "--bind-ip", "127.80.0.75",
    "--connect-ip", "127.80.0.75",
    "--log", $watchLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watchOut -RedirectStandardError $watchErr

if (-not $watcherProcess.WaitForExit(5000)) {
    $watcherProcess.Kill()
    throw "late-inject watcher timed out"
}

New-Item -ItemType File -Path $trigger -Force | Out-Null

try {
    if (-not $process.WaitForExit(10000)) {
        throw "late-injected PowerShell node localhost smoke timed out"
    }

    $text = Get-Content $out -Raw -ErrorAction SilentlyContinue
    $errorText = Get-Content $err -Raw -ErrorAction SilentlyContinue
    $log = Get-Content $watchLog -Raw -ErrorAction SilentlyContinue
    if ($null -eq $text) { $text = "" }
    if ($null -eq $errorText) { $errorText = "" }
    if ($null -eq $log) { $log = "" }
    $blockerText = Get-Content $blockOut -Raw -ErrorAction SilentlyContinue
    if ($null -eq $blockerText) { $blockerText = "" }
    $expected = "BOUND 127.0.0.1:$Port"
    if ($text.IndexOf($expected, [StringComparison]::Ordinal) -lt 0) {
        throw "Late-injected PowerShell did not pass hook to native node. Out=$text Err=$errorText Blocker=$blockerText Log=$log"
    }

    "existing-powershell-late-inject-node-localhost-smoke ok"
    "output: $($text -replace '\s+$', '')"
}
finally {
    foreach ($item in @($process, $watcherProcess, $blocker)) {
        if ($item -and -not $item.HasExited) {
            $item.Kill()
        }
    }
}
