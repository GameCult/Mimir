param(
    [string]$TargetHost = "192.168.1.66",
    [int]$Port = 5200,
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Framerate = 30,
    [int]$AudioSampleRate = 48000,
    [int]$AudioChannels = 2,
    [ValidateSet("ddagrab", "gdigrab")]
    [string]$VideoCapture = "ddagrab",
    [int]$DdagrabOutputIndex = 0,
    [ValidateSet("srt", "tcp-listener")]
    [string]$Transport = "srt",
    [ValidateSet("caller", "listener")]
    [string]$SrtMode = "caller",
    [string]$FfmpegPath = "ffmpeg",
    [string]$Source = "desktop",
    [string]$VideoBitrate = "12000k",
    [string]$AudioBitrate = "192k",
    [string]$LogRoot = "C:\Meta\Mimir\logs",
    [string]$WasapiLoopbackPath = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Quote-CmdArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null
$repo = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutLog = Join-Path $LogRoot "raven-av-srt-$timestamp.out.log"
$stderrLog = Join-Path $LogRoot "raven-av-srt-$timestamp.err.log"
$endpoint = if ($Transport -eq "tcp-listener") {
    "tcp://${TargetHost}:${Port}?listen=1"
} else {
    "srt://${TargetHost}:${Port}?mode=${SrtMode}&latency=120000&timeout=30000000"
}
$videoSize = "${Width}x${Height}"
$gop = [Math]::Max(1, $Framerate * 2)
$captureScript = Join-Path $repo "scripts\wasapi-loopback-capture.ps1"
$loopbackExe = if ([string]::IsNullOrWhiteSpace($WasapiLoopbackPath)) {
    Join-Path $repo "tools\Mimir.WasapiLoopback\Mimir.WasapiLoopback.exe"
} else {
    $WasapiLoopbackPath
}
$audioCaptureCommand = if (Test-Path $loopbackExe) {
    (Quote-CmdArgument $loopbackExe) +
        " --output stdout --sample-rate $AudioSampleRate --channels $AudioChannels"
} else {
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File " +
        (Quote-CmdArgument $captureScript) +
        " -Output " +
        (Quote-CmdArgument "stdout") +
        " -SampleRate $AudioSampleRate -Channels $AudioChannels"
}

$videoInputArgs = if ($VideoCapture -eq "ddagrab") {
    @(
        "-thread_queue_size", "1024",
        "-f", "lavfi",
        "-i", "ddagrab=framerate=${Framerate}:output_idx=${DdagrabOutputIndex}:draw_mouse=1"
    )
} else {
    @(
        "-thread_queue_size", "1024",
        "-f", "gdigrab",
        "-framerate", $Framerate.ToString(),
        "-video_size", $videoSize,
        "-i", $Source
    )
}

$videoEncodeArgs = @(
    "-c:v", "h264_nvenc",
    "-preset", "p4",
    "-tune", "ll",
    "-b:v", $VideoBitrate,
    "-maxrate", $VideoBitrate,
    "-bufsize", "24000k",
    "-g", $gop.ToString()
)
if ($VideoCapture -eq "gdigrab") {
    $videoEncodeArgs += @("-pix_fmt", "yuv420p")
}

$ffmpegArgs = @(
    "-hide_banner",
    "-loglevel", "warning"
)
$ffmpegArgs += $videoInputArgs
$ffmpegArgs += @(
    "-thread_queue_size", "1024",
    "-f", "f32le",
    "-ar", $AudioSampleRate.ToString(),
    "-ac", $AudioChannels.ToString(),
    "-i", "pipe:0",
    "-map", "0:v:0",
    "-map", "1:a:0"
)
$ffmpegArgs += $videoEncodeArgs
$ffmpegArgs += @(
    "-c:a", "aac",
    "-b:a", $AudioBitrate,
    "-ar", $AudioSampleRate.ToString(),
    "-ac", $AudioChannels.ToString(),
    "-f", "mpegts",
    $endpoint
)
$quotedFfmpegArgs = $ffmpegArgs | ForEach-Object { Quote-CmdArgument $_ }
$command = $audioCaptureCommand +
    " | " +
    (Quote-CmdArgument $FfmpegPath) +
    " " +
    ($quotedFfmpegArgs -join " ")

Write-Host "Raven muxed A/V sender:"
Write-Host "  ffmpeg: $FfmpegPath"
Write-Host "  target: $endpoint"
Write-Host "  transport: $Transport"
Write-Host "  video: $(if ($VideoCapture -eq "ddagrab") { "ddagrab output $DdagrabOutputIndex" } else { "$Source $videoSize" })@$Framerate via h264_nvenc"
Write-Host "  audio: Raven Realtek/default render loopback ${AudioChannels}ch ${AudioSampleRate}Hz f32 -> AAC"
Write-Host "  loopback: $(if (Test-Path $loopbackExe) { $loopbackExe } else { "$captureScript (diagnostic fallback)" })"
Write-Host "  sync role: Raven Realtek is co-streamer game/program loopback packaged with NVENC; Starfire Realtek owns chirp emission"
Write-Host "  stdout: $stdoutLog"
Write-Host "  stderr: $stderrLog"

if ($DryRun) {
    Write-Host $command
    exit 0
}

Start-Process `
    -FilePath "cmd.exe" `
    -ArgumentList ("/d /s /c `"$command`"") `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -PassThru |
    ForEach-Object { Write-Host "Started Raven muxed A/V sender pid=$($_.Id)" }
