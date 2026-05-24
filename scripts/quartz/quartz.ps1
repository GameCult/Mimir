param(
    [ValidateSet("build", "dev")]
    [string]$Command = "dev"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sharedQuartzRoot = if ($env:GAMECULT_QUARTZ_ROOT) {
    $env:GAMECULT_QUARTZ_ROOT
} else {
    Join-Path (Split-Path -Parent $repoRoot) "GameCult-Quartz"
}
$portableNodeRoot = Join-Path $repoRoot ".tools\node-v24.15.0-win-x64"
$portableNode = Join-Path $portableNodeRoot "node.exe"

if (Test-Path $portableNode) {
    $node = $portableNode
    $env:PATH = "$portableNodeRoot;$env:PATH"
} else {
    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    if (-not $nodeCommand) {
        throw "node was not found. Install Node.js or restore the portable runtime under .tools."
    }

    $node = $nodeCommand.Source
}

$env:npm_config_cache = Join-Path $repoRoot ".npm-cache"

if (-not (Test-Path $sharedQuartzRoot)) {
    throw "GameCult-Quartz was not found at '$sharedQuartzRoot'. Clone it beside this repo or set GAMECULT_QUARTZ_ROOT."
}

$buildScript = Join-Path $sharedQuartzRoot "scripts\build-site.mjs"

if (-not (Test-Path $buildScript)) {
    throw "GameCult-Quartz build script was not found at '$buildScript'."
}

$scriptArgs = @(
    $buildScript,
    $Command,
    "--siteRoot", $repoRoot,
    "--overlayDir", "site",
    "--contentDir", ".",
    "--outputDir", "quartz-site/public"
)

& $node @scriptArgs
