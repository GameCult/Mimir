param(
    [string]$ObsRoot = "",
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ObsRoot)) {
    if ($env:OBS_STUDIO_ROOT) {
        $ObsRoot = $env:OBS_STUDIO_ROOT
    } elseif (Test-Path "C:\Program Files\obs-studio") {
        $ObsRoot = "C:\Program Files\obs-studio"
    } else {
        throw "OBS root was not found. Pass -ObsRoot or set OBS_STUDIO_ROOT."
    }
}

$pluginDll = Join-Path $repoRoot "native\obs_stem_source\build\$Configuration\mimir_obs_stem_source.dll"
$targetDir = Join-Path $ObsRoot "obs-plugins\64bit"
$targetDll = Join-Path $targetDir "mimir_obs_stem_source.dll"

if (-not $SkipBuild) {
    & (Join-Path $repoRoot "scripts\build-obs-stem-plugin.ps1") -Configuration $Configuration
}

if (-not (Test-Path $pluginDll)) {
    throw "Built plugin DLL was not found at '$pluginDll'."
}

if (-not (Test-Path $targetDir)) {
    if ($WhatIf) {
        Write-Host "Would create $targetDir"
    } else {
        New-Item -ItemType Directory -Force $targetDir | Out-Null
    }
}

if ($WhatIf) {
    Write-Host "Would copy $pluginDll -> $targetDll"
    return
}

Copy-Item -LiteralPath $pluginDll -Destination $targetDll -Force
Write-Host "Installed Mimir OBS plugin: $targetDll"
Write-Host "OBS source types: Mimir Program Texture, Mimir Audio Stem"
