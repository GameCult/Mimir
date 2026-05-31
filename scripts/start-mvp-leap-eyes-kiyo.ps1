param(
    [switch]$Headless,
    [int]$HeadlessWidth = 1280,
    [int]$HeadlessHeight = 720,
    [string]$CaptureFrame = "",
    [string]$NightwingHost = "nightwing",
    [string]$WitnessUrl = "ws://192.168.1.66:8796/eve/periwinkle",
    [string]$ConfigPath = "E:\Projects\Mimir\config\mimir-runtime.mvp-leap-eyes-kiyo.local.json"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repo "artifacts\runtime\mvp-leap-eyes-kiyo"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Test-CommandOk {
    param([string[]]$Command, [string]$Name)
    $process = Start-Process -FilePath $Command[0] -ArgumentList $Command[1..($Command.Length - 1)] -NoNewWindow -Wait -PassThru -RedirectStandardOutput (Join-Path $logDir "$Name.out.log") -RedirectStandardError (Join-Path $logDir "$Name.err.log")
    return $process.ExitCode -eq 0
}

$kiyo = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match "VID_1532&PID_0E05&MI_00" } | Select-Object -First 1
$leap = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match "VID_F182&PID_0003&MI_00" } | Select-Object -First 1

if (-not $kiyo) {
    throw "Kiyo Pro camera interface is not present. MVP needs Kiyo Pro RGB."
}

if (-not $leap) {
    throw "LeapUVC camera interface is not present. Replug/wake the Leap before launching this MVP profile."
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

$remotePublisher = "nohup python3 /tmp/nightwing_typed_witness_publisher.py --url '$WitnessUrl' --track-eyes --interval 0.12 > /tmp/mimir-nightwing-mvp-witness.log 2>&1 &"
ssh $NightwingHost $remotePublisher | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not start Nightwing typed witness publisher."
}

$env:MIMIR_RUNTIME_CONFIG = $ConfigPath
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
Write-Host "Starting MVP: Starfire Leap + Kiyo Pro, Nightwing Eye/Move witness claims"
dotnet @args
