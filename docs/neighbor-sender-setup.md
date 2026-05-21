# Neighbor Sender Setup

## Host

- Sender: `192.168.1.84` / `DESKTOP-M9FTRLL`
- SSH: `ssh "madman's lullaby@192.168.1.84"`
- Install root: `C:\Meta\Mimir`
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
- SoundVolumeView installed through winget as `NirSoft.SoundVolumeView`.

## Sender Config

Live local config on the sender:

```text
C:\Meta\Mimir\config\localcast.json
```

Current endpoints:

```text
video:    srt://192.168.1.66:5100?mode=caller&latency=120000
focusrite srt://192.168.1.66:5101?mode=caller&latency=120000
system    srt://192.168.1.66:5102?mode=caller&latency=120000
```

Current video capture size is `1920x1080` for the interactive desktop. Do not
trust the `1024x768` size reported from SSH; that is the remote session display,
and using it captures only a slice of the actual desktop.

OBS on the receiver should add listener-mode Media Sources:

```text
video:    srt://0.0.0.0:5100?mode=listener&latency=120000&timeout=5000000
focusrite srt://0.0.0.0:5101?mode=listener&latency=120000&timeout=5000000
system    srt://0.0.0.0:5102?mode=listener&latency=120000&timeout=5000000
```

As of `2026-05-18`, the receiver OBS scene collection at
`%APPDATA%\obs-studio\basic\scenes\Untitled.json` has these sources added to
the current scene:

```text
Neighbor PC - Video
Neighbor PC - Focusrite
Neighbor PC - System Audio
```

The pre-edit backup is next to the scene file with a
`Untitled.json.localcast-backup-*` suffix.

## Desktop Launchers

Madman's desktop has:

```text
C:\Users\Madman's Lullaby\Desktop\Start Mimir Sender.cmd
C:\Users\Madman's Lullaby\Desktop\Stop Mimir Sender.cmd
```

Start launcher:

- runs `scripts\start-localcast-desktop.ps1`
- uses `config\localcast.json`
- passes the winget FFmpeg alias path explicitly
- calls `SoundVolumeView.exe` by absolute path inside PowerShell because the
  winget alias is not reliably available in interactive `cmd.exe`
- sets Windows default render to `Focusrite USB Audio\Device\Speakers\Render`
- sets Windows default capture to `Focusrite USB Audio\Device\Analogue 1 + 2\Capture`
- writes FFmpeg logs under `C:\Meta\Mimir\logs`

Stop launcher:

- runs `scripts\stop-localcast-desktop.ps1`
- stops FFmpeg processes whose command line contains `srt://`
- restores Windows default render to `Focusrite USB Audio\Device\Speakers\Render`
- restores Windows default render to `Focusrite USB Audio\Device\Speakers\Render`

## Audio Reality

FFmpeg DirectShow discovery originally exposed one hardware audio input:

```text
Analogue 1 + 2 (Focusrite USB Audio)
```

The sender FFmpeg build does not expose a native `wasapi` input device, so it
cannot directly capture primary playback by itself. `scripts\wasapi-loopback-capture.ps1`
is the direct Core Audio loopback path. It works when launched in the logged-in
console session and audio is actually rendering; SSH-only launches can open the
endpoint but receive no render packets. `scripts\capture_co_streamer_surfaces.py`
therefore schedules the WASAPI loopback leg into the interactive session and
pulls the resulting raw float PCM back for alignment.

Do not rename the loopback in config unless the replacement appears in:

```powershell
ffmpeg -hide_banner -f dshow -list_devices true -i dummy
```

If the loopback file is zero bytes, no app audio rendered during the capture
window. Do not widen OBS to unsynchronized raw audio as a workaround.

## SSH Testing Trap

Do not use an SSH-launched sender process as proof that video capture is broken.
When launched from SSH, `gdigrab` sees the remote Windows session desktop and
can fail with `Failed to capture image (error 5)`. The sender video process
must be launched from Madman's interactive desktop, normally through:

```text
C:\Users\Madman's Lullaby\Desktop\Start Mimir Sender.cmd
```

Attached SSH probes are still useful for audio devices and SRT reachability.
For example, an attached FFmpeg probe successfully transmitted audio to OBS on
ports `5101` and `5102`.
