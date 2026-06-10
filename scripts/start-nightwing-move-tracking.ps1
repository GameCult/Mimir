param(
    [string]$NightwingHost = "nightwing",
    [int]$Port = 8796,
    [string]$StarfireHost = "192.168.1.66",
    [string]$Path = "/eve/periwinkle",
    [string]$EyeDevices = "/dev/video2,/dev/video3",
    [string[]]$MoveLight = @(
        "move-usb=/dev/hidraw1:#35ff6c"
    ),
    [string]$RecorderRoot = "C:\Users\Meta\Videos\Mimir\VerseCaptures",
    [switch]$SkipRecorder,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $repo "artifacts\runtime\nightwing-move-tracking-$runId"
$witnessUrl = "ws://$StarfireHost`:$Port$Path"
$subscribeUrl = "ws://127.0.0.1:$Port$Path/subscribe"

New-Item -ItemType Directory -Force -Path $runDir | Out-Null
New-Item -ItemType Directory -Force -Path $RecorderRoot | Out-Null

function Start-LoggedProcess {
    param(
        [string]$Name,
        [string]$FilePath,
        [string[]]$ArgumentList
    )

    $stdout = Join-Path $runDir "$Name.out.log"
    $stderr = Join-Path $runDir "$Name.err.log"
    if ($DryRun) {
        Write-Host "DRY $Name`: $FilePath $($ArgumentList -join ' ')"
        return $null
    }

    $process = Start-Process `
        -FilePath $FilePath `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $repo `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru
    Set-Content -Path (Join-Path $runDir "$Name.pid") -Value $process.Id
    Write-Host "$Name pid=$($process.Id) stdout=$stdout stderr=$stderr"
    return $process
}

function Wait-Health {
    param([string]$Url)
    if ($DryRun) {
        Write-Host "DRY wait health: $Url"
        return
    }

    $deadline = (Get-Date).AddSeconds(45)
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

Write-Host "Nightwing Move tracking run: $runDir"
Write-Host "Witness URL: $witnessUrl"

if (-not $DryRun) {
    Get-CimInstance Win32_Process |
        Where-Object {
            ($_.CommandLine -match "Mimir.EveSensorReceiver" -and $_.CommandLine -match "--port $Port") -or
            ($_.CommandLine -match "Mimir.VerseRecorder" -and $_.CommandLine -match "$Port$Path/subscribe")
        } |
        ForEach-Object {
            try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {}
        }
}

$receiver = Start-LoggedProcess `
    -Name "periwinkle-receiver" `
    -FilePath "dotnet" `
    -ArgumentList @(
        "run",
        "--project",
        (Join-Path $repo "src\Mimir.EveSensorReceiver\Mimir.EveSensorReceiver.csproj"),
        "--",
        "--port", "$Port",
        "--path", $Path,
        "--subscribe-path", "$Path/subscribe",
        "--source-id", "nightwing-witness",
        "--type", "cultmesh-observation"
    )

if ($receiver) {
    Wait-Health -Url "http://127.0.0.1:$Port/health"
}

if (-not $SkipRecorder) {
    Start-LoggedProcess `
        -Name "verse-recorder" `
        -FilePath "dotnet" `
        -ArgumentList @(
            "run",
            "--project",
            (Join-Path $repo "src\Mimir.VerseRecorder\Mimir.VerseRecorder.csproj"),
            "--",
            "--url", $subscribeUrl,
            "--out-dir", $RecorderRoot
        ) | Out-Null
}

if ($DryRun) {
    Write-Host "DRY Nightwing stage and start on $NightwingHost"
} else {
    scp (Join-Path $repo "tools\nw_eye_cap.py") "${NightwingHost}:/tmp/nw_eye_cap.py" | Out-Null
    scp (Join-Path $repo "tools\nw_move_hint.py") "${NightwingHost}:/tmp/nw_move_hint.py" | Out-Null
    scp (Join-Path $repo "tools\nightwing_typed_witness_publisher.py") "${NightwingHost}:/tmp/nightwing_typed_witness_publisher.py" | Out-Null

    $remoteLog = "~/.local/state/gamecult/mimir-nightwing-move-tracking-$runId.log"
    $remotePid = "~/.local/state/gamecult/mimir-nightwing-move-tracking-$runId.pid"
    $moveArgs = ($MoveLight | ForEach-Object { "--move-light '$($_ -replace "'", "'\''")'" }) -join " "
    $remoteScript = @"
mkdir -p ~/.local/state/gamecult
pkill -f '/tmp/nightwing_typed_witness_publisher.py.*--track-eyes' || true
nohup python3 /tmp/nightwing_typed_witness_publisher.py --url '$witnessUrl' --track-eyes --track-builtin-camera --track-builtin-mic --interval 0.10 --eye-devices '$EyeDevices' --eye-window-seconds 0.08 --tracking-stride 6 $moveArgs > $remoteLog 2>&1 < /dev/null &
sleep 0.5
pgrep -n -f '/tmp/nightwing_typed_witness_publisher.py.*--track-eyes' > $remotePid
cat $remotePid
"@
    $remoteB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($remoteScript))
    $nightwingPid = ssh $NightwingHost "printf '%s' '$remoteB64' | base64 -d | bash"
    Set-Content -Path (Join-Path $runDir "nightwing-witness.pid") -Value ($nightwingPid | Select-Object -First 1)
    Set-Content -Path (Join-Path $runDir "nightwing-witness.logpath.txt") -Value $remoteLog
    Write-Host "nightwing-witness pid=$($nightwingPid | Select-Object -First 1) log=$remoteLog"
}

$manifest = [ordered]@{
    kind = "mimir.nightwing_move_tracking_supervisor.v1"
    runId = $runId
    runDir = $runDir
    witnessUrl = $witnessUrl
    subscribeUrl = $subscribeUrl
    eyeDevices = $EyeDevices
    moveLights = $MoveLight
    startedAt = (Get-Date).ToString("o")
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $runDir "supervisor.json")
Write-Host "Supervisor manifest: $(Join-Path $runDir "supervisor.json")"
