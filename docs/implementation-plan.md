# Implementation Plan

## Current Cut Line

Mimir is now a C# app/runtime plus native reservoir project. The live stream
machine must be direct-driver and native-buffer first:

- `src/Mimir.App` hosts Aquarium Engine for windowing, rendering, and the D3D12
  bridge.
- `src/Mimir.Runtime` owns stream descriptors, source polling, direct push
  ingest, one rolling buffer per configured audio/video stream, and the
  synchronization hub Aquarium can inspect.
- `native/reservoir` owns the lower native rolling-buffer invariant for
  Aquarium/Faust integration.
- PowerShell/FFmpeg/SRT remains a bridge utility for LAN OBS feeds. It is not
  the synchronized program authority.

The old script stack is gone. Do not add a compatibility edge unless it protects
a named invariant that the native runtime cannot protect yet.

## Implemented

- Mimir public identity, branding, and Face memory.
- `Mimir.slnx` with `src/Mimir.App` and `src/Mimir.Runtime`.
- Aquarium host bootstrapping from `Mimir.App`.
- `MimirSynchronizationHub`, `MimirRollingStreamBuffer`, stream descriptors, and
  `IMimirStreamSource`.
- Configurable five-second default rolling buffers for local and network audio
  and video streams.
- `MimirNativeIngestStreamSource` for direct push ingest into runtime buffers.
- `MimirProcessStreamSource` for bridge/network command edges.
- `MimirVideoFrameDescriptor` for dimensions, pixel format, stride, device
  timestamp, and native/GPU handle metadata.
- `IMimirVideoCaptureDriver` and `MimirVideoCaptureDriverSource` as the live
  driver-facing seam for Leap, Media Foundation, DirectShow, libusb, LeapC, or
  shared texture capture.
- `native/reservoir` with one shared-edge rolling buffer, typed views, C ABI,
  source-id hashing, producer helpers, and typed audio/render payload
  descriptors.
- Windows bridge scripts for sender discovery/start/stop and simple OBS Media
  Source ingest.
- Documentation for OBS receiver setup, native rebuild boundaries, the viable
  stream app, and the Mimir Face.

## Temporary

- Audio and video may still traverse separate OBS/SRT endpoints during bridge
  testing so OBS can preserve independent controls.
- Process-backed stream sources are only acceptable for network bridge feeds or
  diagnostics. Six-camera local ingest belongs behind direct capture drivers.
- Calibration artifacts may remain on disk as evidence, but live state must be
  in memory inside Mimir/Aquarium/native runtime surfaces.

## Next

1. Implement concrete direct capture drivers for Leap stereo IR first, then the
   other cameras.
2. Feed those drivers into `MimirVideoCaptureDriverSource` and prove sustained
   frame cadence in the rolling buffers.
3. Add native audio capture workers for local mic, loopback, and network audio
   feeds.
4. Bind Aquarium UI to the synchronization hub so buffer depth, stream cadence,
   source timestamps, and output settings are visible and adjustable.
5. Move GPU feature extraction, fusion, material fitting, render budgeting, and
   Spout2 publication into Aquarium.
6. Move mic alignment, room suppression, voice separation, spatialization, and
   stem generation into Faust/native DSP.
7. Keep the OBS bridge witness ledger as evidence before expanding receiver
   machinery.
