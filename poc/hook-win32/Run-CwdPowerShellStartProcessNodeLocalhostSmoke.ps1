param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55343
)

$ErrorActionPreference = "Stop"

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    throw "Node.js is required for this smoke."
}

$watcher = Join-Path $ArtifactsPath "devwt-folder-watcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"

foreach ($path in @($watcher, $hookDll)) {
    if (-not (Test-Path $path)) {
        throw "Missing PowerShell Start-Process node localhost artifact: $path"
    }
}

$root = Join-Path $ArtifactsPath "cwd-powershell-node-localhost"
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
Start-Sleep -Seconds 2
`$out = Join-Path '$folder' 'node.out'
`$err = Join-Path '$folder' 'node.err'
`$process = Start-Process node -WorkingDirectory '$folder' -ArgumentList @('server.js', '$Port') -PassThru -RedirectStandardOutput `$out -RedirectStandardError `$err
if (-not `$process.WaitForExit(7000)) { `$process.Kill(); throw 'node timed out' }
Get-Content `$out -Raw
Get-Content `$err -Raw -ErrorAction SilentlyContinue
"@ | Set-Content -Path $launcherScript -Encoding ascii

$watchLog = Join-Path $ArtifactsPath "cwd-powershell-node-localhost-watcher.log"
$watchOut = Join-Path $ArtifactsPath "cwd-powershell-node-localhost-watcher.out"
$watchErr = Join-Path $ArtifactsPath "cwd-powershell-node-localhost-watcher.err"
$out = Join-Path $ArtifactsPath "cwd-powershell-node-localhost.out"
$err = Join-Path $ArtifactsPath "cwd-powershell-node-localhost.err"
Remove-Item $watchLog, $watchOut, $watchErr, $out, $err -ErrorAction SilentlyContinue

$watcherProcess = Start-Process $watcher -ArgumentList @(
    "--dll", $hookDll,
    "--map", "$folder=127.80.0.73",
    "--poll-ms", "20",
    "--duration-ms", "15000",
    "--log", $watchLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watchOut -RedirectStandardError $watchErr

Start-Sleep -Milliseconds 300
$process = Start-Process powershell.exe -WorkingDirectory $folder -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $launcherScript) -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err

try {
    if (-not $process.WaitForExit(10000)) {
        throw "PowerShell Start-Process node localhost smoke timed out"
    }

    $text = Get-Content $out -Raw -ErrorAction SilentlyContinue
    $errorText = Get-Content $err -Raw -ErrorAction SilentlyContinue
    $log = Get-Content $watchLog -Raw -ErrorAction SilentlyContinue
    $expected = "BOUND 127.0.0.1:$Port"
    if ($text.IndexOf($expected, [StringComparison]::Ordinal) -lt 0) {
        throw "PowerShell Start-Process node localhost did not report localhost to the app. Out=$text Err=$errorText Log=$log"
    }

    "cwd-powershell-startprocess-node-localhost-smoke ok"
    "output: $($text -replace '\s+$', '')"
}
finally {
    foreach ($item in @($process, $watcherProcess)) {
        if ($item -and -not $item.HasExited) {
            $item.Kill()
        }
    }
}
