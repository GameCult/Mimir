param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Python = ".\.venv\Scripts\python.exe",
    [string]$Field = ".\calibration\runs\audio-program-live-20260518-180226\field-program-cleaned.wav",
    [string]$Reference = ".\calibration\runs\audio-program-live-20260518-180226\ground_truth_loopback.wav",
    [string]$Cache = ".\calibration\runs\audio-phase-field.msgpack",
    [string]$ProbeDir = ".\calibration\runs\active-probes",
    [double]$TargetConfidence = 0.72,
    [double]$TriggerConfidence = 0.45,
    [int]$MinProbeIntervalFrames = 1,
    [double]$ProbeLevelDbfs = -24.0,
    [double]$ProbeSeconds = 0.08,
    [double]$HarmonicRootHz = 440.0,
    [int]$HarmonicVoices = 48,
    [switch]$NoPlayback
)

$ErrorActionPreference = "Stop"

Set-Location -LiteralPath $Root

$logDir = Join-Path $Root "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$resolvedPython = if ([System.IO.Path]::IsPathRooted($Python)) { $Python } else { Join-Path $Root $Python }
$stdout = Join-Path $logDir "audio-phase-field.stdout.log"
$stderr = Join-Path $logDir "audio-phase-field.stderr.log"
$pidPath = Join-Path $logDir "audio-phase-field.pid"

$arguments = @(
    ".\scripts\stream_phase_field.py",
    "--field", $Field,
    "--reference", $Reference,
    "--cache", $Cache,
    "--source-id", "host-focusrite",
    "--source-id", "co-streamer-focusrite",
    "--source-id", "kiyo-0",
    "--source-id", "kiyo-1",
    "--source-id", "ps-eye-0",
    "--source-id", "ps-eye-1",
    "--loop",
    "--realtime",
    "--maintain-confidence",
    "--cram-harmonic-probes",
    "--probe-output-dir", $ProbeDir,
    "--probe-unmasked",
    "--target-confidence", ([string]$TargetConfidence),
    "--trigger-confidence", ([string]$TriggerConfidence),
    "--min-probe-interval-frames", ([string]$MinProbeIntervalFrames),
    "--probe-level-dbfs", ([string]$ProbeLevelDbfs),
    "--probe-seconds", ([string]$ProbeSeconds),
    "--harmonic-root-hz", ([string]$HarmonicRootHz),
    "--harmonic-voices", ([string]$HarmonicVoices)
)

if (-not $NoPlayback) {
    $arguments += "--play-probes"
}

$process = Start-Process -FilePath $resolvedPython -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
Set-Content -Path $pidPath -Value ([string]$process.Id) -Encoding ASCII

Write-Host "Started live audio phase-field confidence loop PID $($process.Id)"
Write-Host "stdout: $stdout"
Write-Host "stderr: $stderr"
Write-Host "pid: $pidPath"
