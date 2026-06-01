param(
    [int]$RavenPort = 5204,
    [int]$DurationSeconds = 20,
    [int]$StartupTimeoutSeconds = 20,
    [int]$Width = 1280,
    [int]$Height = 720,
    [int]$FrameRate = 30,
    [string]$OutputPath = "artifacts\runtime\stream-proof\stream-proof-output.mp4",
    [string]$LogRoot = "artifacts\runtime\stream-proof",
    [string]$FfmpegPath = "ffmpeg",
    [switch]$StartRavenSender,
    [string]$RavenHost = "madman's lullaby@192.168.1.84",
    [string]$RavenAddress = "192.168.1.84",
    [string]$RavenRepo = "C:\Meta\Mimir",
    [string]$StarfireHost = "192.168.1.66",
    [switch]$RavenSenderListens,
    [ValidateSet("srt", "tcp-listener")]
    [string]$RavenSenderTransport = "srt",
    [string]$RavenInputOverride = "",
    [string]$KiyoVideoDevice = "USB Video Device",
    [string]$StarfireScarlettAudioDevice = "Analogue 1 + 2 (2- Focusrite USB Audio)",
    [int]$KiyoWidth = 640,
    [int]$KiyoHeight = 480,
    [int]$KiyoFrameRate = 30,
    [int]$KiyoCornerWidth = 320,
    [int]$KiyoMargin = 28,
    [double]$RavenOffsetSeconds = 0.0,
    [double]$KiyoOffsetSeconds = 0.0,
    [double]$MicOffsetSeconds = 0.0,
    [switch]$NoStarfireScarlett,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Quote-ProcessArgument([string]$Value) {
    if ($Value.Length -eq 0) {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

$repo = Split-Path -Parent $PSScriptRoot
$absoluteOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $repo $OutputPath
}
$outputDirectory = Split-Path -Parent $absoluteOutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$absoluteLogRoot = if ([System.IO.Path]::IsPathRooted($LogRoot)) {
    $LogRoot
} else {
    Join-Path $repo $LogRoot
}
New-Item -ItemType Directory -Force -Path $absoluteLogRoot | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutLog = Join-Path $absoluteLogRoot "stream-proof-record-$timestamp.out.log"
$stderrLog = Join-Path $absoluteLogRoot "stream-proof-record-$timestamp.err.log"

$ravenEndpoint = if ($RavenInputOverride.Length -gt 0) {
    $RavenInputOverride
} elseif ($RavenSenderListens) {
    "srt://${RavenAddress}:${RavenPort}?mode=caller&latency=120000&timeout=30000000"
} else {
    "srt://0.0.0.0:${RavenPort}?mode=listener&latency=120000&timeout=30000000"
}
$ravenSender = "${RavenRepo}\scripts\start-raven-av-sender.ps1"

if ($StartRavenSender) {
    $taskName = "MimirRavenAvSender$RavenPort"
    $remoteCmd = "${RavenRepo}\scripts\run-raven-av-$RavenPort.cmd"
    $remoteTargetHost = if ($RavenSenderListens) { "0.0.0.0" } else { $StarfireHost }
    $remoteSrtMode = if ($RavenSenderListens) { "listener" } else { "caller" }
    $remoteCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ravenSender -TargetHost $remoteTargetHost -Port $RavenPort -SrtMode $remoteSrtMode -Transport $RavenSenderTransport"
    $taskTime = (Get-Date).AddMinutes(1).ToString("HH:mm")
    Write-Host "Starting Raven muxed A/V sender on ${RavenHost} through interactive task ${taskName}: $remoteCommand"
    if (-not $DryRun) {
        ssh $RavenHost "cmd /c echo $remoteCommand ^> $remoteCmd" | Out-Null
        ssh $RavenHost "schtasks /Delete /TN $taskName /F" | Out-Null
        ssh $RavenHost "schtasks /Create /TN $taskName /SC ONCE /ST $taskTime /TR $remoteCmd /F /IT" | Out-Host
        ssh $RavenHost "schtasks /Run /TN $taskName" | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Could not start Raven muxed A/V sender task on $RavenHost."
        }
        Start-Sleep -Milliseconds 1500
    }
}

$arguments = @(
    "-hide_banner",
    "-y",
    "-thread_queue_size", "1024",
    "-itsoffset", $RavenOffsetSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    "-i", $ravenEndpoint,
    "-thread_queue_size", "1024",
    "-rtbufsize", "256M",
    "-itsoffset", $KiyoOffsetSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    "-f", "dshow",
    "-video_size", "${KiyoWidth}x${KiyoHeight}",
    "-framerate", $KiyoFrameRate.ToString(),
    "-i", "video=$KiyoVideoDevice"
)

if (-not $NoStarfireScarlett) {
    $arguments += @(
        "-thread_queue_size", "1024",
        "-rtbufsize", "256M",
        "-itsoffset", $MicOffsetSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        "-f", "dshow",
        "-i", "audio=$StarfireScarlettAudioDevice"
    )
}

$scaledKiyoHeight = [Math]::Max(1, [int][Math]::Round($KiyoCornerWidth * ($KiyoHeight / [double]$KiyoWidth)))
$overlayX = $KiyoMargin
$overlayY = $KiyoMargin

if ($NoStarfireScarlett) {
    $filter = "[0:v]scale=${Width}:${Height},setsar=1,hqdn3d=1.2:1.0:3.0:2.0[base];" +
        "[1:v]scale=${KiyoCornerWidth}:${scaledKiyoHeight},setsar=1,hqdn3d=1.4:1.1:3.4:2.2[pip];" +
        "[base][pip]overlay=${overlayX}:${overlayY}:format=auto[v];" +
        "[0:a]aresample=async=1:first_pts=0,afftdn=nr=8:nf=-35[a]"
} else {
    $filter = "[0:v]scale=${Width}:${Height},setsar=1,hqdn3d=1.2:1.0:3.0:2.0[base];" +
        "[1:v]scale=${KiyoCornerWidth}:${scaledKiyoHeight},setsar=1,hqdn3d=1.4:1.1:3.4:2.2[pip];" +
        "[base][pip]overlay=${overlayX}:${overlayY}:format=auto[v];" +
        "[0:a]aresample=async=1:first_pts=0,afftdn=nr=8:nf=-35[ravenAudio];" +
        "[2:a]aresample=async=1:first_pts=0,afftdn=nr=10:nf=-38,volume=1.0[heroMic];" +
        "[ravenAudio][heroMic]amix=inputs=2:duration=longest:normalize=0[a]"
}

$arguments += @(
    "-filter_complex", $filter,
    "-map", "[v]",
    "-map", "[a]",
    "-t", $DurationSeconds.ToString(),
    "-r", $FrameRate.ToString(),
    "-c:v", "h264_nvenc",
    "-preset", "p4",
    "-tune", "ll",
    "-rc", "cbr",
    "-b:v", "4500k",
    "-maxrate", "4500k",
    "-bufsize", "9000k",
    "-g", ($FrameRate * 2).ToString(),
    "-pix_fmt", "yuv420p",
    "-c:a", "aac",
    "-b:a", "160k",
    "-ar", "48000",
    "-movflags", "+faststart",
    $absoluteOutputPath
)

Write-Host "Stream-proof recording:"
Write-Host "  Raven input: $ravenEndpoint"
Write-Host "  Raven SRT mode: $(if ($RavenSenderListens) { 'Raven listener, Starfire caller' } else { 'Starfire listener, Raven caller' })"
Write-Host "  Kiyo PIP: $KiyoVideoDevice ${KiyoWidth}x${KiyoHeight}@$KiyoFrameRate -> ${KiyoCornerWidth}x${scaledKiyoHeight}"
Write-Host "  Raven audio: Realtek/default render loopback in muxed NVENC/SRT feed"
Write-Host "  Starfire Scarlett: $(if ($NoStarfireScarlett) { 'disabled' } else { $StarfireScarlettAudioDevice })"
Write-Host "  sync role: Starfire Realtek emits chirps; Raven Realtek carries co-streamer game/program audio; Starfire Scarlett captures hero mics plus Raven shotgun"
Write-Host "  Offsets: raven=${RavenOffsetSeconds}s kiyo=${KiyoOffsetSeconds}s mic=${MicOffsetSeconds}s"
Write-Host "  Output: $absoluteOutputPath"
Write-Host "  stdout: $stdoutLog"
Write-Host "  stderr: $stderrLog"

if ($DryRun) {
    Write-Host ($FfmpegPath + " " + ($arguments -join " "))
    exit 0
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $FfmpegPath
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join " "

$process = [System.Diagnostics.Process]::Start($startInfo)
if (-not $process) {
    throw "Could not start FFmpeg recorder."
}

$stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync([System.IO.File]::Open($stdoutLog, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read))
$stderrTask = $process.StandardError.BaseStream.CopyToAsync([System.IO.File]::Open($stderrLog, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read))
$timeoutMs = [Math]::Max(1, $DurationSeconds + $StartupTimeoutSeconds) * 1000
if (-not $process.WaitForExit($timeoutMs)) {
    try { $process.Kill($true) } catch {}
    throw "FFmpeg recorder timed out after $($DurationSeconds + $StartupTimeoutSeconds)s. Check $stderrLog."
}

[void]$stdoutTask.GetAwaiter().GetResult()
[void]$stderrTask.GetAwaiter().GetResult()
if ($process.ExitCode -ne 0) {
    throw "FFmpeg recorder failed with exit code $($process.ExitCode). Check $stderrLog."
}
