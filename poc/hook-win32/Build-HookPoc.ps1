param(
    [string] $Configuration = "Release",
    [string] $ArtifactsPath,
    [switch] $RuntimeOnly
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$src = Join-Path $root "src"
$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    Join-Path $root "artifacts"
} else {
    $ArtifactsPath
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Install Visual Studio Build Tools with C++ workload."
}

$vcvars = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find "VC\Auxiliary\Build\vcvars64.bat" | Select-Object -First 1
if (-not $vcvars) {
    throw "vcvars64.bat was not found. Install Visual Studio C++ build tools."
}

$common = "/nologo /std:c++17 /EHsc /W4 /WX /DUNICODE /D_UNICODE"
if ($Configuration -ieq "Debug") {
    $common = "$common /Zi /Od /MDd"
} else {
    $common = "$common /O2 /MD"
}

$commands = @(
    "cl $common /Fo:`"$artifacts\\`" /Fe:`"$artifacts\devwt-hook-launcher.exe`" `"$src\devwt_hook_launcher.cpp`"",
    "cl $common /Fo:`"$artifacts\\`" /Fe:`"$artifacts\devwt-folder-watcher.exe`" `"$src\devwt_folder_watcher.cpp`" advapi32.lib tdh.lib psapi.lib",
    "cl $common /Fo:`"$artifacts\\`" /LD /Fe:`"$artifacts\devwt-hook.dll`" `"$src\devwt_hook.cpp`" ws2_32.lib"
)

if (-not $RuntimeOnly) {
    $commands += @(
        "cl $common /Fo:`"$artifacts\\`" /Fe:`"$artifacts\devwt-child-spawner.exe`" `"$src\devwt_child_spawner.cpp`"",
        "cl $common /Fo:`"$artifacts\\`" /Fe:`"$artifacts\devwt-env-probe.exe`" `"$src\devwt_env_probe.cpp`"",
        "cl $common /Fo:`"$artifacts\\`" /Fe:`"$artifacts\devwt-bind-probe.exe`" `"$src\devwt_bind_probe.cpp`" ws2_32.lib",
        "cl $common /Fo:`"$artifacts\\`" /Fe:`"$artifacts\devwt-connect-probe.exe`" `"$src\devwt_connect_probe.cpp`" ws2_32.lib"
    )
}

$batch = Join-Path $artifacts "build-hook-poc.cmd"
@(
    "@echo off",
    "call `"$vcvars`"",
    ($commands -join " && ")
) | Set-Content -Path $batch -Encoding ascii

cmd.exe /d /c "`"$batch`""
if ($LASTEXITCODE -ne 0) {
    throw "Native POC build failed with exit code $LASTEXITCODE"
}

"built hook POC artifacts in $artifacts"
