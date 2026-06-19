param(
    [string]$ObsExe = "C:\Program Files\obs-studio\bin\64bit\obs64.exe",
    [string]$ObsConfigRoot = "$env:APPDATA\obs-studio",
    [int[]]$ExpectedUdpPorts = @(5204, 17874),
    [int]$StartupWaitSeconds = 30,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

function Stop-Obs {
    $processes = Get-Process obs64,obs-browser-page,obs-ffmpeg-mux -ErrorAction SilentlyContinue
    if (-not $processes) {
        return
    }

    & "$env:SystemRoot\System32\taskkill.exe" /IM obs64.exe /IM obs-browser-page.exe /IM obs-ffmpeg-mux.exe /T /F | Write-Host

    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 500
        $remaining = Get-Process obs64,obs-browser-page,obs-ffmpeg-mux -ErrorAction SilentlyContinue
        if (-not $remaining) {
            return
        }
    } while ((Get-Date) -lt $deadline)

    $remaining | ForEach-Object {
        try {
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
        } catch {
            Write-Host "Failed to stop lingering $($_.ProcessName) PID $($_.Id): $($_.Exception.Message)"
        }
    }

    Start-Sleep -Seconds 1
}

function Remove-ObsCrashSentinel {
    param([string]$ConfigRoot)

    $sentinel = Join-Path $ConfigRoot ".sentinel"
    if (-not (Test-Path -LiteralPath $sentinel)) {
        return
    }

    $resolvedSentinel = Resolve-Path -LiteralPath $sentinel
    $resolvedConfigRoot = Resolve-Path -LiteralPath $ConfigRoot
    $expected = Join-Path $resolvedConfigRoot.Path ".sentinel"
    if ($resolvedSentinel.Path -ne $expected) {
        throw "Refusing to remove unexpected OBS sentinel path '$($resolvedSentinel.Path)'."
    }

    Remove-Item -LiteralPath $resolvedSentinel.Path -Recurse -Force
    Write-Host "Removed OBS crash sentinel: $($resolvedSentinel.Path)"
}

function Get-UdpListenerLines {
    param([int[]]$Ports)

    $patterns = $Ports | ForEach-Object { ":$_" }
    netstat -ano -p udp | Where-Object {
        $line = $_
        $patterns | Where-Object { $line -match [regex]::Escape($_) }
    }
}

function Get-ObservedUdpPorts {
    param([int[]]$Ports)

    $listeners = @(Get-UdpListenerLines -Ports $Ports)
    $observed = @{}
    foreach ($port in $Ports) {
        $pattern = ":\s*$port\s+"
        if ($listeners | Where-Object { $_ -match $pattern }) {
            $observed[$port] = $true
        }
    }

    [pscustomobject]@{
        Lines = $listeners
        Ports = $observed
    }
}

function Get-LatestObsLog {
    param([string]$ConfigRoot)

    $logRoot = Join-Path $ConfigRoot "logs"
    if (-not (Test-Path -LiteralPath $logRoot)) {
        return $null
    }

    Get-ChildItem -LiteralPath $logRoot -Filter "*.txt" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not (Test-Path -LiteralPath $ObsExe)) {
    throw "OBS executable was not found at '$ObsExe'."
}
if (-not (Test-Path -LiteralPath $ObsConfigRoot)) {
    throw "OBS config root was not found at '$ObsConfigRoot'."
}

Stop-Obs
Remove-ObsCrashSentinel -ConfigRoot $ObsConfigRoot

if ($NoStart) {
    Write-Host "OBS stopped and crash sentinel cleared."
    return
}

$workingDirectory = Split-Path -Parent $ObsExe
Start-Process -FilePath $ObsExe -WorkingDirectory $workingDirectory
Write-Host "Started OBS: $ObsExe"

$deadline = (Get-Date).AddSeconds($StartupWaitSeconds)
$listeners = @()
do {
    Start-Sleep -Seconds 1
    $obs = Get-Process obs64 -ErrorAction SilentlyContinue
    if (-not $obs) {
        throw "OBS did not remain running after startup."
    }

    $observed = Get-ObservedUdpPorts -Ports $ExpectedUdpPorts
    $listeners = @($observed.Lines)
    $missingPorts = @($ExpectedUdpPorts | Where-Object { -not $observed.Ports.ContainsKey($_) })
    if ($missingPorts.Count -eq 0) {
        break
    }
} while ((Get-Date) -lt $deadline)

if ($missingPorts.Count -ne 0) {
    $latestLog = Get-LatestObsLog -ConfigRoot $ObsConfigRoot
    if ($latestLog) {
        Write-Host "Newest OBS log: $($latestLog.FullName) ($($latestLog.Length) bytes)"
        if ($latestLog.Length -gt 0) {
            Get-Content -LiteralPath $latestLog.FullName -Tail 40 | Write-Host
        }
    }
    throw "OBS is running, but expected UDP listener ports are missing: $($missingPorts -join ', ')."
}

Write-Host "OBS process:"
$obs | Select-Object Id, ProcessName, StartTime, Responding | Format-Table -AutoSize
Write-Host "Observed Muninn UDP listeners:"
$listeners | ForEach-Object { Write-Host $_ }
