param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55323
)

$ErrorActionPreference = "Stop"

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    throw "Node.js is required for this smoke."
}

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"

foreach ($path in @($launcher, $hookDll)) {
    if (-not (Test-Path $path)) {
        throw "Missing node-localhost-bind POC artifact: $path"
    }
}

$script = Join-Path $ArtifactsPath "node-localhost-bind.js"
$stdout = Join-Path $ArtifactsPath "node-localhost-bind.out"
$stderr = Join-Path $ArtifactsPath "node-localhost-bind.err"
Remove-Item $script, $stdout, $stderr -ErrorAction SilentlyContinue

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

$process = Start-Process `
    -FilePath $launcher `
    -ArgumentList @("--bind-ip", "127.80.0.70", "--dll", $hookDll, "--", "node", $script, "$Port") `
    -PassThru `
    -NoNewWindow `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr

if (-not $process.WaitForExit(7000)) {
    $process.Kill()
    throw "node localhost bind smoke timed out"
}

$out = Get-Content $stdout -Raw -ErrorAction SilentlyContinue
$err = Get-Content $stderr -Raw -ErrorAction SilentlyContinue
$expected = "BOUND 127.0.0.1:$Port"
$process.Refresh()
$exitCode = if ($null -eq $process.ExitCode) { 0 } else { $process.ExitCode }
if ($exitCode -ne 0 -or $out.IndexOf($expected, [StringComparison]::Ordinal) -lt 0) {
    throw "localhost bind did not report localhost to the app. Exit=$($process.ExitCode) Out=$out Err=$err"
}

"node-localhost-bind-smoke ok"
"output: $($out -replace '\s+$', '')"
