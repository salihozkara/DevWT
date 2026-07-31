param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [int] $Port = 55334
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $ArtifactsPath "devwt-hook-launcher.exe"
$hookDll = Join-Path $ArtifactsPath "devwt-hook.dll"
$spawner = Join-Path $ArtifactsPath "devwt-child-spawner.exe"
$probe = Join-Path $ArtifactsPath "devwt-env-probe.exe"
$environmentName = "DEVWT_TEST_ENDPOINT"
$environmentValue = "custom://localhost:$Port/runtime-agnostic"

foreach ($path in @($launcher, $hookDll, $spawner, $probe)) {
    if (-not (Test-Path $path)) {
        throw "Missing CreateProcess environment pass-through smoke artifact: $path"
    }
}

function Invoke-PassThroughCase {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string[]] $LauncherArguments
    )

    $out = Join-Path $ArtifactsPath "createprocess-env-$Name.out"
    $err = Join-Path $ArtifactsPath "createprocess-env-$Name.err"
    Remove-Item $out, $err -ErrorAction SilentlyContinue

    $arguments = @($LauncherArguments) + @(
        "--dll", $hookDll,
        "--",
        $spawner,
        "--env", "$environmentName=$environmentValue",
        "--",
        $probe,
        $environmentName
    )
    $process = Start-Process `
        -FilePath $launcher `
        -ArgumentList $arguments `
        -PassThru `
        -NoNewWindow `
        -RedirectStandardOutput $out `
        -RedirectStandardError $err

    $process.WaitForExit(10000) | Out-Null
    if (-not $process.HasExited) {
        $process.Kill()
        throw "CreateProcess environment pass-through smoke '$Name' timed out"
    }

    $stdout = Get-Content $out -Raw -ErrorAction SilentlyContinue
    $stderr = Get-Content $err -Raw -ErrorAction SilentlyContinue
    if ($process.ExitCode -ne 0) {
        throw "CreateProcess environment pass-through smoke '$Name' exited with $($process.ExitCode). Out=$stdout Err=$stderr"
    }

    $expected = "ENV $environmentName=$environmentValue"
    if ($stdout -notmatch [regex]::Escape($expected)) {
        throw "Child environment changed in '$Name'. Expected=$expected Out=$stdout Err=$stderr"
    }

    "$Name`: $($stdout -replace '\s+$', '')"
}

$legacy = Invoke-PassThroughCase `
    -Name "legacy-address" `
    -LauncherArguments @("--children-only", "--bind-ip", "127.80.0.41")
$portShift = Invoke-PassThroughCase `
    -Name "port-shift" `
    -LauncherArguments @("--children-only", "--bind-ip", "127.0.0.1", "--port-offset", "24000")

"createprocess-environment-pass-through-smoke ok"
$legacy
$portShift
