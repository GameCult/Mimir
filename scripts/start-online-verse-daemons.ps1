param(
    [string]$NightwingHost = "nightwing",
    [int]$VerseRelayPort = 8798,
    [string]$WitnessUrl = "ws://192.168.1.66:8798/eve/periwinkle",
    [string]$SubscribeUrl = "ws://127.0.0.1:8798/eve/periwinkle/subscribe",
    [string]$ConfigPath = "E:\Projects\Mimir\config\mimir-runtime.well.local.json",
    [string]$WellPublishUrl = "ws://127.0.0.1:8798/eve/periwinkle",
    [string]$RecorderRoot = "C:\Users\Meta\Videos\Mimir\VerseCaptures",
    [int]$FensalirPort = 8799,
    [int]$FensalirWorkerMs = 2,
    [int]$FensalirMaxReservoirQueue = 120,
    [int]$DurationSeconds = 0,
    [int]$MoveFps = 120,
    [int]$MoveFftSize = 256,
    [string[]]$Move = @(
        "move-00-07-04-a3-97-72-usb=/dev/hidraw1:#35ff6c",
        "move-00-07-04-a6-be-5f=/dev/hidraw2:#ff2a00",
        "move-00-06-f5-23-e2-d1=/dev/hidraw3:#00a8ff"
    ),
    [switch]$SkipRuntime,
    [switch]$SkipFensalir,
    [switch]$SkipMoves,
    [switch]$SkipNightwing,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $repo "artifacts\runtime\online-verse-daemons-$runId"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
New-Item -ItemType Directory -Force -Path $RecorderRoot | Out-Null

function Start-LoggedProcess {
    param(
        [string]$Name,
        [string]$FilePath,
        [string[]]$ArgumentList,
        [hashtable]$Environment = @{}
    )

    $stdout = Join-Path $runDir "$Name.out.log"
    $stderr = Join-Path $runDir "$Name.err.log"
    if ($DryRun) {
        Write-Host "DRY $Name`: $FilePath $($ArgumentList -join ' ')"
        return $null
    }

    $previous = @{}
    foreach ($key in $Environment.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
        [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], "Process")
    }
    try {
        $process = Start-Process `
            -FilePath $FilePath `
            -ArgumentList $ArgumentList `
            -WorkingDirectory $repo `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr `
            -PassThru
    } finally {
        foreach ($key in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previous[$key], "Process")
        }
    }
    Set-Content -Path (Join-Path $runDir "$Name.pid") -Value $process.Id
    Write-Host "$Name pid=$($process.Id) stdout=$stdout stderr=$stderr"
    return $process
}

function Wait-HttpHealth {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 45
    )

    if ($DryRun) {
        Write-Host "DRY wait health: $Url"
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                Write-Host "health ok: $Url"
                return
            }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for $Url"
}

$manifest = [ordered]@{
    kind = "mimir.online_verse_daemon_supervisor.v1"
    runId = $runId
    runDir = $runDir
    witnessUrl = $WitnessUrl
    subscribeUrl = $SubscribeUrl
    wellPublishUrl = $WellPublishUrl
    verseRelayPort = $VerseRelayPort
    recorderRoot = $RecorderRoot
    fensalirPort = $FensalirPort
    configPath = $ConfigPath
    startedAt = (Get-Date).ToString("o")
    durationSeconds = $DurationSeconds
    moves = $Move
    processes = @{}
}

Write-Host "Online Verse daemon run: $runDir"

if (-not $DryRun) {
    Get-CimInstance Win32_Process |
        Where-Object {
            ($_.CommandLine -match "Mimir.EveSensorReceiver" -and $_.CommandLine -match "--port $VerseRelayPort") -or
            ($_.CommandLine -match "Mimir.VerseRecorder") -or
            ($_.CommandLine -match "Mimir.FensalirDaemon") -or
            ($_.CommandLine -match "Mimir.Well") -or
            ($_.CommandLine -match "Mimir.BufferSmoke" -and $_.CommandLine -match "--poll-ms")
        } |
        ForEach-Object {
            try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {}
        }
}

$receiver = Start-LoggedProcess `
    -Name "verse-relay" `
    -FilePath "dotnet" `
    -ArgumentList @(
        "run",
        "--project",
        (Join-Path $repo "src\Mimir.EveSensorReceiver\Mimir.EveSensorReceiver.csproj"),
        "--",
        "--port", "$VerseRelayPort",
        "--path", "/eve/periwinkle",
        "--subscribe-path", "/eve/periwinkle/subscribe",
        "--source-id", "nightwing-witness",
        "--type", "cultmesh-observation"
    )
if ($receiver) { $manifest.processes["verseRelayPid"] = $receiver.Id }

Wait-HttpHealth -Url "http://127.0.0.1:$VerseRelayPort/health"

$recorderArgs = @(
    "run",
    "--project",
    (Join-Path $repo "src\Mimir.VerseRecorder\Mimir.VerseRecorder.csproj"),
    "--",
    "--url", $SubscribeUrl,
    "--out-dir", $RecorderRoot,
    "--run-id", $runId,
    "--write-bodies", "true",
    "--body-page-bytes", "134217728"
)
if ($DurationSeconds -gt 0) {
    $recorderArgs += "--seconds"
    $recorderArgs += "$DurationSeconds"
}
$recorder = Start-LoggedProcess -Name "verse-recorder" -FilePath "dotnet" -ArgumentList $recorderArgs
if ($recorder) { $manifest.processes["verseRecorderPid"] = $recorder.Id }

if (-not $SkipFensalir) {
    $fensalirWellLog = Join-Path (Join-Path $RecorderRoot $runId) "observations.jsonl"
    $fensalirCache = Join-Path $runDir "fensalir-daemon.ccmp"
    $fensalir = Start-LoggedProcess `
        -Name "fensalir-daemon" `
        -FilePath "dotnet" `
        -ArgumentList @(
            "run",
            "--project",
            (Join-Path $repo "src\Mimir.FensalirDaemon\Mimir.FensalirDaemon.csproj"),
            "--",
            "--port", "$FensalirPort",
            "--well-log", $fensalirWellLog,
            "--cultcache", $fensalirCache,
            "--poll-ms", "50",
            "--worker-ms", "$FensalirWorkerMs",
            "--max-reservoir-queue", "$FensalirMaxReservoirQueue"
        )
    if ($fensalir) {
        $manifest.processes["fensalirDaemonPid"] = $fensalir.Id
        $manifest.processes["fensalirWellLog"] = $fensalirWellLog
        $manifest.processes["fensalirCultCache"] = $fensalirCache
        $manifest.processes["fensalirProviderSpec"] = "mimir-fensalir-daemon|Mimir Fensalir Daemon|ws://127.0.0.1:$FensalirPort/eve/deck"
        Wait-HttpHealth -Url "http://127.0.0.1:$FensalirPort/health"
    }
}

if (-not $SkipNightwing) {
    if ($DryRun) {
        Write-Host "DRY Nightwing stage and witness start on $NightwingHost"
    } else {
        scp (Join-Path $repo "tools\nw_eye_cap.py") "${NightwingHost}:/tmp/nw_eye_cap.py" | Out-Null
        scp (Join-Path $repo "tools\nw_move_hint.py") "${NightwingHost}:/tmp/nw_move_hint.py" | Out-Null
        scp (Join-Path $repo "tools\nightwing_typed_witness_publisher.py") "${NightwingHost}:/tmp/nightwing_typed_witness_publisher.py" | Out-Null
        $remoteLog = "~/.local/state/gamecult/mimir-online-verse-witness-$runId.log"
        $remotePid = "~/.local/state/gamecult/mimir-online-verse-witness-$runId.pid"
        $remoteScript = @"
mkdir -p ~/.local/state/gamecult
pkill -f '/tmp/nightwing_typed_witness_publisher.py.*--track-eyes' || true
nohup python3 /tmp/nightwing_typed_witness_publisher.py --url '$WitnessUrl' --track-eyes --track-builtin-camera --track-builtin-mic --interval 0.10 --eye-devices /dev/video2,/dev/video3 --eye-window-seconds 0.08 --tracking-stride 6 > $remoteLog 2>&1 < /dev/null &
sleep 0.5
pgrep -n -f '/tmp/nightwing_typed_witness_publisher.py.*--track-eyes' > $remotePid
cat $remotePid
"@
        $remoteB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($remoteScript))
        $nightwingPid = ssh $NightwingHost "printf '%s' '$remoteB64' | base64 -d | bash"
        $manifest.processes["nightwingWitnessPid"] = ($nightwingPid | Select-Object -First 1)
        $manifest.processes["nightwingWitnessLog"] = $remoteLog
    }
}

if (-not $SkipMoves) {
    if (-not $DryRun) {
        Get-CimInstance Win32_Process |
            Where-Object { $_.CommandLine -match "online_move_music_sync.py" } |
            ForEach-Object {
                try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {}
            }
    }

    $moveArgs = @(
        ".\tools\online_move_music_sync.py",
        "--duration", $(if ($DurationSeconds -gt 0) { "$DurationSeconds" } else { "31536000" }),
        "--out-dir", (Join-Path $runDir "move-sync"),
        "--device", "Realtek",
        "--asio-music-channels", "0,1,2,3",
        "--asio-music-source-name", "0=shotgun-mic",
        "--asio-music-source-name", "1=cardioid-mic",
        "--asio-music-source-name", "2=starfire-loopback-l",
        "--asio-music-source-name", "3=starfire-loopback-r-or-synth",
        "--asio-drain-blocks", "1024",
        "--fps", "$MoveFps",
        "--fft-size", "$MoveFftSize",
        "--onset-cooldown-seconds", "0.065",
        "--warmup-seconds", "2.0",
        "--loudness-history-seconds", "14.0",
        "--loudness-threshold", "0.86",
        "--hit-loudness-threshold", "0.84",
        "--loudness-floor", "0.006",
        "--loudness-exponent", "2.6",
        "--quiet-brightness", "0.0",
        "--max-brightness", "0.34",
        "--brightness-exponent", "0.88",
        "--score-subdivisions", "8",
        "--score-instrument-spacing", "2",
        "--score-release", "0.68",
        "--score-loudness-threshold", "0.90",
        "--score-min-loudness-gate", "0.08",
        "--score-loudness-exponent", "1.8",
        "--score-min-flux-percentile", "0.92",
        "--score-min-onset-intensity", "0.42",
        "--score-min-music-confidence", "0.30",
        "--score-min-improv-density", "0.04",
        "--score-max-improv-density", "0.22",
        "--score-target-confidence", "0.78",
        "--score-confidence-gesture-gain", "0.34",
        "--score-confidence-accent-gain", "0.55",
        "--score-min-voice-strength", "0.08",
        "--score-min-voice-body", "0.004",
        "--score-voice-release-seconds", "1.2",
        "--score-voice-decay", "0.72",
        "--score-downbeat-min-onset", "0.62",
        "--score-call-response-min-onset", "0.48",
        "--score-min-accent", "0.025",
        "--score-max-envelope", "0.78",
        "--score-ensemble-loudness-threshold", "0.96",
        "--score-ensemble-min-loudness-gate", "0.25",
        "--score-ensemble-min-onset-intensity", "0.86",
        "--score-ensemble-min-music-confidence", "0.55",
        "--score-ensemble-min-accent", "0.30",
        "--score-min-tempo-confidence", "0.0",
        "--debruijn-polyrhythm",
        "--harmonic-base", "1.5",
        "--microtonal-cents", "17",
        "--voice-glide-rate", "3.4",
        "--voice-vibrato-hz", "5.2",
        "--voice-vibrato-cents", "18.0",
        "--voice-note-hue-mix", "0.46",
        "--voice-vibrato-hue-width", "0.012",
        "--score-voice-roles", "violin-syrinx,harp-arpeggio,peck-syrinx",
        "--violin-glide-rate", "1.35",
        "--violin-release", "0.93",
        "--violin-call-seconds", "0.72",
        "--harp-slot-spacing", "5",
        "--harp-min-onset-intensity", "0.58",
        "--harp-call-seconds", "0.18",
        "--peck-min-onset-intensity", "0.34",
        "--peck-call-seconds", "0.11"
    )
    $moveArgs += "--emit-bioacoustic-realtk"
    $moveArgs += "--bioacoustic-song"
    $moveArgs += "aquasynth-formant-weaver"
    $moveArgs += "--bioacoustic-device"
    $moveArgs += "Realtek"
    $moveArgs += "--bioacoustic-gain"
    $moveArgs += "1.05"
    $moveArgs += "--bioacoustic-loop-seconds"
    $moveArgs += "0.42"
    $moveArgs += "--bioacoustic-min-interval-seconds"
    $moveArgs += "0.18"
    $moveArgs += "--bioacoustic-max-active-calls"
    $moveArgs += "3"
    foreach ($spec in $Move) {
        $moveArgs += "--move"
        $moveArgs += $spec
    }
    $python = "C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
    $moveProcess = Start-LoggedProcess -Name "move-sync" -FilePath $python -ArgumentList $moveArgs
    if ($moveProcess) { $manifest.processes["moveSyncPid"] = $moveProcess.Id }
}

if (-not $SkipRuntime) {
    $runtime = Start-LoggedProcess `
        -Name "mimir-well" `
        -FilePath "dotnet" `
        -ArgumentList @(
            "run",
            "--project",
            (Join-Path $repo "src\Mimir.Well\Mimir.Well.csproj"),
            "--",
            "--seconds", $(if ($DurationSeconds -gt 0) { "$DurationSeconds" } else { "0" }),
            "--poll-ms", "5",
            "--publish-url", $WellPublishUrl,
            "--node-id", "starfire",
            "--publish-ms", "250",
            "--sync-ms", "250",
            "--presentation-delay-ms", "2500",
            "--max-samples-per-source", "4",
            "--visual-calibration", "true",
            "--visual-calibration-ms", "250",
            "--visual-expected-leds", "38",
            "--visual-minimum-luma", "0.55",
            "--visual-setting-seconds", "0.75",
            "--visual-resweep-seconds", "12.0",
            "--capture-pages", "true",
            "--capture-ms", "250",
            "--capture-max-body-bytes", "4194304",
            "--capture-inline-bodies", "false",
            "--stream-frames", "true",
            "--stream-frame-inline-bodies", "true"
        ) `
        -Environment @{
            "MIMIR_RUNTIME_CONFIG" = $ConfigPath
            "MIMIR_SYNC_TELEMETRY_SECONDS" = "1"
        }
    if ($runtime) { $manifest.processes["mimirRuntimePid"] = $runtime.Id }
}

$manifestPath = Join-Path $runDir "supervisor.json"
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath
Write-Host "Supervisor manifest: $manifestPath"
