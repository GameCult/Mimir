# Implementation Plan

## Current Cut Line

Mimir is now a C# app/runtime plus native reservoir project. The live stream
machine must be direct-driver and native-buffer first.

For the source-level ownership map, read [[code-algorithm-map|Code Algorithm Map]].

- `src/Mimir.App` hosts Fensalir for windowing, rendering, and the D3D12
  bridge.
- `src/Mimir.Runtime` owns stream descriptors, source polling, direct push
  ingest, one rolling buffer per configured audio/video stream, and the
  synchronization hub Fensalir can inspect.
- The default five-second window is an intentional latency/memory trade: use it
  to line up streams and extract the volumetric audio/video field before OBS
  sees program output.
- `native/reservoir` owns the lower native rolling-buffer invariant for
  Fensalir/Faust integration.
- PowerShell/FFmpeg/SRT remains a bridge utility for LAN OBS feeds. It is not
  the synchronized program authority.

The old script stack is gone. Do not add a compatibility edge unless it protects
a named invariant that the native runtime cannot protect yet.

## Implemented

- Mimir public identity, branding, and Face memory.
- `Mimir.slnx` with `src/Mimir.App` and `src/Mimir.Runtime`.
- Fensalir host bootstrapping from `Mimir.App`.
- `MimirSynchronizationHub`, `MimirRollingStreamBuffer`, stream descriptors, and
  `IMimirStreamSource`.
- Configurable five-second default rolling buffers for local and network audio
  and video streams.
- `MimirNativeIngestStreamSource` for direct push ingest into runtime buffers.
- `MimirProcessStreamSource` for bridge/network command edges.
- `MimirFrameEventProcessStreamSource` for temporary JSON-line frame metadata
  from native probes into the same rolling buffers Fensalir inspects. One probe
  process can accept multiple emitted `sourceId` values so it does not reopen
  the same camera set once per stream.
- `native/asio_capture` plus `MimirAsioStreamSource` provide the first
  production-shaped Focusrite path: a native in-process ASIO callback source
  feeds sample-bearing 192 kHz Float32 blocks directly into `Mimir.Runtime`
  rolling buffers on one interface clock domain.
- `src/Mimir.BufferSmoke` loads the runtime config, polls the synchronization
  hub, and prints the actual rolling buffers. Use `--require-samples` when an
  empty declared sensor buffer should fail the run. Use `--bioacoustic-self-test`
  to render the active motif timeline into memory and verify that the decoder
  recovers direct word anchors. Use `--standalone-bioacoustic-self-test` to
  verify that a receiver with only the codebook/schedule can recover canonical
  source offset from delayed audio.
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
  response per frequency so ultrasonic acoustic claims stay measured. The probe
  can also play raw mono Float32 timeline audio with `--play-f32-mono` and
  capture raw interleaved Float32 ASIO input with `--record-f32-interleaved`.
- `MimirBioacousticTimeline` owns the active runtime watermark described in
  [[bioacoustic-timeline-watermark|Bioacoustic Timeline Watermark]]. It renders
  a low-gain birdsong-like word language: 128 self-identifying word positions,
  left-speaker and right-speaker variants, four formant-rich syllables per word,
  rhythm variation, and direct word identity so a correctly decoded word
  identifies canonical timeline position. Runtime active sync now reports
  `evidence=bioacoustic`.
- `MimirChirpletTimeline` owns the older structured chirplet calibration stream,
  PCM segment rendering, matched timing trace, and per-band response kernels.
  It remains a reference/diagnostic path.
- `MimirChirpletSymbolCodebook` owns the 32 symbol definitions. Each symbol has
  a unique chirp shape, with inter-chirp rhythm as additional code evidence.
- `MimirChirpletStreamDecoder` is the first constrained chirplet-transform
  receiver. It owns a bounded PCM window, emits transform frames with multiple
  phase-invariant symbol candidates and per-candidate refined sample offsets,
  decodes code-valid triplet anchors through a local trellis that requires gap
  and clock coherence, and fits a per-source sample clock from those anchors.
- `MimirAudioSynchronizationAnalyzer` ports the first live sync measurement:
  sample-bearing audio blocks are resampled into the Scarlett loopback timeline.
  The analyzer derives delay only from matched decoded timeline anchors.
  A source without at least one matched anchor has no timing report for that
  window. It accepts Float32, Int16, Int24, and Int32 PCM windows so ASIO/native
  capture can feed true interface formats without a pre-conversion shim.
- `MimirAudioSynchronizationStateTracker` owns the first smoothed per-source
  sync state: latest fractional delay, smoothed delay, confidence, per-band
  response evidence, and delay-slope/SRO estimate in ppm.
- `MimirRuntime` updates audio sync analysis online as a bounded rotating
  service and can emit live sync telemetry with
  `MIMIR_SYNC_TELEMETRY_SECONDS`. UI and telemetry read cached reports/states;
  they do not run synchronization analysis.
- `MimirRuntime` queues bioacoustic timeline PCM through Fensalir audio when the
  active timing witness is allowed. `MimirAudioSynchronizationSettings.Mode`
  selects `chirp-only`, `passive`, or `hybrid`; passive disables active
  emission, chirp-only emits the active witness continuously, and hybrid emits
  the active pilot only while passive confidence is below threshold.
- `MimirPassiveAudioSynchronizationEstimator` is the first program-audio timing
  path. It estimates loopback-to-mic delay with PHAT-weighted cross-spectrum
  correlation so music can act as the default timing witness before any audible
  watermark is needed.
- `MimirChirpBinTimeline` is the old chirp-bin calibration path. It renders a
  fixed-slope chirp-bin codebook and decodes symbols with cheap event-energy
  proposals, dechirp plus fixed Goertzel bins, and the same de Bruijn triplet
  timeline-anchor machine. The detector keeps
  time/frequency ambiguity as candidate symbol/offset pairs so code constraints
  can choose the coherent path. The analyzer refines the final fractional delay
  with constrained local waveform correlation around the decoded active delay.
  Each classified chirp carries the full bin-energy response surface, and stream
  decodes aggregate that into per-band calibration evidence for frequency
  response normalization. `MimirChirpBinCalibrationModel` now preserves usable
  bands, expected-symbol versus observed-bin confusion observations, timing
  residuals, delay hypotheses, phase summaries, and an adaptive codebook plan.
  The reference decoder can consume that model as learned response weighting,
  phase-coherence weighting, first-order group-delay correction, and joint
  global delay/bin-shift hypotheses. The
  runtime emitter also consumes the model's emission plan, rendering the smaller
  reliable symbol alphabet at the higher recommended de Bruijn order when the
  physical path cannot support all 32 bins.
  Reports/states expose delay in microseconds as well as fractional samples.
  Hybrid now emits the bioacoustic watermark as low-gain half-second bursts only
  while passive confidence is weak; chirp-bin remains available for ASIO
  calibration/replay commands.
  Use `--bioacoustic-self-test` and `--standalone-bioacoustic-self-test` to
  prove the active motif decoder. Use `--chirp-only-sync-self-test` to prove
  that the analyzer can recover fractional delay from the bioacoustic runtime
  watermark. The current synthetic active proof recovers a 317.375-sample delay
  with printed 0.000 us error.
- `Mimir.BufferSmoke --render-chirp-bin-f32` and `--analyze-asio-f32` provide
  the retained Scarlett calibration artifact proof. A 192 kHz chirp-bin run decoded
  Focusrite `Loopback 1 -> Loopback 2` at `0.000 us` with 12 matched anchors and
  0.999 confidence. `--calibrate-chirp-bin-asio-f32` can render/capture/analyze
  a calibration session and persist the response/confusion/delay model under
  `calibration/chirp-bin/`. `--analyze-asio-f32 --calibration ...` loads that
  model into the chirp-bin reference decoder. Physical input 1 still failed pairwise timing
  in the stored artifact, but it produced a useful response/confusion model
  with two reliable symbols. Acoustic robustness remains separate from the clean
  loopback timing proof, but failed timing windows now leave usable response
  evidence instead of silence.
- `config/mimir-runtime.asio.example.json` is the minimal continuous Scarlett
  runtime ingest proof. It loads `native/asio_capture` in process at 192 kHz
  and declares `asio-ch0` through `asio-ch3` as accepted audio sources. A
  two-second BufferSmoke run ingested more than 12,000 sample-bearing blocks
  and retained 2,048 blocks per channel, proving loopback and mic channels enter
  `Mimir.Runtime` together in one ASIO clock domain without stdout/base64
  transport. BufferSmoke does not
  emit speaker calibration audio, so that proof is ingest-only rather than a
  sync report.
  The active bioacoustic standalone receiver test recovers a delayed audio
  stream to below printed microsecond precision using only the motif codebook
  and schedule state.
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
  in memory inside Mimir/Fensalir/native runtime surfaces.

## Next

1. Replace the frame-event diagnostic bridge with concrete direct capture
   drivers for Leap stereo IR first, then the
   other cameras.
2. Feed those drivers into `MimirVideoCaptureDriverSource` and prove sustained
   frame cadence in the rolling buffers.
3. Use bioacoustic motif response profiles from real microphones to tune
   contour, formant, band, gain, rhythm weighting, and left/right speaker
   separation without weakening the standalone word-identity receiver invariant.
   Keep chirp-bin calibration artifacts as reference data, not the runtime
   target.
4. Add the synchronization actuator: drive a variable-rate resampler and
   fractional delay line per non-reference stream from the smoothed
   `MimirAudioSynchronizationState`. First, prove the bioacoustic motif decoder
   through real loopback and microphone paths so every correctly heard word
   becomes a deterministic timeline anchor before the actuator moves samples.
5. Prove the bioacoustic hybrid fallback through real loopback and microphones
   with probe durations long enough to keep loopback and mic windows live.
6. Bind Fensalir UI to the synchronization hub so buffer depth, stream cadence,
   source timestamps, and output settings are visible and adjustable.
7. Move GPU feature extraction, fusion, material fitting, render budgeting, and
   Spout2 publication into Fensalir.
8. Move mic alignment, room suppression, voice separation, spatialization, and
   stem generation into Faust/native DSP.
9. Keep the OBS bridge witness ledger as evidence before expanding receiver
   machinery.
