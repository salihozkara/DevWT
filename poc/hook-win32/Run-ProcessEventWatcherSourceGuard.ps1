param(
    [string] $SourcePath = (Join-Path $PSScriptRoot "src\devwt_folder_watcher.cpp")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SourcePath)) {
    throw "Missing watcher source: $SourcePath"
}

$source = Get-Content -Path $SourcePath -Raw

if ($source -notmatch "DevwtKernelProcessKeyword\s*=\s*0x10") {
    throw "process event watcher must subscribe only to WINEVENT_KEYWORD_PROCESS (0x10)"
}

$callbackMatch = [regex]::Match(
    $source,
    "static VOID WINAPI ProcessEventRecordCallback\(PEVENT_RECORD eventRecord\)\s*\{(?<body>.*?)\n\}",
    [System.Text.RegularExpressions.RegexOptions]::Singleline)

if (-not $callbackMatch.Success) {
    throw "ProcessEventRecordCallback was not found"
}

$callbackBody = $callbackMatch.Groups["body"].Value
if ($callbackBody -match "ScanProcessesOnce") {
    throw "process event callback must not scan every process on each ETW event"
}

if ($callbackBody -notmatch "TryReadProcessStartPid") {
    throw "process event callback must read the created process id from event payload"
}

"process-event-watcher-source-guard ok"
