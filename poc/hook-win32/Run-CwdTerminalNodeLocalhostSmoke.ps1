param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55333
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
        throw "Missing CWD node localhost artifact: $path"
    }
}

$root = Join-Path $ArtifactsPath "cwd-node-localhost"
$folder = Join-Path $root "ctx-a"
New-Item -ItemType Directory -Path $folder -Force | Out-Null

$script = Join-Path $folder "server.js"
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
'@ | Set-Content -Path $script -Encoding ascii

$watchLog = Join-Path $ArtifactsPath "cwd-node-localhost-watcher.log"
$watchOut = Join-Path $ArtifactsPath "cwd-node-localhost-watcher.out"
$watchErr = Join-Path $ArtifactsPath "cwd-node-localhost-watcher.err"
$out = Join-Path $ArtifactsPath "cwd-node-localhost.out"
$err = Join-Path $ArtifactsPath "cwd-node-localhost.err"
Remove-Item $watchLog, $watchOut, $watchErr, $out, $err -ErrorAction SilentlyContinue

$watcherProcess = Start-Process $watcher -ArgumentList @(
    "--dll", $hookDll,
    "--map", "$folder=127.80.0.72",
    "--poll-ms", "20",
    "--duration-ms", "15000",
    "--log", $watchLog
) -PassThru -NoNewWindow -RedirectStandardOutput $watchOut -RedirectStandardError $watchErr

Start-Sleep -Milliseconds 300
$cmd = "cd /d `"$folder`" && ping -n 3 127.0.0.1 >nul && node server.js $Port"
$process = Start-Process -FilePath $env:ComSpec -WorkingDirectory $ArtifactsPath -ArgumentList @("/d", "/s", "/c", $cmd) -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err

try {
    if (-not $process.WaitForExit(9000)) {
        throw "cwd node localhost smoke timed out"
    }

    $text = Get-Content $out -Raw -ErrorAction SilentlyContinue
    $errorText = Get-Content $err -Raw -ErrorAction SilentlyContinue
    $log = Get-Content $watchLog -Raw -ErrorAction SilentlyContinue
    $expected = "BOUND 127.0.0.1:$Port"
    if ($text.IndexOf($expected, [StringComparison]::Ordinal) -lt 0) {
        throw "cwd node localhost did not report localhost to the app. Out=$text Err=$errorText Log=$log"
    }

    "cwd-terminal-node-localhost-smoke ok"
    "output: $($text -replace '\s+$', '')"
}
finally {
    foreach ($item in @($process, $watcherProcess)) {
        if ($item -and -not $item.HasExited) {
            $item.Kill()
        }
    }
}
