param(
    [int]$DurationSeconds = 15,
    [string]$OutputPath = "artifacts\runtime\kiyo-dual-mic-composite\kiyo-dual-mic-composite.mp4",
    [string]$LogRoot = "artifacts\runtime\kiyo-dual-mic-composite",
    [string]$FfmpegPath = "ffmpeg",
    [string]$AsioProbePath = "native\probes\asio_audio_cadence\build\Release\asio_audio_cadence.exe",
    [string]$KiyoVideoDevice = "USB Video Device",
    [int]$KiyoWidth = 640,
    [int]$KiyoHeight = 480,
    [int]$KiyoFrameRate = 30,
    [int]$SampleRate = 48000,
    [int]$AsioChannels = 4,
    [int]$ShotgunChannel = 0,
    [int]$CardioidChannel = 1,
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

function Start-LoggedProcess([string]$FileName, [string[]]$Arguments, [string]$StdoutPath, [string]$StderrPath) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ($Arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join " "
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $process) {
        throw "Could not start $FileName"
    }

    $stdoutStream = [System.IO.File]::Open($StdoutPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    $stderrStream = [System.IO.File]::Open($StderrPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    return [pscustomobject]@{
        Process = $process
        StdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
        StderrTask = $process.StandardError.BaseStream.CopyToAsync($stderrStream)
        StdoutStream = $stdoutStream
        StderrStream = $stderrStream
    }
}

function Wait-LoggedProcess($Handle, [int]$TimeoutSeconds, [string]$Name) {
    if (-not $Handle.Process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $Handle.Process.Kill($true) } catch {}
        throw "$Name timed out after ${TimeoutSeconds}s."
    }

    [void]$Handle.StdoutTask.GetAwaiter().GetResult()
    [void]$Handle.StderrTask.GetAwaiter().GetResult()
    $Handle.StdoutStream.Dispose()
    $Handle.StderrStream.Dispose()
    if ($Handle.Process.ExitCode -ne 0) {
        throw "$Name failed with exit code $($Handle.Process.ExitCode)."
    }
}

$repo = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$absoluteLogRoot = if ([System.IO.Path]::IsPathRooted($LogRoot)) { $LogRoot } else { Join-Path $repo $LogRoot }
$runDirectory = Join-Path $absoluteLogRoot $timestamp
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

$absoluteOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repo $OutputPath }
$outputDirectory = Split-Path -Parent $absoluteOutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$asioRaw = Join-Path $runDirectory "scarlett-f32-interleaved.raw"
$kiyoMkv = Join-Path $runDirectory "kiyo-video.mkv"
$compositeWav = Join-Path $runDirectory "dual-mic-composite.wav"
$absoluteAsioProbe = if ([System.IO.Path]::IsPathRooted($AsioProbePath)) { $AsioProbePath } else { Join-Path $repo $AsioProbePath }

$asioArgs = @(
    "--set-sample-rate", $SampleRate.ToString(),
    "--record-f32-interleaved", $asioRaw,
    "--capture-seconds", $DurationSeconds.ToString()
)
$kiyoArgs = @(
    "-hide_banner",
    "-y",
    "-thread_queue_size", "1024",
    "-rtbufsize", "256M",
    "-f", "dshow",
    "-video_size", "${KiyoWidth}x${KiyoHeight}",
    "-framerate", $KiyoFrameRate.ToString(),
    "-i", "video=$KiyoVideoDevice",
    "-t", $DurationSeconds.ToString(),
    "-r", $KiyoFrameRate.ToString(),
    "-c:v", "h264_nvenc",
    "-preset", "p4",
    "-tune", "ll",
    "-pix_fmt", "yuv420p",
    $kiyoMkv
)

Write-Host "Kiyo + computed dual-mic composite recording:"
Write-Host "  Kiyo: $KiyoVideoDevice ${KiyoWidth}x${KiyoHeight}@$KiyoFrameRate"
Write-Host "  ASIO: $absoluteAsioProbe sampleRate=$SampleRate channels=$AsioChannels shotgun=$ShotgunChannel cardioid=$CardioidChannel"
Write-Host "  Run dir: $runDirectory"
Write-Host "  Output: $absoluteOutputPath"

if ($DryRun) {
    Write-Host ($absoluteAsioProbe + " " + ($asioArgs -join " "))
    Write-Host ($FfmpegPath + " " + ($kiyoArgs -join " "))
    exit 0
}

$asio = Start-LoggedProcess $absoluteAsioProbe $asioArgs (Join-Path $runDirectory "asio.out.log") (Join-Path $runDirectory "asio.err.log")
$kiyo = Start-LoggedProcess $FfmpegPath $kiyoArgs (Join-Path $runDirectory "kiyo.out.log") (Join-Path $runDirectory "kiyo.err.log")

Wait-LoggedProcess $asio ($DurationSeconds + 15) "ASIO capture"
Wait-LoggedProcess $kiyo ($DurationSeconds + 15) "Kiyo capture"

dotnet run --project (Join-Path $repo "src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj") -- `
    --asio-dual-mic-composite-wav `
    --input $asioRaw `
    --output $compositeWav `
    --sample-rate $SampleRate `
    --channels $AsioChannels `
    --shotgun-channel $ShotgunChannel `
    --cardioid-channel $CardioidChannel
if ($LASTEXITCODE -ne 0) {
    throw "Dual-mic composite WAV generation failed."
}

$muxArgs = @(
    "-hide_banner",
    "-y",
    "-i", $kiyoMkv,
    "-i", $compositeWav,
    "-map", "0:v:0",
    "-map", "1:a:0",
    "-c:v", "copy",
    "-c:a", "aac",
    "-b:a", "192k",
    "-ar", "48000",
    "-movflags", "+faststart",
    $absoluteOutputPath
)
$mux = Start-LoggedProcess $FfmpegPath $muxArgs (Join-Path $runDirectory "mux.out.log") (Join-Path $runDirectory "mux.err.log")
Wait-LoggedProcess $mux 60 "Mux"

Write-Host "Recording complete:"
Write-Host "  $absoluteOutputPath"
Write-Host "  composite wav: $compositeWav"
Write-Host "  report: $([System.IO.Path]::ChangeExtension($compositeWav, '.json'))"
