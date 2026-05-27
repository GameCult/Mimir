param(
    [string]$Configuration = "Release",
    [string]$TemplateVersion = "master"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$sdkRoot = Join-Path $root "artifacts\obs-sdk"
$templateRoot = Join-Path $sdkRoot "obs-plugintemplate"
$depsRoot = Join-Path $templateRoot ".deps"
$buildRoot = Join-Path $templateRoot "build_x64_local"
$pluginBuildRoot = Join-Path $root "native\obs_stem_source\build"

New-Item -ItemType Directory -Force $sdkRoot | Out-Null
if (-not (Test-Path $templateRoot)) {
    git clone --depth 1 --branch $TemplateVersion https://github.com/obsproject/obs-plugintemplate.git $templateRoot
}

if (-not (Test-Path (Join-Path $depsRoot "cmake\libobsConfig.cmake"))) {
    cmake -S $templateRoot -B $buildRoot -G "Visual Studio 17 2022" -A x64 -DENABLE_FRONTEND_API=OFF -DENABLE_QT=OFF
}

cmake `
    -S (Join-Path $root "native\obs_stem_source") `
    -B $pluginBuildRoot `
    -Dlibobs_DIR="$(Join-Path $depsRoot 'cmake')" `
    -DCMAKE_PREFIX_PATH="$depsRoot"

cmake --build $pluginBuildRoot --config $Configuration --parallel
