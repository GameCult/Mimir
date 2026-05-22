# Fresh Workspace Handoff

This is the short re-entry packet for `E:\Projects\Mimir`.

Do not trust this file for exact branch, HEAD, or dirty state. Ask git.

## Rehydrate

```powershell
git status --short --branch
git log --oneline -5
Get-Content .\state\map.yaml
Get-Content .\notes\current-system-map.md
Get-Content .\docs\implementation-plan.md
Get-Content .\state\evidence.jsonl -Tail 8
```

## Current Shape

- Public brand/Face is Mimir.
- Mimir's Face doctrine lives in `docs/mimir-face.md`.
- VoidBot persona/state live under `.voidbot/voice/` and `.voidbot/state/`.
  Commit `.voidbot` dirt in a separate state commit whenever it appears.
- V1 still has FFmpeg/SRT/OBS bridge utilities for LAN ingest.
- The live stream app is C# plus Aquarium: `Mimir.slnx` contains
  `src/Mimir.App` and `src/Mimir.Runtime`.
- `src/Mimir.App` hosts Aquarium Engine as the windowing/rendering/D3D12 bridge.
- `src/Mimir.Runtime` owns `MimirSynchronizationHub`, configurable five-second
  rolling buffers, stream descriptors, source adapters, direct native ingest,
  audio chirplet delay estimation, provisional aligned audio frames, and the
  video capture driver seam.
- Local six-camera ingest should use direct driver adapters. Process-backed
  sources are bridge/network edges only.
- Leap stereo IR is the first timing-camera candidate for direct ingest.
- `native/reservoir` owns the lower shared-edge typed-handle invariant for
  Aquarium/Faust binding work.
- Aquarium owns production GPU fusion, UI, and Spout2 publication.
- Faust/native DSP owns hot audio alignment, separation, spatialization, and
  synchronized stems.
- OBS receives final program surfaces; it does not own synchronization.

## Current Pressure

- Implement the first concrete Leap direct capture driver and measure cadence.
- Add remaining camera drivers through `IMimirVideoCaptureDriver`.
- Add native audio capture workers for mic, loopback, and network audio feeds.
- Turn the current loopback-referenced chirplet measurement into the real
  synchronization actuator: smooth delay/SRO, then drive fractional delay and
  variable-rate resampling.
- Bind Aquarium UI to stream health, buffer depth, timestamps, settings, and
  output management.
- Keep PowerShell/FFmpeg/SRT as bridge utilities until native program output
  replaces them.
- Kiyo Pro has two UVC extension units. Moving it to a motherboard USB3 port
  changed the descriptor path to `root_hub30` / `bcdUSB=0x0320` and exposed
  720p/1080p YUY2/MJPG/H264/NV12 at 60 fps, but Windows still reports
  `UsbHighSpeed` rather than `UsbSuperSpeed`; measured cadence remains about
  25 fps across all tested formats. Verify cable/port SuperSpeed before poking
  writable vendor selectors.
- First simultaneous local pull only saw LeapUVC, Kiyo Pro, and one PS3 Eye.
  The KS multi-stream harness and raw PS3 Eye probe can pull all three at once,
  but Leap drops hard under shared USB load: 40.12 fps when the PS3 Eye runs
  640x480@60, and 81.10 fps when the PS3 Eye runs 320x240@187. The regular
  Kiyo and second PS3 Eye were absent, so six-camera viability is not proven.
- Current chirplet-backed smoke uses Scarlett speaker loopback as timing
  authority and can build an aligned audio frame containing loopback, Focusrite
  mic, Kiyo mic, and Kiyo Pro mic when 9-16 kHz calibration chirplets are
  playing. PS3 Eye audio is enumeration/runtime-fragile: one run saw both mic
  buffers empty while both cameras were live, then a replug made both PS3 Eye
  mic buffers emit 480-frame WASAPI blocks again.

## Immediate Re-entry Instruction

Replace the diagnostic audio JSON process bridge with native audio capture
workers, then turn chirplet delay reports into a smoothed SRO/fractional-delay
actuator. Keep loopback as timing authority. Do not restore deleted script
infrastructure because a stale doc once missed it.
