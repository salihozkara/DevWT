param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55353
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
        throw "Missing PowerShell native node localhost artifact: $path"
    }
}

$root = Join-Path $ArtifactsPath "cwd-powershell-native-node-localhost"
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

server.listen(port, 'localhost', () => {
  const address = server.address();
  console.log(`BOUND ${address.address}:${address.port}`);
  setTimeout(() => server.close(() => process.exit(0)), 1000);
});
'@ | Set-Content -Path $serverScript -Encoding ascii

$launcherScript = Join-Path $folder "launch.ps1"
@"
`$ErrorActionPreference = 'Stop'
Start-Sleep -Seconds 4
node .\server.js $Port
"@ | Set-Content -Path $launcherScript -Encoding ascii

$watchLog = Join-Path $ArtifactsPath "cwd-powershell-native-node-localhost-watcher.log"
$watchOut = Join-Path $ArtifactsPath "cwd-powershell-native-node-localhost-watcher.out"
$watchErr = Join-Path $ArtifactsPath "cwd-powershell-native-node-localhost-watcher.err"
$out = Join-Path $ArtifactsPath "cwd-powershell-native-node-localhost.out"
$err = Join-Path $ArtifactsPath "cwd-powershell-native-node-localhost.err"
$blockOut = Join-Path $ArtifactsPath "cwd-powershell-native-node-localhost-blocker.out"
$blockErr = Join-Path $ArtifactsPath "cwd-powershell-native-node-localhost-blocker.err"
Remove-Item $watchLog, $watchOut, $watchErr, $out, $err, $blockOut, $blockErr -ErrorAction SilentlyContinue

$blocker = Start-Process $probe -ArgumentList @("--port", "$Port", "--label", "localhost-blocker", "--hold-ms", "12000") -PassThru -NoNewWindow -RedirectStandardOutput $blockOut -RedirectStandardError $blockErr
Start-Sleep -Milliseconds 600

$watcherProcess = Start-Process $watcher -ArgumentList @(
    "--dll", $hookDll,
    "--map", "$folder=127.80.0.74",
    "--poll-ms", "20",
    "--duration-ms", "2500",
    "--log", $watchLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watchOut -RedirectStandardError $watchErr

Start-Sleep -Milliseconds 300
$process = Start-Process powershell.exe -WorkingDirectory $folder -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $launcherScript) -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err

try {
    if (-not $process.WaitForExit(10000)) {
        throw "PowerShell native node localhost smoke timed out"
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
        throw "PowerShell native node localhost did not inherit hook. Out=$text Err=$errorText Blocker=$blockerText Log=$log"
    }

    "cwd-powershell-native-node-localhost-smoke ok"
    "output: $($text -replace '\s+$', '')"
}
finally {
    foreach ($item in @($process, $watcherProcess, $blocker)) {
        if ($item -and -not $item.HasExited) {
            $item.Kill()
        }
    }
}
