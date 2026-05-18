$ErrorActionPreference = "Stop"

$root = "C:\Meta\LocalCastBridge"
$soundVolumeView = "C:\Users\Madman's Lullaby\AppData\Local\Microsoft\WinGet\Links\SoundVolumeView.exe"
$senderStop = Join-Path $root "scripts\sender-stop.ps1"

Set-Location $root

& $senderStop

if (Test-Path -LiteralPath $soundVolumeView) {
    & $soundVolumeView /SetDefault "Focusrite USB Audio\Device\Speakers\Render" 0
    & $soundVolumeView /SetDefault "Focusrite USB Audio\Device\Speakers\Render" 1
} else {
    Write-Warning "SoundVolumeView was not found at $soundVolumeView"
}

Get-Process voicemeeter_x64 -ErrorAction SilentlyContinue | Stop-Process -Force

