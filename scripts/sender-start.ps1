param(
    [string]$Config = ".\config\localcast.json",
    [string]$FfmpegPath = "ffmpeg",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$LogRoot = Join-Path $Root "logs"

function Resolve-ConfigPath {
    param([string]$Path)
    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    return $resolved.Path
}

function Assert-Value {
    param(
        [object]$Value,
        [string]$Message
    )
    if ($null -eq $Value -or "$Value".Trim().Length -eq 0) {
        throw $Message
    }
}

function Get-SrtUrl {
    param(
        [object]$Receiver,
        [int]$PortOffset
    )

    $port = [int]$Receiver.basePort + $PortOffset
    $query = "mode=caller&latency=$($Receiver.srtLatencyMicros)"
    if ($Receiver.passphrase -and "$($Receiver.passphrase)".Length -gt 0) {
        $escaped = [uri]::EscapeDataString("$($Receiver.passphrase)")
        $query = "$query&passphrase=$escaped&pbkeylen=16"
    }
    return "srt://$($Receiver.host):$($port)?$query"
}

function Format-CommandLine {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )
    $parts = @($Executable)
    foreach ($arg in $Arguments) {
        if ($arg -match '[\s"&?=]') {
            $escaped = $arg.Replace('"', '\"')
            $parts += '"' + $escaped + '"'
        } else {
            $parts += $arg
        }
    }
    return ($parts -join " ")
}

function Format-ArgumentList {
    param([string[]]$Arguments)
    $parts = @()
    foreach ($arg in $Arguments) {
        if ($arg -match '[\s"&?=]') {
            $escaped = $arg.Replace('"', '\"')
            $parts += '"' + $escaped + '"'
        } else {
            $parts += $arg
        }
    }
    return ($parts -join " ")
}

function Start-SourceProcess {
    param(
        [string]$Name,
        [string[]]$Arguments
    )

    $line = Format-CommandLine -Executable $FfmpegPath -Arguments $Arguments
    if ($DryRun) {
        Write-Host ""
        Write-Host "[$Name]"
        Write-Host $line
        return
    }

    New-Item -ItemType Directory -Force $LogRoot | Out-Null
    $stdout = Join-Path $LogRoot "$Name.out.log"
    $stderr = Join-Path $LogRoot "$Name.err.log"
    $argumentLine = Format-ArgumentList -Arguments $Arguments
    $process = Start-Process -FilePath $FfmpegPath -ArgumentList $argumentLine -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    Write-Host "$Name started. PID=$($process.Id) stdout=$stdout stderr=$stderr"
}

$configPath = Resolve-ConfigPath -Path $Config
$settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

Assert-Value -Value $settings.receiver.host -Message "receiver.host is required"
Assert-Value -Value $settings.receiver.basePort -Message "receiver.basePort is required"
if (-not $settings.receiver.srtLatencyMicros) {
    $settings.receiver | Add-Member -NotePropertyName srtLatencyMicros -NotePropertyValue 120000
}

if ($settings.video -and $settings.video.enabled) {
    $videoUrl = Get-SrtUrl -Receiver $settings.receiver -PortOffset ([int]$settings.video.portOffset)
    $videoArgs = @(
        "-hide_banner",
        "-nostdin",
        "-loglevel", "info",
        "-f", "gdigrab",
        "-framerate", "$($settings.video.framerate)",
        "-video_size", "$($settings.video.width)x$($settings.video.height)",
        "-i", "$($settings.video.source)",
        "-an",
        "-c:v", "h264_nvenc",
        "-preset", "$($settings.video.preset)",
        "-tune", "$($settings.video.tune)",
        "-b:v", "$($settings.video.bitrate)",
        "-maxrate", "$($settings.video.maxrate)",
        "-bufsize", "$($settings.video.bufsize)",
        "-pix_fmt", "yuv420p",
        "-g", "$([int]$settings.video.framerate * 2)",
        "-f", "mpegts",
        $videoUrl
    )
    Start-SourceProcess -Name "video" -Arguments $videoArgs
}

foreach ($audio in @($settings.audioSources)) {
    Assert-Value -Value $audio.name -Message "audioSources[].name is required"
    Assert-Value -Value $audio.device -Message "audioSources[].device is required"
    $audioUrl = Get-SrtUrl -Receiver $settings.receiver -PortOffset ([int]$audio.portOffset)
    $audioArgs = @(
        "-hide_banner",
        "-nostdin",
        "-loglevel", "info",
        "-f", "dshow",
        "-i", "audio=$($audio.device)",
        "-vn",
        "-c:a", "$(if ($audio.codec) { $audio.codec } else { "aac" })",
        "-b:a", "$($audio.bitrate)",
        "-f", "mpegts",
        $audioUrl
    )
    Start-SourceProcess -Name "audio-$($audio.name)" -Arguments $audioArgs
}

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run complete. Start OBS Media Sources in listener mode before running without -DryRun."
}
