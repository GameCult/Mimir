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
  and the video capture driver seam.
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
- Bind Aquarium UI to stream health, buffer depth, timestamps, settings, and
  output management.
- Keep PowerShell/FFmpeg/SRT as bridge utilities until native program output
  replaces them.

## Immediate Re-entry Instruction

Read `docs/native-rebuild-plan.md`, then make the smallest direct-driver or
runtime-buffer cut that improves the live Mimir machine. Do not restore deleted
script infrastructure because a stale doc once missed it.
