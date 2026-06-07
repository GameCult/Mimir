param(
    [string]$TargetHost = "10.77.0.2",
    [int]$Port = 5200,
    [string]$ObsTargetHost = "",
    [int]$ObsPort = 5204,
    [int]$EvePort = 8801,
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Framerate = 30,
    [int]$AudioSampleRate = 48000,
    [int]$AudioChannels = 2,
    [ValidateSet("ddagrab", "gdigrab")]
    [string]$VideoCapture = "ddagrab",
    [int]$DdagrabOutputIndex = 0,
    [string]$AudioDevice = "Realtek",
    [string]$FfmpegPath = "ffmpeg",
    [string]$WasapiLoopbackPath = "",
    [string]$CultMeshCachePath = "C:\Meta\Mimir\state\raven-capture-mux.ccmp",
    [string]$LogRoot = "C:\Meta\Mimir\logs",
    [switch]$NoObsTarget,
    [switch]$NoCultNetServer,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ObsTargetHost)) {
    $ObsTargetHost = $TargetHost
}
$project = Join-Path $repo "src\Mimir.RavenDaemon\Mimir.RavenDaemon.csproj"
$runtimeArgs = @(
    "run",
    "--project", $project,
    "--",
    "--target-host", $TargetHost,
    "--port", $Port,
    "--obs-target-host", $ObsTargetHost,
    "--obs-port", $ObsPort,
    "--eve-port", $EvePort,
    "--width", $Width,
    "--height", $Height,
    "--framerate", $Framerate,
    "--audio-sample-rate", $AudioSampleRate,
    "--audio-channels", $AudioChannels,
    "--video-capture", $VideoCapture,
    "--ddagrab-output-index", $DdagrabOutputIndex,
    "--audio-device", $AudioDevice,
    "--ffmpeg", $FfmpegPath,
    "--cultmesh-cache", $CultMeshCachePath,
    "--log-root", $LogRoot
)

if ($NoObsTarget) {
    $runtimeArgs += "--no-obs-target"
}

if (-not [string]::IsNullOrWhiteSpace($WasapiLoopbackPath)) {
    $runtimeArgs += @("--wasapi-loopback", $WasapiLoopbackPath)
}

if ($NoCultNetServer) {
    $runtimeArgs += "--no-cultnet-server"
}

if ($DryRun) {
    $runtimeArgs += "--dry-run"
    & dotnet @runtimeArgs
    exit $LASTEXITCODE
}

New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $CultMeshCachePath) | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutLog = Join-Path $LogRoot "mimir-raven-daemon-$timestamp.out.log"
$stderrLog = Join-Path $LogRoot "mimir-raven-daemon-$timestamp.err.log"

$process = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList $runtimeArgs `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -PassThru

Write-Host "Started Mimir.RavenDaemon pid=$($process.Id)"
Write-Host "  Eve/CultMesh: ws://0.0.0.0:${EvePort}/eve/deck"
Write-Host "  Health: http://127.0.0.1:${EvePort}/health"
Write-Host "  Mimir target: srt://${TargetHost}:${Port}"
if (-not $NoObsTarget) {
    Write-Host "  OBS target: srt://${ObsTargetHost}:${ObsPort}"
}
Write-Host "  CultMesh cache: $CultMeshCachePath"
Write-Host "  stdout: $stdoutLog"
Write-Host "  stderr: $stderrLog"
