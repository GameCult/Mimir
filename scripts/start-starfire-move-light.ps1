param(
    [string]$Rgb = "#35ff6c",
    [int]$RefreshMs = 250,
    [double]$HoldSeconds = 0,
    [switch]$TurnOffOnExit,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $repo "artifacts\runtime\starfire-move-light-$runId"
$probe = Join-Path $repo "src\Mimir.PsMoveProbe\bin\Release\net10.0\Mimir.PsMoveProbe.exe"
$eventLog = Join-Path $runDir "move-light-events.jsonl"

New-Item -ItemType Directory -Force -Path $runDir | Out-Null

if (-not (Test-Path $probe)) {
    dotnet build (Join-Path $repo "src\Mimir.PsMoveProbe\Mimir.PsMoveProbe.csproj") -c Release -p:UseSharedCompilation=false
}

$args = @(
    "--rgb", $Rgb,
    "--hold-seconds", "$HoldSeconds",
    "--refresh-ms", "$RefreshMs",
    "--event-log", $eventLog
)
if ($TurnOffOnExit) {
    $args += "--turn-off-on-exit"
}

if ($DryRun) {
    Write-Host "DRY starfire-move-light: $probe $($args -join ' ')"
    return
}

$stdout = Join-Path $runDir "starfire-move-light.out.log"
$stderr = Join-Path $runDir "starfire-move-light.err.log"
$process = Start-Process `
    -FilePath $probe `
    -ArgumentList $args `
    -WorkingDirectory $repo `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -PassThru

Set-Content -Path (Join-Path $runDir "starfire-move-light.pid") -Value $process.Id
[ordered]@{
    kind = "mimir.starfire_move_light_supervisor.v1"
    runId = $runId
    runDir = $runDir
    rgb = $Rgb
    refreshMs = $RefreshMs
    holdSeconds = $HoldSeconds
    pid = $process.Id
    eventLog = $eventLog
    startedAt = (Get-Date).ToString("o")
} | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $runDir "supervisor.json")

Write-Host "starfire-move-light pid=$($process.Id) runDir=$runDir eventLog=$eventLog"
