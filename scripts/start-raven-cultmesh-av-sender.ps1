param(
    [string]$RelayHost = "10.77.0.1",
    [int]$RelayPort = 3075,
    [string]$Stream = "raven-primary-av",
    [string]$FfmpegPath = "ffmpeg",
    [string]$VideoInput = "desktop",
    [ValidateSet("WasapiLoopback", "DirectShow")]
    [string]$AudioMode = "WasapiLoopback",
    [string]$AudioInput = "Stereo Mix (Realtek(R) Audio)",
    [ValidateSet("Console", "Multimedia", "Communications")]
    [string]$LoopbackRole = "Console",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Framerate = 30,
    [string]$VideoBitrate = "8000k",
    [string]$AudioBitrate = "192k",
    [string]$MediaToolPath = ""
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\Mimir.CultMeshMedia\Mimir.CultMeshMedia.csproj"
$publishedTool = Join-Path $PSScriptRoot "..\artifacts\mimir-cultmesh-media-win-x64-selfcontained\Mimir.CultMeshMedia.exe"
$loopbackScript = Join-Path $PSScriptRoot "wasapi-loopback-capture.ps1"

if ([string]::IsNullOrWhiteSpace($MediaToolPath) -and (Test-Path $publishedTool)) {
    $MediaToolPath = $publishedTool
}

$ffmpegArgs = @(
    "-hide_banner",
    "-loglevel", "warning",
    "-f", "gdigrab",
    "-framerate", $Framerate,
    "-video_size", "${Width}x${Height}",
    "-i", $VideoInput
)

if ($AudioMode -eq "WasapiLoopback") {
    $ffmpegArgs += @(
        "-f", "f32le",
        "-ar", "48000",
        "-ac", "2",
        "-i", "pipe:0"
    )
} else {
    $ffmpegArgs += @(
        "-f", "dshow",
        "-i", "audio=$AudioInput"
    )
}

$ffmpegArgs += @(
    "-map", "0:v:0",
    "-map", "1:a:0",
    "-c:v", "h264_nvenc",
    "-preset", "p3",
    "-tune", "ll",
    "-b:v", $VideoBitrate,
    "-maxrate", $VideoBitrate,
    "-bufsize", "16000k",
    "-g", ($Framerate * 2),
    "-pix_fmt", "yuv420p",
    "-c:a", "aac",
    "-b:a", $AudioBitrate,
    "-ar", "48000",
    "-ac", "2",
    "-f", "mpegts",
    "pipe:1"
)

$senderArgs = @("send", "--host", $RelayHost, "--port", $RelayPort, "--stream", $Stream, "--producer", "raven", "--chunk-bytes", "16384", "--slots", "96")

if ($AudioMode -eq "WasapiLoopback") {
    if (-not (Test-Path $loopbackScript)) {
        throw "WASAPI loopback script not found: $loopbackScript"
    }

    if ([string]::IsNullOrWhiteSpace($MediaToolPath)) {
        & $loopbackScript -Output stdout -Role $LoopbackRole | & $FfmpegPath @ffmpegArgs | dotnet run --project $project -- @senderArgs
    } else {
        & $loopbackScript -Output stdout -Role $LoopbackRole | & $FfmpegPath @ffmpegArgs | & $MediaToolPath @senderArgs
    }
} else {
    if ([string]::IsNullOrWhiteSpace($MediaToolPath)) {
        & $FfmpegPath @ffmpegArgs | dotnet run --project $project -- @senderArgs
    } else {
        & $FfmpegPath @ffmpegArgs | & $MediaToolPath @senderArgs
    }
}
