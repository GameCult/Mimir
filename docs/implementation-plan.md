# Implementation Plan

## Current Cut Line

Mimir is now a C# app/runtime plus native reservoir project. The live stream
machine must be direct-driver and native-buffer first.

For the source-level ownership map, read [[code-algorithm-map|Code Algorithm Map]].

- `src/Mimir.App` hosts Fensalir for windowing, rendering, and the D3D12
  bridge.
- `src/Mimir.Runtime` owns stream descriptors, source polling, direct push
  ingest, one rolling buffer per configured audio/video/tracking stream, and the
  synchronization hub Fensalir can inspect.
- The default five-second window is an intentional latency/memory trade: use it
  to line up streams and extract the volumetric audio/video field before Mimir
  emits program output.
- `native/reservoir` owns the lower native rolling-buffer invariant for
  Fensalir/Faust integration.
- PowerShell/FFmpeg/SRT remains a bridge utility for LAN compatibility feeds.
  It is not the synchronized program or publication authority.

The old script stack is gone. Do not add a compatibility edge unless it protects
a named invariant that the native runtime cannot protect yet.

## Implemented

- Mimir public identity, branding, and Face memory.
- `Mimir.slnx` with `src/Mimir.App` and `src/Mimir.Runtime`.
- Fensalir host bootstrapping from `Mimir.App`.
- `MimirSynchronizationHub`, `MimirRollingStreamBuffer`, stream descriptors, and
  `IMimirStreamSource`.
- Configurable five-second default rolling buffers for local and network audio
  and video/tracking streams.
- `MimirNativeIngestStreamSource` for direct push ingest into runtime buffers.
- `MimirStreamKind.Tracking` plus `MimirTrackingObservation` carry PS Move
  evidence samples as typed `mimir.move_tracking_observation.v1` documents.
  Starfire and Nightwing both have USB-attached Moves. Muninn daemons publish
  source-local glowing-orb marker candidates and controller/IMU/button state;
  Odin owns discovery and schema projection, not pose fusion.
- `native/reservoir` exposes a `move_evidence` typed view and
  `LocalcastMoveEvidenceBufferDescriptor` so Muninn Move witnesses can be
  admitted as compute-upload buffers before Mimir's fusion pass and Fensalir's
  GPU consumers touch them.
- `MimirNativeReservoirRuntime` is the managed owner for that native runtime
  boundary. It pins Move evidence sample/descriptor batches so native reservoir
  handles remain valid while Mimir prepares fusion input.
- `MimirMuninnMoveEvidenceAdapter` mirrors Muninn's marker-candidate and
  controller-state document schemas and normalizes them into native Move
  evidence samples. It does not associate markers to controllers or synthesize
  pose.
- The realtime Move witness path uses CultMesh streaming, not CultCache polling:
  Muninn publishes shared-memory stream frames, Mimir consumes the latest read
  lease, normalizes the frame, and admits it to native `move_evidence`.
- `mimir.move_controller_pose.v1` is Mimir's resolved wand pose contract.
  Mimir owns calibration, association, triangulation, IMU fusion, prediction,
  confidence, and latency accounting for those poses before Fensalir consumes
  them as interactive controller input.
- `MimirMoveFusion` is the first calibrated fusion owner for Move tracking:
  it consumes native Muninn Move evidence samples, requires calibrated camera
  witnesses before publishing a pose, associates marker candidates with the
  USB controller state, triangulates multi-camera orb position, demotes
  single-ray fallbacks through confidence, carries buttons/trigger/gyro into
  `mimir.move_controller_pose.v1`, and marks orientation as
  `orientation:imu-unresolved` until the IMU/prediction pass earns full 6DoF.
- `MimirMovePoseStream` frames resolved Move poses as
  `mimir.move_controller_pose_stream_frame.v1` over CultMesh shared-memory
  bytes streams so Fensalir and other consumers have a realtime stream contract
  for Mimir-fused controller input.
- `scripts/start-nightwing-move-tracking.ps1` is the narrow live Nightwing
  bring-up path: it starts the `/eve/periwinkle` receiver/recorder on Starfire,
  stages the Nightwing Eye/Move Python worker, keeps a heartbeat for field
  liveness, and publishes `mimir.psmove_light_state.v1` plus
  `mimir.move_controller_observation_state.v1` blob observations from both PS3
  Eyes. The blob stream is optical witness evidence for later pose fusion, not
  the final 6DoF pose owner.
- Structured PS Move light pulses are Muninn output commands. Mimir publishes
  `muninn.move_light_command.v1` over CultNet/CultMesh to the Muninn daemon on
  the host that owns the USB-attached Move. Muninn writes PS Move HID report
  `0x06` locally and updates command state/receipt.
  `scripts/start-starfire-move-light.ps1` and Nightwing direct HID writes are
  now smoke/bootstrap paths for hardware proof when Muninn is not yet running
  on that host.
- Odin's Muninn organ owns Move optical candidate extraction in
  `E:\Projects\Odin\crates\muninn-move-tracker` and publishes/feeds
  `muninn.move_marker_candidate.v1` records. Mimir consumes those candidate
  streams; it does not own raw optical extraction. Final pose, stereo
  triangulation, calibration, association, IMU fusion, and prediction belong to
  Mimir.
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
  source offset from delayed audio. Use `--bioacoustic-train` to run the
  indexed cepstral receiver hypothesis panel and write CultCache/audio receipts
  under `artifacts/bioacoustic-training/`.
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
  identifies canonical timeline position. The active receiver is no longer a
  sequence decoder: each song contour should expose multiple time/frequency
  anchors through syllable timing, bends, formants, payload ornaments, rhythm,
  speaker tint, and log-mel shape. Runtime active sync now reports
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
- `faust/mimir_alignment_actuator.dsp` is the first Faust-owned sample movement
  surface: six channels of bounded fractional delay and gain controls. Mimir
  estimates delay/SRO; Faust/native DSP applies correction.
- `MimirRuntime` updates audio sync analysis online as a bounded rotating
  service and can emit live sync telemetry with
  `MIMIR_SYNC_TELEMETRY_SECONDS`. UI and telemetry read cached reports/states;
  they do not run synchronization analysis.
- `MimirRuntime` publishes live ASIO spectrum history to Fensalir in two layers:
  `AquariumBufferFieldFrame` carries the real buffer-field intent for spectrum
  windows as spline-domain tube fields with tangent/curvature/normal/derivative
  appearance and probe policy, while `AquariumSplineFrame` is the temporary
  Catmull-Rom tube preview so the buffer contents remain visible before GPU
  reservoir lowering owns the draw.
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
- `Mimir.BufferSmoke --calibrate-contestant-asio-f32` persists the active
  packet-song physical calibration model under `calibration/bioacoustic/`.
  It learns per-channel schedule offset, polarity, payload reliability,
  response-normalization bands, gain, confidence, and pairwise propagation
  delay from the same 192 kHz ASIO capture. The latest Scarlett receipt
  `calibration/bioacoustic/scarlett-canary-packet-192k-rerun.json` clears the
  current hot-loop budget at 10.7x realtime across four channels. Loopback
  channels decode 37/37 with 2.524 us MAE; the co-streamer shotgun decodes
  37/37 payload with 58.785 us MAE; the cardioid decodes 26/37 with
  90.558 us MAE. This is a real response/propagation-delay model, but not yet
  microsecond-accurate physical mic sync.
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
  and schedule state. `--bioacoustic-actuator-self-test` estimates a synthetic
  317.375-sample bioacoustic delay, applies the fractional correction, and
  remeasures the residual below printed microsecond precision.
- `src/Mimir.Runtime/Synchronization` now contains the reusable Perfect Machine
  module library distilled from the research pass: node profiles, decoder
  profiles, language/emission profiles, path-learning sessions, benchmark
  panels, audio actuator strategies, native capture profiles, camera ingest
  strategies, reservoir strategies, audio/visual field profiles, compute
  offload profiles, program publication profiles, CultMesh contracts, Fensalir
  lowering, and assembly plans. Use
  `Mimir.BufferSmoke --perfect-machine-profile-smoke` to prove the catalog
  assembles, `--perfect-machine-contract-smoke` to write a CultCache contract
  proof, `--perfect-machine-manifest` to export the module manifest for
  tooling/UI/remote witness use, and `--perfect-machine-lowering-benchmark` to
  measure the Mimir-to-Fensalir lowering path. The current six-camera/two-audio
  synthetic lowering benchmark runs at roughly 2.5 us per iteration with about
  2.3 KB allocated per iteration. `--move-tracking-contract-smoke` proves
  Starfire-local and Nightwing-remote Move tracking observations enter
  Mimir's rolling buffers with Muninn producer identity and Odin discovery
  provenance.
- `MimirVideoFrameDescriptor` for dimensions, pixel format, stride, device
  timestamp, and native/GPU handle metadata.
- `IMimirVideoCaptureDriver` and `MimirVideoCaptureDriverSource` as the live
  driver-facing seam for Leap, Media Foundation, DirectShow, libusb, LeapC, or
  shared texture capture.
- `native/reservoir` with one shared-edge rolling buffer, typed views, C ABI,
  source-id hashing, producer helpers, and typed audio/render payload
  descriptors.
- Windows bridge scripts for sender discovery/start/stop and simple OBS Media
  Source compatibility ingest.
- `src/Mimir.Runtime/Synchronization` now contains the first typed program
  composition contracts: `mimir.program_scene.v1`, `mimir.program_output.v1`,
  and `mimir.eve_operator_surface.v1`. These make Mimir's scene graph,
  Yggdrasil/site publication route, and Eve operator control/preview surface
  manifestable through CultMesh.
- `docs/mimir-program-composition.md` is the current authority map for the new
  stream-program architecture: Muninn observes on Starfire/Nightwing/Raven-class
  hosts, Mimir consumes selected streams and composes the program, Eve lowers
  controls/previews/stats, Yggdrasil publishes to the site, and OBS is
  compatibility-only.
- `src/Mimir.CultMeshMedia` is the first explicit CultMesh media/body bridge:
  `relay` hosts the document relay on CultNet UDP `3075`, `send` reads an
  MPEG-TS byte stream from stdin and publishes rolling
  `mimir.cultmesh_media_frame` documents, and `recv` subscribes to those
  documents and writes ordered MPEG-TS bytes to a Starfire-local UDP endpoint
  for compatibility sinks.
- `scripts/start-raven-cultmesh-av-sender.ps1`,
  `scripts/start-yggdrasil-cultmesh-media-relay.ps1`, and
  `scripts/start-starfire-cultmesh-av-receiver.ps1` are the CultMesh bridge
  operators. Raven capture defaults to FFmpeg desktop frames plus Mimir's
  WASAPI loopback capture muxed as H.264/AAC MPEG-TS; DirectShow audio remains
  an explicit fallback.
- Documentation for OBS receiver setup, native rebuild boundaries, the viable
  stream app, and the Mimir Face.

## Temporary

- Audio and video may still traverse separate OBS/SRT endpoints during bridge
  testing, but Mimir/Eve owns independent controls.
- The CultMesh media bridge still lowers to local UDP for compatibility sinks
  because OBS is not a CultMesh consumer. Network transit between Raven,
  Yggdrasil, and Starfire is the CultMesh/CultNet path; OBS-local UDP is an
  egress adapter only.
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
3. Promote the packet-song physical calibration receipt into the runtime
   receiver. The live decoder should keep its ear open for self-identifying
   song contours, extract intra-call time/frequency anchors from log-mel parts,
   apply learned per-output/mic path weighting, and feed a global
   delay/clock/path hypothesis. Keep chirp-bin calibration artifacts as
   reference data, not the runtime target.
4. Add the synchronization actuator: drive a variable-rate resampler and
   fractional delay line per non-reference stream from the smoothed
   `MimirAudioSynchronizationState`. First, prove the bioacoustic motif decoder
   through real loopback and microphone paths so every correctly heard word
   becomes a deterministic timeline anchor before the actuator moves samples.
5. Prove the bioacoustic hybrid fallback through real loopback and microphones
   with probe durations long enough to keep loopback and mic windows live.
6. Bind Fensalir UI to the synchronization hub so buffer depth, stream cadence,
   source timestamps, and output settings are visible and adjustable.
7. Implement the Mimir program scene graph as the shared commit primitive for
   source subscription, transforms, crop, chroma key, visibility, layer order,
   preview, and output publication. Import the current OBS scene only as an
   initial mirror, then make Eve GUI/TUI the operator surface.
8. Add the Yggdrasil-facing site publisher daemon that consumes the Mimir
   program output and publishes it without owning a second composition.
9. Lower `AquariumBufferFieldFrame` spline tube fields into Fensalir compute:
   sample buffer-domain paths stochastically by visual contribution, emit SDF
   splat probes, write them into the spatiotemporal splat reservoir, and sample
   that reservoir in the temporally antialiased scene pass. The direct spline
   preview must stay a witness until this path owns rendering.
10. Move GPU feature extraction, fusion, material fitting, render budgeting, and
   Spout2 publication into Fensalir.
11. Move mic alignment, room suppression, voice separation, spatialization, and
   stem generation into Faust/native DSP.
12. Keep the OBS bridge witness ledger as evidence before expanding receiver
   machinery.
