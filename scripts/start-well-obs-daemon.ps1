param(
    [string]$WellLog = "",
    [string]$MapName = "Local\MimirObsStemBus",
    [string]$CompositeStemId = "well_composite",
    [int]$PollMs = 25,
    [switch]$FromStart
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($WellLog)) {
    $runtime = Join-Path $repo "artifacts\runtime"
    $latest = Get-ChildItem -Path $runtime -Filter "verse-relay.out.log" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $latest) {
        throw "No verse-relay.out.log found under $runtime. Pass -WellLog explicitly."
    }

    $WellLog = $latest.FullName
}

$runDir = Join-Path $repo ("artifacts\runtime\well-obs-daemon-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$stdout = Join-Path $runDir "well-obs-daemon.out.log"
$stderr = Join-Path $runDir "well-obs-daemon.err.log"
$daemonExe = Join-Path $repo "src\Mimir.WellObsDaemon\bin\Debug\net10.0-windows\Mimir.WellObsDaemon.exe"

if (-not (Test-Path $daemonExe)) {
    dotnet build (Join-Path $repo "src\Mimir.WellObsDaemon\Mimir.WellObsDaemon.csproj")
}

$daemonArgs = @(
    "--well-log",
    $WellLog,
    "--map",
    $MapName,
    "--composite-stem-id",
    $CompositeStemId,
    "--poll-ms",
    "$PollMs"
)

if ($FromStart) {
    $daemonArgs += "--from-start"
}

$process = Start-Process `
    -FilePath $daemonExe `
    -ArgumentList $daemonArgs `
    -WorkingDirectory $repo `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -PassThru

$manifest = [ordered]@{
    document = "mimir.well_obs_daemon.supervisor.v1"
    pid = $process.Id
    wellLog = $WellLog
    mapName = $MapName
    compositeStemId = $CompositeStemId
    stdout = $stdout
    stderr = $stderr
    startedAt = (Get-Date).ToUniversalTime().ToString("O")
}

$manifestPath = Join-Path $runDir "supervisor.json"
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8
Write-Output "Mimir Well OBS daemon PID $($process.Id)"
Write-Output "Supervisor manifest: $manifestPath"
Write-Output "Requires Well capture pages with inline audio bodies enabled."
Write-Output "Poll log: Get-Content -Tail 20 '$stderr'"
