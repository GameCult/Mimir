param(
    [string]$ObsRoot = "",
    [string]$PluginRoot = "",
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

$pluginDll = Join-Path $repoRoot "native\obs_stem_source\build\$Configuration\mimir_obs_stem_source.dll"

if (-not [string]::IsNullOrWhiteSpace($ObsRoot)) {
    $targetDir = Join-Path $ObsRoot "obs-plugins\64bit"
} else {
    if ([string]::IsNullOrWhiteSpace($PluginRoot)) {
        if ($env:OBS_PLUGIN_ROOT) {
            $PluginRoot = $env:OBS_PLUGIN_ROOT
        } else {
            $PluginRoot = Join-Path $env:ALLUSERSPROFILE "obs-studio\plugins"
        }
    }

    $targetDir = Join-Path $PluginRoot "mimir_obs_stem_source\bin\64bit"
}

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
Write-Host "OBS source types: Mimir Program Texture, Mimir Audio Stem, Muninn Stream"
