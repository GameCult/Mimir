param(
    [string]$TargetHost = "192.168.1.66",
    [int]$Port = 5202,
    [int]$SampleRate = 48000,
    [int]$Channels = 2,
    [string]$FfmpegPath = "ffmpeg",
    [string]$LogRoot = "C:\Meta\Mimir\logs",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null
$repo = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutLog = Join-Path $LogRoot "raven-realtk-loopback-$timestamp.out.log"
$stderrLog = Join-Path $LogRoot "raven-realtk-loopback-$timestamp.err.log"
$endpoint = "srt://${TargetHost}:${Port}?mode=caller&latency=120000"
$captureScript = Join-Path $repo "scripts\wasapi-loopback-capture.ps1"

$command = "& `"$captureScript`" -Output - -SampleRate $SampleRate -Channels $Channels | & `"$FfmpegPath`" -hide_banner -nostdin -loglevel warning -f f32le -ar $SampleRate -ac $Channels -i pipe:0 -f f32le `"$endpoint`""

Write-Host "Raven Realtek loopback sender:"
Write-Host "  target: $endpoint"
Write-Host "  format: f32le ${Channels}ch ${SampleRate}Hz"
Write-Host "  stdout: $stdoutLog"
Write-Host "  stderr: $stderrLog"

if ($DryRun) {
    Write-Host $command
    exit 0
}

Start-Process `
    -FilePath "powershell" `
    -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $command) `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -PassThru |
    ForEach-Object { Write-Host "Started Raven Realtek loopback sender pid=$($_.Id)" }
