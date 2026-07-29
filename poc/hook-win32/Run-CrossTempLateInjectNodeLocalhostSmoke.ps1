param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55393
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
        throw "Missing cross-temp late-inject artifact: $path"
    }
}

$root = Join-Path $ArtifactsPath "cross-temp-late-inject-node-localhost"
$folder = Join-Path $root "ctx-a"
$watcherTemp = Join-Path $root "watcher-temp"
$childTemp = Join-Path $root "child-temp"
New-Item -ItemType Directory -Path $folder, $watcherTemp, $childTemp -Force | Out-Null

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

$watchLog = Join-Path $ArtifactsPath "cross-temp-late-inject-node-localhost-watcher.log"
$watchOut = Join-Path $ArtifactsPath "cross-temp-late-inject-node-localhost-watcher.out"
$watchErr = Join-Path $ArtifactsPath "cross-temp-late-inject-node-localhost-watcher.err"
$out = Join-Path $ArtifactsPath "cross-temp-late-inject-node-localhost.out"
$err = Join-Path $ArtifactsPath "cross-temp-late-inject-node-localhost.err"
Remove-Item $watchLog, $watchOut, $watchErr, $out, $err, $trigger -ErrorAction SilentlyContinue
Remove-Item (Join-Path $watcherTemp "devwt-hook-poc-*.env"), (Join-Path $childTemp "devwt-hook-poc-*.env") -ErrorAction SilentlyContinue
Remove-Item "C:\ProgramData\DevWT\hook-pids\devwt-hook-poc-*.env" -ErrorAction SilentlyContinue

$blocker = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse("127.0.0.1"), $Port)
$blocker.Start()

function Quote-Arg([string] $value) {
    '"' + ($value -replace '"','\"') + '"'
}

$childStartInfo = [System.Diagnostics.ProcessStartInfo]::new("powershell.exe")
$childStartInfo.WorkingDirectory = $folder
$childStartInfo.UseShellExecute = $false
$childStartInfo.RedirectStandardOutput = $true
$childStartInfo.RedirectStandardError = $true
$childStartInfo.Environment["TEMP"] = $childTemp
$childStartInfo.Environment["TMP"] = $childTemp
$childStartInfo.Arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Quote-Arg $runnerScript)) -join " "

$process = [System.Diagnostics.Process]::Start($childStartInfo)
Start-Sleep -Milliseconds 500

$watcherStartInfo = [System.Diagnostics.ProcessStartInfo]::new($watcher)
$watcherStartInfo.UseShellExecute = $false
$watcherStartInfo.RedirectStandardOutput = $true
$watcherStartInfo.RedirectStandardError = $true
$watcherStartInfo.Environment["TEMP"] = $watcherTemp
$watcherStartInfo.Environment["TMP"] = $watcherTemp
$watcherArgs = @(
    "--dll", $hookDll,
    "--pid", "$($process.Id)",
    "--bind-ip", "127.80.0.77",
    "--connect-ip", "127.80.0.77",
    "--log", $watchLog)
$quotedWatcherArgs = foreach ($argument in $watcherArgs) {
    if ($argument -match '\s') {
        Quote-Arg $argument
    } else {
        $argument
    }
}
$watcherStartInfo.Arguments = $quotedWatcherArgs -join " "

$watcherProcess = [System.Diagnostics.Process]::Start($watcherStartInfo)

try {
    if (-not $watcherProcess.WaitForExit(5000)) {
        throw "cross-temp late-inject watcher timed out"
    }

    Set-Content -Path $watchOut -Encoding ascii -Value $watcherProcess.StandardOutput.ReadToEnd()
    Set-Content -Path $watchErr -Encoding ascii -Value $watcherProcess.StandardError.ReadToEnd()

    New-Item -ItemType File -Path $trigger -Force | Out-Null
    if (-not $process.WaitForExit(10000)) {
        throw "cross-temp late-injected PowerShell node localhost smoke timed out"
    }

    Set-Content -Path $out -Encoding ascii -Value $process.StandardOutput.ReadToEnd()
    Set-Content -Path $err -Encoding ascii -Value $process.StandardError.ReadToEnd()

    $text = Get-Content $out -Raw -ErrorAction SilentlyContinue
    $errorText = Get-Content $err -Raw -ErrorAction SilentlyContinue
    $log = Get-Content $watchLog -Raw -ErrorAction SilentlyContinue
    if ($null -eq $text) { $text = "" }
    if ($null -eq $errorText) { $errorText = "" }
    if ($null -eq $log) { $log = "" }
    $expected = "BOUND 127.0.0.1:$Port"
    if ($text.IndexOf($expected, [StringComparison]::Ordinal) -lt 0) {
        throw "Cross-temp late-injected PowerShell did not pass shared pid config to native node. Out=$text Err=$errorText Log=$log"
    }

    "cross-temp-late-inject-node-localhost-smoke ok"
    "output: $($text -replace '\s+$', '')"
}
finally {
    foreach ($item in @($process, $watcherProcess)) {
        if ($item -and -not $item.HasExited) {
            $item.Kill()
        }
    }
    if ($blocker) {
        $blocker.Stop()
    }
}
