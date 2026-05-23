# Implementation Plan

## Current Cut Line

Mimir is now a C# app/runtime plus native reservoir project. The live stream
machine must be direct-driver and native-buffer first:

- `src/Mimir.App` hosts Aquarium Engine for windowing, rendering, and the D3D12
  bridge.
- `src/Mimir.Runtime` owns stream descriptors, source polling, direct push
  ingest, one rolling buffer per configured audio/video stream, and the
  synchronization hub Aquarium can inspect.
- The default five-second window is an intentional latency/memory trade: use it
  to line up streams and extract the volumetric audio/video field before OBS
  sees program output.
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
- `MimirFrameEventProcessStreamSource` for temporary JSON-line frame metadata
  from native probes into the same rolling buffers Aquarium inspects. One probe
  process can accept multiple emitted `sourceId` values so it does not reopen
  the same camera set once per stream.
- `src/Mimir.BufferSmoke` loads the runtime config, polls the synchronization
  hub, and prints the actual rolling buffers. Use `--require-samples` when an
  empty declared sensor buffer should fail the run. Use `--chirplet-self-test`
  to render the canonical timeline into memory and verify that the constrained
  decoder recovers sub-frame anchors without hardware.
- `native/probes/wasapi_audio_cadence` captures WASAPI mic or render-loopback
  block metadata and emits `audio-block` JSON events for the diagnostic runtime
  adapter. It can probe requested shared/exclusive formats so driver state is
  explicit, but Scarlett production capture belongs on ASIO.
- `native/probes/asio_audio_cadence` opens the registered Focusrite ASIO COM
  driver, reports channel counts, buffer sizing, supported sample rates, and can
  run a short input callback capture. Current Starfire Focusrite USB ASIO proof
  with the Scarlett Solo 4th Gen shows 4 inputs / 2 outputs, including
  `Loopback 1/2`, 192-frame preferred buffers, 44.1-192 kHz support, and
  nonzero 4-channel `Int32LSB` input callbacks at 192 kHz. `--monitor-sweep`
  emits low-gain ASIO output bursts and synchronously measures loopback/mic
  response per frequency so ultrasonic acoustic claims stay measured.
- `MimirChirpletTimeline` owns the structured birdsong-like calibration stream,
  PCM segment rendering, matched timing trace, and per-band response kernels.
  The default timeline is an order-3 de Bruijn symbol sequence over 32
  time/frequency constellation symbols, so any three consecutive correctly
  detected symbols identify a timeline event inside the current operating
  horizon. Symbol identity is carried by start band, glide shape, duration, and
  following inter-chirp gap.
- `MimirChirpletSymbolCodebook` owns the 32 symbol definitions. Each symbol has
  a unique chirp shape, with inter-chirp rhythm as additional code evidence.
- `MimirChirpletStreamDecoder` is the first constrained chirplet-transform
  receiver. It owns a bounded PCM window, emits transform frames with multiple
  phase-invariant symbol candidates and per-candidate refined sample offsets,
  decodes code-valid triplet anchors through a local trellis that requires gap
  and clock coherence, and fits a per-source sample clock from those anchors.
- `MimirAudioSynchronizationAnalyzer` ports the first live sync measurement:
  sample-bearing audio blocks are resampled into the Scarlett loopback timeline.
  The analyzer derives delay only from matched decoded triplet timeline anchors.
  A source without at least three matched anchors has no timing report for that
  window. It accepts Float32, Int16, Int24, and Int32 PCM windows so ASIO/native
  capture can feed true interface formats without a pre-conversion shim.
- `MimirAudioSynchronizationStateTracker` owns the first smoothed per-source
  sync state: latest fractional delay, smoothed delay, confidence, per-band
  response evidence, and delay-slope/SRO estimate in ppm.
- `MimirRuntime` updates audio sync analysis online as a bounded rotating
  service and can emit live sync telemetry with
  `MIMIR_SYNC_TELEMETRY_SECONDS`. UI and telemetry read cached reports/states;
  they do not run synchronization analysis.
- `MimirRuntime` queues chirplet timeline PCM through Aquarium audio when the
  active timing witness is allowed. `MimirAudioSynchronizationSettings.Mode`
  selects `chirp-only`, `passive`, or `hybrid`; passive disables active
  emission, and hybrid emits the active pilot only while passive confidence is
  below threshold.
- `MimirPassiveAudioSynchronizationEstimator` is the first program-audio timing
  path. It estimates loopback-to-mic delay with PHAT-weighted cross-spectrum
  correlation so music can act as the default timing witness before any audible
  watermark is needed.
- `MimirChirpBinTimeline` is the active hybrid watermark path. It renders a
  fixed-slope chirp-bin codebook and decodes symbols with one dechirp plus
  fixed Goertzel bins, then feeds the same de Bruijn triplet timeline-anchor
  machine. The detector scores dechirped bin energy directly, caches chirp
  kernels per sample rate, and uses a widened 32-symbol codebook so sub-frame
  timing error does not masquerade as neighboring symbol identity. The matched
  chirp-bin peak is refined below one sample and reports/states expose delay in
  microseconds as well as fractional samples. Hybrid emits this as low-gain
  half-second bursts every two seconds only while passive confidence is weak.
  Use `--chirp-bin-self-test` to prove the codebook/decoder and
  `--hybrid-sync-self-test` to prove that the analyzer can recover a fractional
  delay from a one-second rolling-buffer chirp-bin window. The current synthetic
  microsecond proof recovers a 317.375-sample delay with 0.369 us error.
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
- Frame-event process sources are diagnostic only. They prove source cadence and
  runtime plumbing without dragging stdout bytes into the pixel hot loop.
- Calibration artifacts may remain on disk as evidence, but live state must be
  in memory inside Mimir/Aquarium/native runtime surfaces.

## Next

1. Replace the frame-event diagnostic bridge with concrete direct capture
   drivers for Leap stereo IR first, then the
   other cameras.
2. Feed those drivers into `MimirVideoCaptureDriverSource` and prove sustained
   frame cadence in the rolling buffers.
3. Replace the WASAPI frame-event diagnostic bridge with native audio capture
   workers for local mic, loopback, and network audio feeds. Use the verified
   Focusrite ASIO path for Scarlett capture; WASAPI remains a diagnostic
   witness, not the Scarlett hot path.
4. Add the synchronization actuator: drive a variable-rate resampler and
   fractional delay line per non-reference stream from the smoothed
   `MimirAudioSynchronizationState`. First, prove the constrained chirplet
   decoder through real loopback and microphone paths so every correctly heard
   triplet becomes a deterministic timeline anchor before the actuator moves
   samples.
5. Prove the chirp-bin hybrid fallback through real loopback and microphones
   with probe durations long enough to keep loopback and mic windows live.
6. Bind Aquarium UI to the synchronization hub so buffer depth, stream cadence,
   source timestamps, and output settings are visible and adjustable.
7. Move GPU feature extraction, fusion, material fitting, render budgeting, and
   Spout2 publication into Aquarium.
8. Move mic alignment, room suppression, voice separation, spatialization, and
   stem generation into Faust/native DSP.
9. Keep the OBS bridge witness ledger as evidence before expanding receiver
   machinery.
