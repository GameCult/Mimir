$ErrorActionPreference = "Stop"

$root = "C:\Meta\LocalCastBridge"
$voicemeeter = "C:\Program Files (x86)\VB\Voicemeeter\voicemeeter_x64.exe"
$soundVolumeView = "C:\Users\Madman's Lullaby\AppData\Local\Microsoft\WinGet\Links\SoundVolumeView.exe"
$ffmpeg = "C:\Users\Madman's Lullaby\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe"
$config = Join-Path $root "config\localcast.json"
$senderStart = Join-Path $root "scripts\sender-start.ps1"
$voicemeeterRouting = Join-Path $root "scripts\configure-voicemeeter-routing.ps1"

Set-Location $root

if (Test-Path -LiteralPath $voicemeeter) {
    Start-Process -FilePath $voicemeeter
    Start-Sleep -Seconds 2
} else {
    Write-Warning "Voicemeeter was not found at $voicemeeter"
}

if (Test-Path -LiteralPath $soundVolumeView) {
    & $soundVolumeView /SetDefault "VB-Audio Voicemeeter VAIO\Device\Voicemeeter VAIO3 Input\Render" 0
    & $soundVolumeView /SetDefault "VB-Audio Voicemeeter VAIO\Device\Voicemeeter VAIO3 Input\Render" 1
    & $soundVolumeView /SetDefault "VB-Audio Voicemeeter VAIO\Device\Voicemeeter Out B3\Capture" 0
    & $soundVolumeView /SetDefault "VB-Audio Voicemeeter VAIO\Device\Voicemeeter Out B3\Capture" 1
} else {
    Write-Warning "SoundVolumeView was not found at $soundVolumeView"
}

if (Test-Path -LiteralPath $voicemeeterRouting) {
    & $voicemeeterRouting
} else {
    Write-Warning "Voicemeeter routing script was not found at $voicemeeterRouting"
}

& $senderStart -Config $config -FfmpegPath $ffmpeg
