# Neighbor Sender Setup

## Host

- Sender: `192.168.1.84` / `DESKTOP-M9FTRLL`
- SSH: `ssh "madman's lullaby@192.168.1.84"`
- Install root: `C:\Meta\LocalCastBridge`
- Receiver OBS workstation: `192.168.1.66`

## Installed State

- FFmpeg installed through winget as `Gyan.FFmpeg 8.1.1`.
- FFmpeg alias path observed:
  - `C:\Users\Madman's Lullaby\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe`
- NVIDIA GPU observed:
  - `NVIDIA GeForce RTX 4060 Ti`
  - driver `591.86`
- FFmpeg reports:
  - `srt` input/output protocol
  - `h264_nvenc` encoder

## Sender Config

Live local config on the sender:

```text
C:\Meta\LocalCastBridge\config\localcast.json
```

Current endpoints:

```text
video:    srt://192.168.1.66:5100?mode=caller&latency=120000
focusrite srt://192.168.1.66:5101?mode=caller&latency=120000
```

OBS on the receiver should add listener-mode Media Sources:

```text
video:    srt://0.0.0.0:5100?mode=listener&latency=120000&timeout=5000000
focusrite srt://0.0.0.0:5101?mode=listener&latency=120000&timeout=5000000
```

## Desktop Launchers

Madman's desktop has:

```text
C:\Users\Madman's Lullaby\Desktop\Start LocalCast Sender.cmd
C:\Users\Madman's Lullaby\Desktop\Stop LocalCast Sender.cmd
```

Start launcher:

- runs `scripts\sender-start.ps1`
- uses `config\localcast.json`
- passes the winget FFmpeg alias path explicitly

Stop launcher:

- runs `scripts\sender-stop.ps1`
- stops FFmpeg processes whose command line contains `srt://`

## Audio Reality

FFmpeg DirectShow discovery currently exposes one audio input:

```text
Analogue 1 + 2 (Focusrite USB Audio)
```

This is a real audio source, but it is not the same thing as system-output loopback. The installed FFmpeg build exposes `dshow` and `gdigrab`, not `wasapi`, so capturing game/desktop output as its own source still needs a virtual or hardware loopback device such as a mixer/interface loopback, Stereo Mix if available, VB-Cable, or Voicemeeter.

Do not pretend desktop audio exists until a real capture device appears in:

```powershell
ffmpeg -hide_banner -f dshow -list_devices true -i dummy
```

