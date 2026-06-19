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
$localeSourceDir = Join-Path $repoRoot "native\obs_stem_source\data\locale"

if (-not [string]::IsNullOrWhiteSpace($ObsRoot)) {
    $targetDir = Join-Path $ObsRoot "obs-plugins\64bit"
    $targetLocaleDir = Join-Path $ObsRoot "data\obs-plugins\mimir_obs_stem_source\locale"
} else {
    if ([string]::IsNullOrWhiteSpace($PluginRoot)) {
        if ($env:OBS_PLUGIN_ROOT) {
            $PluginRoot = $env:OBS_PLUGIN_ROOT
        } else {
            $PluginRoot = Join-Path $env:ALLUSERSPROFILE "obs-studio\plugins"
        }
    }

    $targetDir = Join-Path $PluginRoot "mimir_obs_stem_source\bin\64bit"
    $targetLocaleDir = Join-Path $PluginRoot "mimir_obs_stem_source\data\locale"
}

$targetDll = Join-Path $targetDir "mimir_obs_stem_source.dll"

if (-not $SkipBuild) {
    & (Join-Path $repoRoot "scripts\build-obs-stem-plugin.ps1") -Configuration $Configuration
}

if (-not (Test-Path $pluginDll)) {
    throw "Built plugin DLL was not found at '$pluginDll'."
}
if (-not (Test-Path $localeSourceDir)) {
    throw "Plugin locale directory was not found at '$localeSourceDir'."
}

if (-not (Test-Path $targetDir)) {
    if ($WhatIf) {
        Write-Host "Would create $targetDir"
    } else {
        New-Item -ItemType Directory -Force $targetDir | Out-Null
    }
}
if (-not (Test-Path $targetLocaleDir)) {
    if ($WhatIf) {
        Write-Host "Would create $targetLocaleDir"
    } else {
        New-Item -ItemType Directory -Force $targetLocaleDir | Out-Null
    }
}

if ($WhatIf) {
    Write-Host "Would copy $pluginDll -> $targetDll"
    Get-ChildItem -LiteralPath $localeSourceDir -File | ForEach-Object {
        Write-Host "Would copy $($_.FullName) -> $(Join-Path $targetLocaleDir $_.Name)"
    }
    return
}

Copy-Item -LiteralPath $pluginDll -Destination $targetDll -Force
Copy-Item -Path (Join-Path $localeSourceDir "*") -Destination $targetLocaleDir -Force
Write-Host "Installed Mimir OBS plugin: $targetDll"
Write-Host "OBS source types: Mimir Program Texture, Mimir Audio Stem, Muninn Stream, Muninn Video, Muninn Audio"
