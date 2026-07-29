param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55273
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$project = Join-Path $PSScriptRoot "lab\DotnetFastBind\DotnetFastBind.csproj"
$publishPath = Join-Path $ArtifactsPath "dotnet-fast-bind"
$app = Join-Path $publishPath "DotnetFastBind.exe"

foreach ($path in @($launcher, $hookDll, $project)) {
    if (-not (Test-Path $path)) {
        throw "Missing dotnet smoke artifact: $path"
    }
}

dotnet publish $project -c Release -o $publishPath | Out-Host
if (-not (Test-Path $app)) {
    throw "Dotnet apphost was not published: $app"
}

$out1 = Join-Path $ArtifactsPath "dotnet-a.out"
$out2 = Join-Path $ArtifactsPath "dotnet-b.out"
$err1 = Join-Path $ArtifactsPath "dotnet-a.err"
$err2 = Join-Path $ArtifactsPath "dotnet-b.err"
Remove-Item $out1, $out2, $err1, $err2 -ErrorAction SilentlyContinue

$a = Start-Process $launcher -ArgumentList @("--bind-ip", "127.80.0.30", "--dll", $hookDll, "--", $app, "$Port", "dotnet-a", "8000") -PassThru -NoNewWindow -RedirectStandardOutput $out1 -RedirectStandardError $err1
Start-Sleep -Milliseconds 1000
$b = Start-Process $launcher -ArgumentList @("--bind-ip", "127.80.0.31", "--dll", $hookDll, "--", $app, "$Port", "dotnet-b", "8000") -PassThru -NoNewWindow -RedirectStandardOutput $out2 -RedirectStandardError $err2
Start-Sleep -Milliseconds 1500

try {
    $aOut = Get-Content $out1 -Raw -ErrorAction SilentlyContinue
    $bOut = Get-Content $out2 -Raw -ErrorAction SilentlyContinue
    $aErr = Get-Content $err1 -Raw -ErrorAction SilentlyContinue
    $bErr = Get-Content $err2 -Raw -ErrorAction SilentlyContinue

    if ($a.HasExited -or $b.HasExited) {
        throw "Dotnet process exited early. a=$($a.HasExited) b=$($b.HasExited) aOut=$aOut bOut=$bOut aErr=$aErr bErr=$bErr"
    }

    if ($aOut -notmatch "DOTNET_BOUND dotnet-a 127\.0\.0\.1:$Port") {
        throw "Dotnet A did not report localhost to the app. Out=$aOut Err=$aErr"
    }

    if ($bOut -notmatch "DOTNET_BOUND dotnet-b 127\.0\.0\.1:$Port") {
        throw "Dotnet B did not report localhost to the app. Out=$bOut Err=$bErr"
    }

    "dotnet-fast-bind-smoke ok"
    "dotnet-a: $($aOut -replace '\s+$', '')"
    "dotnet-b: $($bOut -replace '\s+$', '')"
}
finally {
    if ($a -and -not $a.HasExited) {
        $a.Kill()
    }

    if ($b -and -not $b.HasExited) {
        $b.Kill()
    }
}
