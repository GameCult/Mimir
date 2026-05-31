param(
    [int]$InputPort = 5200,
    [int]$VideoPort = 5210,
    [int]$AudioPort = 5212,
    [int]$AudioSampleRate = 48000,
    [int]$AudioChannels = 2,
    [string]$FfmpegPath = "ffmpeg",
    [string]$LogRoot = "E:\Projects\Mimir\artifacts\runtime\stream-proof",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutLog = Join-Path $LogRoot "raven-av-demux-$timestamp.out.log"
$stderrLog = Join-Path $LogRoot "raven-av-demux-$timestamp.err.log"
$inputEndpoint = "srt://0.0.0.0:${InputPort}?mode=listener&latency=120000&timeout=5000000"
$videoEndpoint = "srt://127.0.0.1:${VideoPort}?mode=caller&latency=20000"
$audioEndpoint = "srt://127.0.0.1:${AudioPort}?mode=caller&latency=20000"

$arguments = @(
    "-hide_banner",
    "-nostdin",
    "-loglevel", "warning",
    "-fflags", "nobuffer",
    "-flags", "low_delay",
    "-i", $inputEndpoint,
    "-map", "0:v:0",
    "-an",
    "-c:v", "copy",
    "-f", "mpegts",
    $videoEndpoint,
    "-map", "0:a:0",
    "-vn",
    "-acodec", "pcm_f32le",
    "-ar", $AudioSampleRate.ToString(),
    "-ac", $AudioChannels.ToString(),
    "-f", "f32le",
    $audioEndpoint
)

Write-Host "Raven muxed A/V demux:"
Write-Host "  input: $inputEndpoint"
Write-Host "  video: $videoEndpoint"
Write-Host "  audio: $audioEndpoint"
Write-Host "  stdout: $stdoutLog"
Write-Host "  stderr: $stderrLog"

if ($DryRun) {
    Write-Host ($FfmpegPath + " " + ($arguments -join " "))
    exit 0
}

Get-CimInstance Win32_Process |
    Where-Object { $_.ProcessName -like "ffmpeg*" -and $_.CommandLine -match [regex]::Escape($inputEndpoint) } |
    ForEach-Object {
        try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {}
    }

$process = Start-Process `
    -FilePath $FfmpegPath `
    -ArgumentList $arguments `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -PassThru

Write-Host "Started Raven muxed A/V demux pid=$($process.Id)"
