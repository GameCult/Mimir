param(
    [string]$FfmpegPath = "ffmpeg"
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [string]$Label,
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "== $Label =="
    & $FfmpegPath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$Label exited with code $LASTEXITCODE"
    }
}

Write-Host "FFmpeg binary: $FfmpegPath"

Invoke-Checked -Label "Version" -Arguments @("-hide_banner", "-version")
Invoke-Checked -Label "SRT protocol check" -Arguments @("-hide_banner", "-protocols")
Invoke-Checked -Label "NVENC encoder check" -Arguments @("-hide_banner", "-encoders")
Invoke-Checked -Label "DirectShow devices" -Arguments @("-hide_banner", "-f", "dshow", "-list_devices", "true", "-i", "dummy")

Write-Host ""
Write-Host "Look for:"
Write-Host "- protocol list contains srt"
Write-Host "- encoder list contains h264_nvenc"
Write-Host "- audio device names you can copy into config/localcast.json"

