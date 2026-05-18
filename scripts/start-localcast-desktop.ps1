$ErrorActionPreference = "Stop"

$root = "C:\Meta\LocalCastBridge"
$soundVolumeView = "C:\Users\Madman's Lullaby\AppData\Local\Microsoft\WinGet\Links\SoundVolumeView.exe"
$ffmpeg = "C:\Users\Madman's Lullaby\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe"
$config = Join-Path $root "config\localcast.json"
$senderStart = Join-Path $root "scripts\sender-start.ps1"

Set-Location $root

if (Test-Path -LiteralPath $soundVolumeView) {
    & $soundVolumeView /SetDefault "Focusrite USB Audio\Device\Speakers\Render" 0
    & $soundVolumeView /SetDefault "Focusrite USB Audio\Device\Speakers\Render" 1
    & $soundVolumeView /SetDefault "Focusrite USB Audio\Device\Analogue 1 + 2\Capture" 0
    & $soundVolumeView /SetDefault "Focusrite USB Audio\Device\Analogue 1 + 2\Capture" 1
} else {
    Write-Warning "SoundVolumeView was not found at $soundVolumeView"
}

& $senderStart -Config $config -FfmpegPath $ffmpeg
