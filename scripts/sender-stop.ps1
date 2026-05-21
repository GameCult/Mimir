param(
    [string]$LogDirectory = ".\logs"
)

$ErrorActionPreference = "Stop"

$logs = Resolve-Path -LiteralPath $LogDirectory -ErrorAction SilentlyContinue
if (-not $logs) {
    Write-Host "No log directory found at $LogDirectory"
    return
}

$ffmpegProcesses = Get-CimInstance Win32_Process |
    Where-Object {
        $_.Name -ieq "ffmpeg.exe" -and
        $_.CommandLine -like "*srt://*"
    }

if (-not $ffmpegProcesses) {
    Write-Host "No Mimir-looking ffmpeg SRT processes found."
    return
}

foreach ($process in $ffmpegProcesses) {
    Stop-Process -Id $process.ProcessId -Force
    Write-Host "Stopped ffmpeg PID=$($process.ProcessId)"
}

