param(
    [switch]$Headless,
    [int]$HeadlessWidth = 1280,
    [int]$HeadlessHeight = 720,
    [string]$CaptureFrame = "",
    [string]$NightwingHost = "nightwing",
    [switch]$StartRavenSender,
    [string]$RavenHost = "madman's lullaby@192.168.1.84",
    [string]$RavenRepo = "C:\Meta\Mimir",
    [string]$StarfireHost = "192.168.1.66",
    [string]$WitnessUrl = "ws://192.168.1.66:8796/eve/periwinkle",
    [string]$ConfigPath = "E:\Projects\Mimir\config\mimir-runtime.stream-proof.local.json",
    [switch]$ProgramOutput,
    [string]$SharedTextureName = "Global\MimirFensalirProgramTexture",
    [string]$SharedFenceName = "Global\MimirFensalirProgramFence",
    [int]$SharedTextureRingCount = 1,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repo "artifacts\runtime\stream-proof"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

if ($DryRun) {
    Write-Host "MIMIR_RUNTIME_CONFIG=$ConfigPath"
    if ($ProgramOutput) {
        Write-Host "FENSALIR_PROGRAM_OUTPUT_D3D12=1"
        Write-Host "FENSALIR_PROGRAM_OUTPUT_NAME=$SharedTextureName"
        Write-Host "FENSALIR_PROGRAM_OUTPUT_FENCE_NAME=$SharedFenceName"
        Write-Host "FENSALIR_PROGRAM_OUTPUT_RING_COUNT=$SharedTextureRingCount"
    }
    Write-Host "Would stage Nightwing witness tools to $NightwingHost and start --track-eyes publisher at $WitnessUrl"
    if ($StartRavenSender) {
        Write-Host "Would start Raven muxed A/V sender on ${RavenHost}: ${RavenRepo}\scripts\start-raven-av-sender.ps1 -TargetHost $StarfireHost -Port 5200"
    }
    Write-Host "Would start local Raven A/V demux: scripts\start-raven-av-demux.ps1"
    Write-Host "Would launch: dotnet run --project $repo\src\Mimir.App\Mimir.App.csproj"
    Write-Host "Program layout: raven-display full-frame, kiyo-pro-rgb picture-in-picture"
    exit 0
}

$kiyo = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match "VID_1532&PID_0E05&MI_00" } | Select-Object -First 1
$leap = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match "VID_F182&PID_0003&MI_00" } | Select-Object -First 1

if (-not $kiyo) {
    throw "Kiyo Pro camera interface is not present. Stream proof needs the Kiyo AR program view."
}

if (-not $leap) {
    throw "LeapUVC camera interface is not present. Replug/wake the Leap before launching this profile."
}

$nightwingCheck = ssh $NightwingHost "test -e /dev/video2 -a -e /dev/video3 && command -v python3 >/dev/null && echo ok" 2>$null
if ($LASTEXITCODE -ne 0 -or $nightwingCheck -notmatch "ok") {
    throw "Nightwing does not expose /dev/video2 + /dev/video3 with python3 available."
}

scp (Join-Path $repo "tools\nw_eye_cap.py") "${NightwingHost}:/tmp/nw_eye_cap.py" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not stage nw_eye_cap.py on Nightwing."
}
scp (Join-Path $repo "tools\nw_move_hint.py") "${NightwingHost}:/tmp/nw_move_hint.py" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not stage nw_move_hint.py on Nightwing."
}
scp (Join-Path $repo "tools\nightwing_typed_witness_publisher.py") "${NightwingHost}:/tmp/nightwing_typed_witness_publisher.py" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not stage nightwing_typed_witness_publisher.py on Nightwing."
}

$receiverRunning = Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -match "Mimir.EveSensorReceiver" -and $_.CommandLine -match "--port 8796" } |
    Select-Object -First 1
if (-not $receiverRunning) {
    $receiverOut = Join-Path $logDir "nightwing-witness-receiver.out.log"
    $receiverErr = Join-Path $logDir "nightwing-witness-receiver.err.log"
    Start-Process -WindowStyle Hidden -FilePath "dotnet" -ArgumentList @(
        "run",
        "--no-build",
        "--project",
        (Join-Path $repo "src\Mimir.EveSensorReceiver\Mimir.EveSensorReceiver.csproj"),
        "--",
        "--port",
        "8796",
        "--path",
        "/eve/periwinkle",
        "--source-id",
        "nightwing-witness",
        "--type",
        "cultmesh-observation"
    ) -RedirectStandardOutput $receiverOut -RedirectStandardError $receiverErr
    Start-Sleep -Milliseconds 800
}

$remotePublisher = 'ps -eo pid=,comm=,args= | awk ''$2=="python3" && $0 ~ /\/tmp\/nightwing_typed_witness_publisher.py/ && $0 ~ /--track-eyes/ {print $1}'' | xargs -r kill; nohup python3 /tmp/nightwing_typed_witness_publisher.py --url ''' + $WitnessUrl + ''' --track-eyes --interval 0.12 > /tmp/mimir-nightwing-stream-proof.log 2>&1 &'
ssh $NightwingHost $remotePublisher | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not start Nightwing typed witness publisher."
}

& (Join-Path $repo "scripts\start-raven-av-demux.ps1")

if ($StartRavenSender) {
    $taskName = "MimirRavenAvSender5200"
    $ravenSender = "${RavenRepo}\scripts\start-raven-av-sender.ps1"
    $remoteCmd = "${RavenRepo}\scripts\run-raven-av-5200.cmd"
    $taskTime = (Get-Date).AddMinutes(1).ToString("HH:mm")
    ssh $RavenHost "cmd /c echo powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ravenSender -TargetHost $StarfireHost -Port 5200 ^> $remoteCmd" | Out-Null
    ssh $RavenHost "schtasks /Delete /TN $taskName /F" | Out-Null
    ssh $RavenHost "schtasks /Create /TN $taskName /SC ONCE /ST $taskTime /TR $remoteCmd /F /IT" | Out-Host
    ssh $RavenHost "schtasks /Run /TN $taskName" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not start Raven muxed A/V sender task on $RavenHost."
    }
    Start-Sleep -Milliseconds 1500
}

$env:MIMIR_RUNTIME_CONFIG = $ConfigPath
if ($ProgramOutput) {
    $env:FENSALIR_PROGRAM_OUTPUT_D3D12 = "1"
    $env:FENSALIR_PROGRAM_OUTPUT_NAME = $SharedTextureName
    $env:FENSALIR_PROGRAM_OUTPUT_FENCE_NAME = $SharedFenceName
    $env:FENSALIR_PROGRAM_OUTPUT_RING_COUNT = [string]$SharedTextureRingCount
}
$args = @("run", "--project", (Join-Path $repo "src\Mimir.App\Mimir.App.csproj"))
if ($Headless) {
    $args += "--"
    $args += "--headless"
    $args += "--headless-width"
    $args += "$HeadlessWidth"
    $args += "--headless-height"
    $args += "$HeadlessHeight"
    if ($CaptureFrame) {
        $args += "--capture-frame"
        $args += $CaptureFrame
    }
}

Write-Host "MIMIR_RUNTIME_CONFIG=$ConfigPath"
Write-Host "Starting stream proof: Leap + Kiyo Pro + Raven screen/audio, Nightwing Eye/Move witness claims"
if ($ProgramOutput) {
    Write-Host "Program output texture: $SharedTextureName"
}
Write-Host "Program layout: raven-display full-frame, kiyo-pro-rgb picture-in-picture"
dotnet @args
