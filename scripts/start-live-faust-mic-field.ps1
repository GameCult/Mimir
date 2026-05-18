param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Python = ".\.venv\Scripts\python.exe",
    [string]$InputPath = ".\calibration\runs\audio-program-live-20260518-180226\field-program-cleaned.wav",
    [string]$Cache = ".\calibration\runs\audio-mic-field.msgpack"
)

$ErrorActionPreference = "Stop"

Set-Location -LiteralPath $Root

$logDir = Join-Path $Root "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$resolvedPython = if ([System.IO.Path]::IsPathRooted($Python)) { $Python } else { Join-Path $Root $Python }
$stdout = Join-Path $logDir "faust-mic-field.stdout.log"
$stderr = Join-Path $logDir "faust-mic-field.stderr.log"
$pidPath = Join-Path $logDir "faust-mic-field.pid"

$arguments = @(
    ".\scripts\stream_faust_mic_field.py",
    "--input", $InputPath,
    "--cache", $Cache,
    "--loop",
    "--realtime",
    "--smoke-readback"
)

$process = Start-Process -FilePath $resolvedPython -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
Set-Content -Path $pidPath -Value ([string]$process.Id) -Encoding ASCII

Write-Host "Started live Faust mic-field publisher PID $($process.Id)"
Write-Host "stdout: $stdout"
Write-Host "stderr: $stderr"
Write-Host "pid: $pidPath"
