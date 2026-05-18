param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

Set-Location -LiteralPath $Root

$pidPath = Join-Path $Root "logs\audio-phase-field.pid"
if (-not (Test-Path -LiteralPath $pidPath)) {
    Write-Host "No live audio phase-field PID file found."
    exit 0
}

$pidText = (Get-Content -LiteralPath $pidPath -Raw).Trim()
if ($pidText.Length -eq 0) {
    Remove-Item -LiteralPath $pidPath -Force
    Write-Host "Empty PID file removed."
    exit 0
}

$process = Get-Process -Id ([int]$pidText) -ErrorAction SilentlyContinue
if ($null -ne $process) {
    Stop-Process -Id $process.Id -Force
    Write-Host "Stopped live audio phase-field PID $($process.Id)"
} else {
    Write-Host "Live audio phase-field PID $pidText is not running."
}

Remove-Item -LiteralPath $pidPath -Force
