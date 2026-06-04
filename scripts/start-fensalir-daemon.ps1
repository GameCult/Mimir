param(
    [string]$WellLog = "",
    [string]$CultCache = "",
    [int]$Port = 8799,
    [int]$PollMs = 100,
    [int]$WorkerMs = 4,
    [int]$MaxReservoirQueue = 120,
    [double]$GpuBudget = 0.50,
    [double]$CpuBudget = 0.25,
    [switch]$NoBuild,
    [switch]$FromStart
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repo "src\Mimir.FensalirDaemon\Mimir.FensalirDaemon.csproj"
if (-not $NoBuild) {
    dotnet build $project | Out-Host
}

if ([string]::IsNullOrWhiteSpace($CultCache)) {
    $CultCache = Join-Path $repo "state\fensalir-daemon.ccmp"
}

if ([string]::IsNullOrWhiteSpace($WellLog)) {
    $runtimeRoot = Join-Path $repo "artifacts\runtime"
    if (Test-Path $runtimeRoot) {
        $WellLog = Get-ChildItem -Path $runtimeRoot -Recurse -File -Include *.log,*.jsonl |
            Where-Object { $_.Length -gt 0 } |
            Sort-Object LastWriteTimeUtc -Descending |
            Where-Object {
                try {
                    Select-String -Path $_.FullName -Pattern 'mimir\.well_' -Quiet -ErrorAction Stop
                } catch {
                    $false
                }
            } |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $repo "artifacts\runtime\fensalir-daemon-$runId"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$stdout = Join-Path $runDir "fensalir-daemon.out.log"
$stderr = Join-Path $runDir "fensalir-daemon.err.log"
$manifest = Join-Path $runDir "supervisor.json"
$dll = Join-Path $repo "src\Mimir.FensalirDaemon\bin\Debug\net10.0\Mimir.FensalirDaemon.dll"
$rootVerse = "asgard"
$locatedService = "$rootVerse.starfire.mimir-fensalir"
$tuiAddress = "$locatedService/eve/tui"
$guiAddress = "$locatedService/eve/gui"
$bridgeRoute = "ws://127.0.0.1:$Port/eve/deck"

$arguments = @(
    $dll,
    "--port", $Port,
    "--cultcache", $CultCache,
    "--poll-ms", $PollMs,
    "--worker-ms", $WorkerMs,
    "--max-reservoir-queue", $MaxReservoirQueue,
    "--gpu-budget", $GpuBudget.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--cpu-budget", $CpuBudget.ToString([Globalization.CultureInfo]::InvariantCulture)
)

if (-not [string]::IsNullOrWhiteSpace($WellLog)) {
    $arguments += @("--well-log", $WellLog)
}

if ($FromStart) {
    $arguments += "--from-start"
}

$process = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repo -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden

[pscustomobject]@{
    daemon = "Mimir.FensalirDaemon"
    pid = $process.Id
    port = $Port
    workerMs = $WorkerMs
    maxReservoirQueue = $MaxReservoirQueue
    wellLog = $WellLog
    cultCache = $CultCache
    rootVerse = $rootVerse
    canonicalService = "$rootVerse.mimir-fensalir"
    locatedService = $locatedService
    cultMeshAddress = $tuiAddress
    providerSpec = "mimir-fensalir-daemon|Mimir Fensalir Daemon|$tuiAddress"
    endpoints = @($tuiAddress, $guiAddress)
    routes = @(
        [ordered]@{ kind = "websocket-bridge"; address = $bridgeRoute; carries = $tuiAddress; note = "Fensalir Eve deck bridge route; not service identity." },
        [ordered]@{ kind = "websocket-bridge"; address = "$bridgeRoute/cultmesh"; carries = $tuiAddress; note = "Legacy CultMesh bridge route; not service identity." }
    )
    stdout = $stdout
    stderr = $stderr
    started = (Get-Date).ToString("o")
} | ConvertTo-Json | Set-Content -Path $manifest -Encoding UTF8

Write-Host "Started Mimir.FensalirDaemon PID $($process.Id)"
Write-Host "Health: http://127.0.0.1:$Port/health"
Write-Host "Eve provider: mimir-fensalir-daemon|Mimir Fensalir Daemon|$tuiAddress"
Write-Host "Bridge route: $bridgeRoute"
Write-Host "Manifest: $manifest"
