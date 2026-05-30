# Raven And Eve Ingest

## Objective

Ingest Raven screen capture and Eve camera capture into Mimir as ordinary
network video sources, then let the existing synchronization hub place those
samples in rolling buffers beside Scarlett audio and local camera sources.

## Authority Map

- Owner: `Mimir.Runtime` owns decoded video samples and rolling-buffer
  placement.
- Inputs: Raven sends H.264/MPEG-TS over SRT to Starfire port `5200`; Eve sends
  camera video over SRT to Starfire port `5201`; Scarlett/ASIO carries audio and
  sync evidence.
- Outputs: `raven-display` and `eve-camera` emit `MimirStreamSample` frames with
  BGRA payloads and `MimirVideoFrameDescriptor` metadata.
- Derived state: network arrival timestamps are capture metadata only. Raven
  audio-in-Scarlett remains the timing witness for global alignment.
- Forbidden writers: OBS, FFmpeg sender clocks, and SRT arrival time do not own
  final sync truth.
- Cut line: no custom transport daemon and no second compositor path until this
  FFmpeg/SRT edge fails a concrete invariant.

## Starfire Receiver

Use the Raven/Eve runtime config:

```powershell
$env:MIMIR_RUNTIME_CONFIG = "E:\Projects\Mimir\config\mimir-runtime.raven-eve.example.json"
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --seconds 5
```

When both senders are active, require sample arrival:

```powershell
$env:MIMIR_RUNTIME_CONFIG = "E:\Projects\Mimir\config\mimir-runtime.raven-eve.example.json"
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --seconds 5 --require-samples
```

The config starts two FFmpeg listener processes:

- `raven-display`: `srt://0.0.0.0:5200?mode=listener&latency=120000&timeout=5000000`
- `eve-camera`: `srt://0.0.0.0:5201?mode=listener&latency=120000&timeout=5000000`

Each listener decodes to raw BGRA frames on stdout. Mimir reads exact frame
boundaries and stores those frames in the normal synchronization buffers.

## Raven Sender

Run this on Raven from the logged-in desktop session:

```powershell
E:\Projects\Mimir\scripts\start-raven-screen-capture-sender.ps1 -TargetHost 192.168.1.66 -Port 5200
```

The script uses `gdigrab` plus `h264_nvenc`, then calls Starfire's SRT listener.
Do not launch the capture itself through a blind SSH session unless the desktop
capture path has been proven there; `gdigrab` captures the interactive Windows
desktop, and service/session boundaries can turn screen capture into a black
box with excellent logs.

Raven sync signal: route Raven audio or a Raven-generated chirp into the
Scarlett alongside the microphone. Mimir should treat that audio evidence as
the authority for aligning Raven video with the rest of the program.

## Eve Sender Status

Eve is configured as a receiver lane now, and SSH is reachable, but the actual
camera producer is not implemented in this repo yet. The existing Eve app is a
view/control surface for receiving Starfire video and sending touch events; it
is not currently an AVFoundation camera broadcaster. No `ffmpeg` or obvious
camera-capture helper is installed on the iPad path checked during setup.

Current next options:

- Add AVFoundation capture to Eve and send H.264 over SRT or MPEG-TS/TCP to
  Starfire. This keeps Eve as the source owner and matches the network-producer
  model, but needs the iPad online and a native sender cut.
- Use a known iOS camera streaming app that can publish RTSP, SRT, NDI, or
  MPEG-TS, then point the `eve-camera` FFmpeg listener/receiver at that stream.
  This is fastest for a room test, but delegates camera authority to an app
  outside the Mimir/Eve codebase.
- Capture Eve over USB/Continuity on Starfire and expose it as a local camera
  source. This is operationally simple if Windows sees the device, but Eve stops
  being a networked producer and the clock-domain model changes.

The coherent default is the AVFoundation sender, because it gives Mimir one
clean producer per physical source and leaves sync ownership explicit.

## Smoke Test

The raw-video source has a local synthetic smoke that does not require Raven or
Eve:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --ffmpeg-rawvideo-source-smoke
```

That smoke starts FFmpeg with `testsrc2`, decodes BGRA frames through stdout,
and verifies the sample metadata and payload size that Mimir receives.
