param(
  [string] $Target = "eve",
  [string] $SharedTextureName = "Global\MimirFensalirProgramTexture",
  [string] $SharedFenceName = "Global\MimirFensalirProgramFence",
  [string] $Configuration = "Debug",
  [int] $Width = 1620,
  [int] $Height = 2160,
  [int] $Fps = 30,
  [int] $Port = 8792,
  [string] $LanHost = "192.168.1.66",
  [string] $Encoder = "h264_nvenc",
  [int] $Quality = 26,
  [string] $FfmpegPath = "",
  [switch] $NoReverseTunnel,
  [string] $MimirLogPath = "logs\eve-dashboard-mimir.log",
  [string] $RelayLogPath = "logs\eve-dashboard-relay.log",
  [string] $TunnelLogPath = "logs\eve-dashboard-ssh-tunnel.log",
  [string[]] $AppArguments = @()
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$mimirLog = Join-Path $repoRoot $MimirLogPath
$mimirErr = [System.IO.Path]::ChangeExtension($mimirLog, ".err.log")
$relayLog = Join-Path $repoRoot $RelayLogPath
$relayErr = [System.IO.Path]::ChangeExtension($relayLog, ".err.log")
$tunnelLog = Join-Path $repoRoot $TunnelLogPath
$tunnelErr = [System.IO.Path]::ChangeExtension($tunnelLog, ".err.log")
New-Item -ItemType Directory -Path (Split-Path -Parent $mimirLog) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $relayLog) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $tunnelLog) -Force | Out-Null

Write-Host "Checking EVE SSH target: $Target"
ssh $Target "uname -a >/tmp/mimir-eve-dashboard-check.txt && cat /tmp/mimir-eve-dashboard-check.txt"

$env:FENSALIR_PROGRAM_OUTPUT_D3D12 = "1"
$env:FENSALIR_PROGRAM_OUTPUT_NAME = $SharedTextureName
$env:FENSALIR_PROGRAM_OUTPUT_FENCE_NAME = $SharedFenceName

$mimirProject = Join-Path $repoRoot "src\Mimir.App\Mimir.App.csproj"
$mimirArgs = @(
  "run",
  "--project",
  $mimirProject,
  "-c",
  $Configuration,
  "--"
) + $AppArguments

Write-Host "Starting Mimir/Fensalir program output: $SharedTextureName"
Write-Host "Program output fence: $SharedFenceName"
$mimirProcess = Start-Process -FilePath "dotnet" `
  -ArgumentList $mimirArgs `
  -WorkingDirectory $repoRoot `
  -RedirectStandardOutput $mimirLog `
  -RedirectStandardError $mimirErr `
  -WindowStyle Hidden `
  -PassThru

Write-Host "Mimir PID $($mimirProcess.Id); waiting for shared texture publication..."
$deadline = [DateTimeOffset]::Now.AddSeconds(30)
do {
  Start-Sleep -Milliseconds 250
  $mimirText = if (Test-Path $mimirLog) { Get-Content -Raw $mimirLog } else { "" }
  if ($mimirText -match "D3D12 program output shared texture") {
    break
  }
} while ([DateTimeOffset]::Now -lt $deadline -and -not $mimirProcess.HasExited)

if ($mimirProcess.HasExited) {
  throw "Mimir exited before publishing the shared program texture. Check $mimirLog and $mimirErr."
}

$relayProject = Join-Path $repoRoot "src\Mimir.EveRelay\Mimir.EveRelay.csproj"
$relayArgs = @(
  "run",
  "--project",
  $relayProject,
  "-c",
  $Configuration,
  "--no-restore",
  "--",
  "--shared-texture", $SharedTextureName,
  "--width", $Width,
  "--height", $Height,
  "--fps", $Fps,
  "--port", $Port,
  "--lan-host", $LanHost,
  "--encoder", $Encoder,
  "--quality", $Quality
)
if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) {
  $relayArgs += @("--ffmpeg", $FfmpegPath)
}

Write-Host "Starting H.264 Eve relay on port $Port"
$relayProcess = Start-Process -FilePath "dotnet" `
  -ArgumentList $relayArgs `
  -WorkingDirectory $repoRoot `
  -RedirectStandardOutput $relayLog `
  -RedirectStandardError $relayErr `
  -WindowStyle Hidden `
  -PassThru

if (-not $NoReverseTunnel) {
  $forwardPattern = "-R ${Port}:127.0.0.1:${Port}"
  $existingTunnel = Get-CimInstance Win32_Process -Filter "name = 'ssh.exe'" |
    Where-Object { $_.CommandLine -like "*$forwardPattern*" -and $_.CommandLine -like "* $Target*" } |
    Select-Object -First 1

  if ($existingTunnel) {
    Write-Host "Using existing EVE reverse tunnel PID $($existingTunnel.ProcessId)."
  } else {
    Write-Host "Starting EVE reverse tunnel: $Target 127.0.0.1:$Port -> Starfire 127.0.0.1:$Port"
    $tunnelProcess = Start-Process -FilePath "ssh" `
      -ArgumentList @("-N", "-R", "${Port}:127.0.0.1:${Port}", $Target) `
      -RedirectStandardOutput $tunnelLog `
      -RedirectStandardError $tunnelErr `
      -WindowStyle Hidden `
      -PassThru
    Write-Host "Tunnel PID $($tunnelProcess.Id)."
  }

  Start-Sleep -Milliseconds 500
  ssh $Target "curl -s --max-time 3 http://127.0.0.1:$Port/health || true"
}

Write-Host "Relay PID $($relayProcess.Id). Launching EveCanvas."
ssh $Target "uiopen --bundleid org.gamecult.evecanvas"

Write-Host "Poll with:"
Write-Host "  Get-Content -Tail 40 '$mimirLog'"
Write-Host "  Get-Content -Tail 40 '$mimirErr'"
Write-Host "  Get-Content -Tail 40 '$relayLog'"
Write-Host "  Get-Content -Tail 40 '$relayErr'"
Write-Host "  Get-Content -Tail 40 '$tunnelLog'"
Write-Host "  Get-Content -Tail 40 '$tunnelErr'"
