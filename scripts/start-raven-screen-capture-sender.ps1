param(
    [string]$TargetHost = "192.168.1.66",
    [int]$Port = 5200,
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Framerate = 30,
    [string]$FfmpegPath = "ffmpeg",
    [string]$Source = "desktop",
    [string]$VideoBitrate = "12000k",
    [string]$LogRoot = "C:\Meta\Mimir\logs",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutLog = Join-Path $LogRoot "raven-screen-srt-$timestamp.out.log"
$stderrLog = Join-Path $LogRoot "raven-screen-srt-$timestamp.err.log"
$endpoint = "srt://${TargetHost}:${Port}?mode=caller&latency=120000"
$videoSize = "${Width}x${Height}"
$gop = [Math]::Max(1, $Framerate * 2)

$arguments = @(
    "-hide_banner",
    "-nostdin",
    "-loglevel", "warning",
    "-f", "gdigrab",
    "-framerate", $Framerate.ToString(),
    "-video_size", $videoSize,
    "-i", $Source,
    "-an",
    "-c:v", "h264_nvenc",
    "-preset", "p4",
    "-tune", "ll",
    "-b:v", $VideoBitrate,
    "-maxrate", $VideoBitrate,
    "-bufsize", "24000k",
    "-pix_fmt", "yuv420p",
    "-g", $gop.ToString(),
    "-f", "mpegts",
    $endpoint
)

Write-Host "Raven screen sender:"
Write-Host "  ffmpeg: $FfmpegPath"
Write-Host "  target: $endpoint"
Write-Host "  source: $Source $videoSize@$Framerate"
Write-Host "  stdout: $stdoutLog"
Write-Host "  stderr: $stderrLog"

if ($DryRun)
{
    Write-Host ($FfmpegPath + " " + ($arguments -join " "))
    exit 0
}

$process = Start-Process `
    -FilePath $FfmpegPath `
    -ArgumentList $arguments `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -PassThru

Write-Host "Started Raven screen sender pid=$($process.Id)"
