# Raven And Eve Ingest

## Objective

Ingest Raven screen capture plus Eve camera and microphone capture into Mimir,
then let the existing synchronization hub place those samples in rolling buffers
beside Scarlett audio and local camera sources.

## Authority Map

- Owner: `Mimir.Runtime` owns decoded samples and rolling-buffer placement.
- Inputs: Raven currently sends H.264/MPEG-TS over the SRT bridge to Starfire
  port `5200`; Eve currently sends camera frame-events over WebSocket port
  `8793`; Eve currently sends microphone frame-events over WebSocket port
  `8794`; Scarlett/ASIO carries audio and sync evidence. The target Verse shape
  is CultMesh reliable UDP: typed stream-frame envelopes plus bounded body
  shards for media, with compact state/cursors/backpressure beside them.
- Outputs: `raven-display`, `eve-camera`, and `eve-mic` emit
  `MimirStreamSample` records with video or audio descriptors.
- Derived state: network arrival timestamps are capture metadata only. Raven
  audio-in-Scarlett remains the timing witness for global alignment.
- Forbidden writers: OBS, FFmpeg sender clocks, WebSocket receive time, SRT
  arrival time, and CultMesh packet arrival time do not own final sync truth.
- Cut line: EveCanvas owns Eve's sensors directly. External iOS streaming apps
  are fallback tools, not the current architecture. SRT/WebSocket bridge
  transports are diagnostics and compatibility edges once the reliable-UDP
  Verse media lane exists.

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

The config starts one FFmpeg listener and two Eve sensor receiver processes:

- `raven-display`: `srt://0.0.0.0:5200?mode=listener&latency=120000&timeout=5000000`
- `eve-camera`: `ws://0.0.0.0:8793/eve/camera`
- `eve-mic`: `ws://0.0.0.0:8794/eve/mic`

The Raven listener decodes to raw BGRA frames on stdout. The Eve receivers emit
typed JSON frame-events from EveCanvas camera/mic packets. Mimir stores all of
them in the normal synchronization buffers.

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

## Eve Sensor Sender

EveCanvas now starts native AVFoundation capture after permission grants:

- Camera frames are sampled from `AVCaptureVideoDataOutput`, JPEG-compressed,
  wrapped as `video-frame` events for `eve-camera`, and sent to
  `ws://192.168.1.66:8793/eve/camera`.
- Microphone blocks are captured with `AVAudioEngine`, packed as interleaved
  Float32 samples, wrapped as `audio-block` events for `eve-mic`, and sent to
  `ws://192.168.1.66:8794/eve/mic`.

This is an inspectable first transport, not the final high-throughput shape.
When cadence or bandwidth becomes the bottleneck, replace the JSON/base64 event
transport with CultMesh reliable-UDP media frames and body shards, optionally
carrying VideoToolbox H.264 or another explicit media payload. Preserve the same
source ownership and Mimir runtime sample contract.

Deploy EveCanvas after staging:

```powershell
powershell -ExecutionPolicy Bypass -File E:\Projects\Eve\scripts\stage-to-eve.ps1
ssh eve "cd /var/mobile/Projects/Eve && export THEOS=/var/theos && make package install && uiopen --bundleid org.gamecult.evecanvas"
```

## Smoke Test

The raw-video source has a local synthetic smoke that does not require Raven or
Eve:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --ffmpeg-rawvideo-source-smoke
```

That smoke starts FFmpeg with `testsrc2`, decodes BGRA frames through stdout,
and verifies the sample metadata and payload size that Mimir receives.

The Eve sensor receiver can be checked without launching the whole runtime:

```powershell
dotnet run --project .\src\Mimir.EveSensorReceiver\Mimir.EveSensorReceiver.csproj -- --port 8793 --path /eve/camera --source-id eve-camera --type video-frame
```

Periwinkle's Android Eve client publishes binary CultMesh sensor observations:

```powershell
dotnet run --project .\src\Mimir.EveSensorReceiver\Mimir.EveSensorReceiver.csproj -- --port 8796 --path /eve/periwinkle --source-id periwinkle-accelerometer --type cultmesh-observation
```

The receiver accepts `mimir.eve_sensor_observation.v1` MessagePack packets on
that lane. The device remains an observation source; Mimir owns synchronization
and interpretation.

The same receiver also accepts `mimir.eve_media_observation.v1` MessagePack
packets. Periwinkle publishes low-rate camera luma frames as
`periwinkle-camera` and microphone PCM blocks as `periwinkle-mic` on the
CultMesh observation lane. The legacy iPad Eve camera and mic uplinks have
matching binary media document support while their existing `eve-camera` and
`eve-mic` ports remain usable.
