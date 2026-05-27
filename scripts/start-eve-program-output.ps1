param(
  [string] $Target = "eve",
  [string] $SharedTextureName = "Global\MimirFensalirProgramTexture",
  [string] $SharedFenceName = "Global\MimirFensalirProgramFence",
  [int] $SharedTextureRingCount = 1,
  [string] $Configuration = "Debug",
  [string] $LogPath = "logs\eve-program-output-fensalir.log",
  [string[]] $AppArguments = @()
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$absoluteLogPath = Join-Path $repoRoot $LogPath
$absoluteErrorLogPath = [System.IO.Path]::ChangeExtension($absoluteLogPath, ".err.log")
$logDirectory = Split-Path -Parent $absoluteLogPath
if (-not (Test-Path $logDirectory)) {
  New-Item -ItemType Directory -Path $logDirectory | Out-Null
}

Write-Host "Checking EVE SSH target: $Target"
ssh $Target "uname -a >/tmp/mimir-eve-program-output-check.txt && cat /tmp/mimir-eve-program-output-check.txt"

$env:FENSALIR_PROGRAM_OUTPUT_D3D12 = "1"
$env:FENSALIR_PROGRAM_OUTPUT_NAME = $SharedTextureName
$env:FENSALIR_PROGRAM_OUTPUT_FENCE_NAME = $SharedFenceName
$env:FENSALIR_PROGRAM_OUTPUT_RING_COUNT = [string] $SharedTextureRingCount

$projectPath = Join-Path $repoRoot "src\Mimir.App\Mimir.App.csproj"
$dotnetArgs = @(
  "run",
  "--project",
  $projectPath,
  "-c",
  $Configuration,
  "--"
) + $AppArguments

Write-Host "Starting Mimir/Fensalir with shared D3D12 output: $SharedTextureName"
Write-Host "Program output fence: $SharedFenceName"
Write-Host "Program output ring slots: $SharedTextureRingCount"
Write-Host "Log: $absoluteLogPath"
$process = Start-Process -FilePath "dotnet" `
  -ArgumentList $dotnetArgs `
  -WorkingDirectory $repoRoot `
  -RedirectStandardOutput $absoluteLogPath `
  -RedirectStandardError $absoluteErrorLogPath `
  -WindowStyle Hidden `
  -PassThru

Write-Host "Started PID $($process.Id). Poll with:"
Write-Host "  Get-Content -Tail 40 '$absoluteLogPath'"
Write-Host "  Get-Content -Tail 40 '$absoluteErrorLogPath'"
