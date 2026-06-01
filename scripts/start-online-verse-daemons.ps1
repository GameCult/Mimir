param(
    [string]$NightwingHost = "nightwing",
    [string]$WitnessUrl = "ws://192.168.1.66:8796/eve/periwinkle",
    [string]$SubscribeUrl = "ws://127.0.0.1:8796/eve/periwinkle/subscribe",
    [string]$ConfigPath = "E:\Projects\Mimir\config\mimir-runtime.well.local.json",
    [string]$WellPublishUrl = "ws://127.0.0.1:8796/eve/periwinkle",
    [string]$RecorderRoot = "C:\Users\Meta\Videos\Mimir\VerseCaptures",
    [int]$DurationSeconds = 0,
    [int]$MoveFps = 120,
    [int]$MoveFftSize = 256,
    [string[]]$Move = @(
        "move-00-07-04-a3-97-72-usb=/dev/hidraw1:#35ff6c",
        "move-00-07-04-a6-be-5f=/dev/hidraw2:#ff2a00",
        "move-00-06-f5-23-e2-d1=/dev/hidraw3:#00a8ff"
    ),
    [switch]$SkipRuntime,
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

$manifest = [ordered]@{
    kind = "mimir.online_verse_daemon_supervisor.v1"
    runId = $runId
    runDir = $runDir
    witnessUrl = $WitnessUrl
    subscribeUrl = $SubscribeUrl
    wellPublishUrl = $WellPublishUrl
    recorderRoot = $RecorderRoot
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
            ($_.CommandLine -match "Mimir.EveSensorReceiver" -and $_.CommandLine -match "--port 8796") -or
            ($_.CommandLine -match "Mimir.VerseRecorder") -or
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
        "--port", "8796",
        "--path", "/eve/periwinkle",
        "--subscribe-path", "/eve/periwinkle/subscribe",
        "--source-id", "nightwing-witness",
        "--type", "cultmesh-observation"
    )
if ($receiver) { $manifest.processes["verseRelayPid"] = $receiver.Id }

Start-Sleep -Milliseconds 900

$recorderArgs = @(
    "run",
    "--project",
    (Join-Path $repo "src\Mimir.VerseRecorder\Mimir.VerseRecorder.csproj"),
    "--",
    "--url", $SubscribeUrl,
    "--out-dir", $RecorderRoot
)
if ($DurationSeconds -gt 0) {
    $recorderArgs += "--seconds"
    $recorderArgs += "$DurationSeconds"
}
$recorder = Start-LoggedProcess -Name "verse-recorder" -FilePath "dotnet" -ArgumentList $recorderArgs
if ($recorder) { $manifest.processes["verseRecorderPid"] = $recorder.Id }

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
        "--quiet-brightness", "0.008",
        "--max-brightness", "0.72",
        "--brightness-exponent", "0.9",
        "--debruijn-polyrhythm",
        "--harmonic-base", "1.5",
        "--microtonal-cents", "17"
    )
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
            "--seconds", $(if ($DurationSeconds -gt 0) { "$DurationSeconds" } else { "31536000" }),
            "--poll-ms", "5",
            "--publish-url", $WellPublishUrl,
            "--node-id", "starfire",
            "--publish-ms", "250",
            "--sync-ms", "250",
            "--presentation-delay-ms", "2500",
            "--max-samples-per-source", "4"
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
