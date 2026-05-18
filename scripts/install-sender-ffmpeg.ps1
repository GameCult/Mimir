param(
    [string]$LogDirectory = "C:\Meta\logs"
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force $LogDirectory | Out-Null

$stdout = Join-Path $LogDirectory "localcast-ffmpeg-install.out.log"
$stderr = Join-Path $LogDirectory "localcast-ffmpeg-install.err.log"

$winget = Get-Command winget -ErrorAction Stop
$arguments = @(
    "install",
    "--id", "Gyan.FFmpeg",
    "--source", "winget",
    "--accept-package-agreements",
    "--accept-source-agreements",
    "--disable-interactivity"
)

$process = Start-Process -FilePath $winget.Source -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden

[pscustomobject]@{
    ProcessId = $process.Id
    Stdout = $stdout
    Stderr = $stderr
} | ConvertTo-Json

