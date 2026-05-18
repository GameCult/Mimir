param(
    [string]$VoicemeeterExe = "C:\Program Files (x86)\VB\Voicemeeter\voicemeeter_x64.exe",
    [string]$VoicemeeterRemoteDll = "C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll",
    [int[]]$CandidateStrips = @(7, 5, 6, 3, 4),
    [int[]]$CandidateBuses = @(7, 6, 5, 4, 3, 2, 1, 0),
    [string]$LogPath = "C:\Meta\LocalCastBridge\logs\voicemeeter-routing.log",
    [switch]$AlsoMonitorA1
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force (Split-Path -Parent $LogPath) | Out-Null
"$(Get-Date -Format o) configuring Voicemeeter routing" | Set-Content -LiteralPath $LogPath -Encoding UTF8

function Log-Line {
    param([string]$Line)
    $Line | Tee-Object -FilePath $LogPath -Append
}

if (-not (Test-Path -LiteralPath $VoicemeeterRemoteDll)) {
    throw "Voicemeeter Remote DLL not found: $VoicemeeterRemoteDll"
}

if (-not (Get-Process -Name "voicemeeter_x64" -ErrorAction SilentlyContinue)) {
    if (-not (Test-Path -LiteralPath $VoicemeeterExe)) {
        throw "Voicemeeter executable not found: $VoicemeeterExe"
    }
    Start-Process -FilePath $VoicemeeterExe
    Start-Sleep -Seconds 3
}

$escapedDll = $VoicemeeterRemoteDll.Replace("\", "\\")
$source = @"
using System;
using System.Runtime.InteropServices;

public static class LocalCastVoicemeeterRemote {
    [DllImport("$escapedDll", CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_Login();

    [DllImport("$escapedDll", CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_Logout();

    [DllImport("$escapedDll", CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_SetParameterFloat(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        float value
    );

    [DllImport("$escapedDll", CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_GetVoicemeeterType(ref int voicemeeterType);
}
"@

Add-Type -TypeDefinition $source

function Set-VMFloat {
    param(
        [string]$Name,
        [float]$Value
    )
    return [LocalCastVoicemeeterRemote]::VBVMR_SetParameterFloat($Name, $Value)
}

$login = [LocalCastVoicemeeterRemote]::VBVMR_Login()
try {
    if ($login -lt 0) {
        throw "Voicemeeter Remote login failed with code $login"
    }
    Start-Sleep -Milliseconds 250

    $voicemeeterType = 0
    [void][LocalCastVoicemeeterRemote]::VBVMR_GetVoicemeeterType([ref]$voicemeeterType)
    Log-Line "Voicemeeter type: $voicemeeterType"

    $routed = @()
    foreach ($strip in $CandidateStrips) {
        $b3 = Set-VMFloat -Name "Strip[$strip].B3" -Value 1.0
        $mute = Set-VMFloat -Name "Strip[$strip].Mute" -Value 0.0
        Log-Line "Strip[$strip].B3 -> $b3"
        Log-Line "Strip[$strip].Mute -> $mute"
        if ($b3 -ge 0) {
            $routed += "Strip[$strip].B3"
        }
        if ($AlsoMonitorA1) {
            $a1 = Set-VMFloat -Name "Strip[$strip].A1" -Value 1.0
            Log-Line "Strip[$strip].A1 -> $a1"
        }
        if ($mute -ge 0) {
            $gain = Set-VMFloat -Name "Strip[$strip].Gain" -Value 0.0
            Log-Line "Strip[$strip].Gain -> $gain"
        }
    }

    foreach ($bus in $CandidateBuses) {
        $mute = Set-VMFloat -Name "Bus[$bus].Mute" -Value 0.0
        $gain = Set-VMFloat -Name "Bus[$bus].Gain" -Value 0.0
        Log-Line "Bus[$bus].Mute -> $mute"
        Log-Line "Bus[$bus].Gain -> $gain"
    }

    if ($routed.Count -eq 0) {
        throw "No candidate Voicemeeter strips accepted B3 routing"
    }

    Log-Line "Voicemeeter B3 routing enabled: $($routed -join ', ')"
}
finally {
    [void][LocalCastVoicemeeterRemote]::VBVMR_Logout()
}
