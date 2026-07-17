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
- `MimirMuninnMoveEvidenceAdapter.TryAdmitLatestCultMeshFrame` now exposes a
  typed admission receipt for that latest read: decoded frame id, producer,
  publish/read timestamps, sample counts by kind, source/arrival ranges, native
  reservoir handle, and reservoir edge/window. `Mimir.BufferSmoke
  --muninn-move-cultmesh-stream-smoke` asserts the receipt so the named proof
  spine can be traced instead of inferred.
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
- `MimirMoveProofSurface` is the observer-only dev proof surface for the named
  Move chain. It combines a Muninn evidence admission receipt with a Mimir pose
  stream frame and a Fensalir presentation/probe timestamp into
  `mimir.move_proof_surface.v1`, then lowers the verdict to a Fensalir
  `AquariumSplineFrame` probe. `Mimir.BufferSmoke --move-proof-surface-smoke`
  proves the explicit
  `muninn:nightwing:move-evidence:79 -> mimir:starfire:move-evidence:79 ->
  mimir:starfire:move-pose:79 -> fensalir:starfire:presented-frame:79` chain,
  and rejects single-ray fallback as not full pose.
- `MimirMoveProofPipeline` is the shared commit primitive for the live Move
  proof path. It starts from a Muninn CultMesh shared-memory evidence frame,
  admits samples to the native `move_evidence` reservoir, runs Mimir-owned
  fusion, creates the Mimir pose stream frame, creates the proof surface, and
  returns the Fensalir spline probe in one pass. `Mimir.BufferSmoke
  --move-proof-pipeline-smoke` proves that shape against the native reservoir
  debug DLL; live hardware and presented-frame capture still need to call this
  primitive instead of hand-assembling a parallel path.
- `MimirRuntime` is now the live Fensalir attachment point for proof surfaces.
  `PublishMoveProofSurface` accepts the Mimir-owned
  `mimir.move_proof_surface.v1` document, `CreateFrame` composes its
  observer-only `move-proof-*` splines into `AquariumFrame.Scene.SplineFrame`,
  and `Mimir.BufferSmoke --move-proof-runtime-frame-smoke` proves the frame is
  empty before publish, contains the proof splines after publish, and clears
  without leaving a repair loop behind.
- `MimirMoveProofRuntimeDriver` is the bounded runtime bridge from a Muninn
  CultMesh shared-memory evidence ring to `MimirRuntime.PublishMoveProofSurface`.
  It derives the Mimir evidence, Mimir pose, and Fensalir frame ids from the
  actual Muninn frame suffix, calls the shared proof pipeline, and suppresses
  duplicate frame admission. `Mimir.BufferSmoke --move-proof-runtime-driver-smoke`
  proves `MimirRuntime.Update` can pull one ring frame into the same-sequence
  proof chain and visible spline frame.
- `MimirMoveProofRuntimeConfiguration` is the typed runtime configuration and
  validation contract for future real Move proof subscriptions. An enabled
  source names the Muninn evidence stream, native reservoir path, Mimir evidence
  and pose frame prefixes, Fensalir presented-frame prefix, fusion authority,
  consumer contract, and at least two calibrated camera witnesses. It creates
  `MimirMoveProofRuntimeDriver` only after the supplied ring stream id matches
  the configured evidence stream.
- `MimirRuntime` now owns configured Move proof activation instead of leaving
  config as a readout. At scene-ready it asks an
  `IMimirMoveProofEvidenceRingProvider` for the configured Muninn evidence ring,
  opens the configured native reservoir, creates the driver, retains the
  resources, and exposes an activation status. The default provider fails
  explicitly because the current C# CultMesh ring is still in-process only;
  `Mimir.BufferSmoke --move-proof-runtime-activation-smoke` injects an
  in-process provider and proves config activation can produce the same named
  proof chain. Real Nightwing/Starfire cross-process ring opening remains
  pending.
- `mimir.move_proof_runtime_activation.v1` is the typed readiness/proof-spine
  surface for configured Move proof sources. It reports the configured stream,
  provider kind, active/driver state, native reservoir path, calibrated camera
  ids, latest same-stream proof ids, latest verdict, and the unavailable
  default-provider diagnostic. `Mimir.BufferSmoke
  --move-proof-runtime-activation-surface-smoke` proves both an active injected
  ring document and the inactive default runtime document without claiming live
  field evidence.
- `mimir.move_proof_evidence_frame_snapshot.v1` is the one-copy fallback for
  captured Muninn evidence frames while C# CultMesh has no cross-process ring
  opener. A configured `EvidenceSnapshotPath` is loaded by
  `MimirConfiguredMoveProofEvidenceRingProvider`, metadata-checked against the
  encoded payload, copied into an owned in-process ring, and then consumed by
  the same runtime driver/pipeline. `Mimir.BufferSmoke
  --move-proof-runtime-snapshot-smoke` proves the named proof spine through
  that file boundary. It is field capture/replay support, not the final live
  shared-memory proof.
- Odin/Muninn now has the matching producer-side latest snapshot writer:
  `--move-evidence-snapshot <path>` writes the Mimir-compatible
  `mimir.move_proof_evidence_frame_snapshot.v1` artifact from the same Muninn
  Move evidence publisher after the frame payload is accepted into the stream
  ring. The Odin unit
  `move_evidence_snapshot_writes_mimir_compatible_frame_artifact` decodes the
  snapshot tuple and embedded Muninn frame payload. This lets Nightwing produce
  a field artifact for Mimir replay while the final live ring/page transport is
  still pending.
- `MimirMoveProofDevSurface` is a dev-only bootstrap gated by
  `MIMIR_MOVE_PROOF_DEV_SURFACE`. It uses the same runtime proof attachment so
  `Mimir.BufferSmoke --move-proof-presented-frame-smoke` can run `Mimir.App`
  headless, capture the Fensalir-presented PNG, and pixel-check the named
  proof probe. This verifies the renderer/probe layer; it is not live
  Nightwing hardware evidence.
- `MimirMoveCalibrationProtocol` publishes the typed calibration preflight for
  Starfire/Nightwing Moves: required Muninn evidence streams, optional
  Quest headset/controller pose witnesses, stillness/sweep/validation phases, and
  the four derived calibration outputs Mimir must produce before IMU
  orientation can become authority.
- Muninn publishes Quest access as `muninn.quest_access.v1` for the USB-attached
  Quest. Mimir consumes that access surface and later `muninn.quest_pose_frame.v1`
  samples as optional calibration evidence; Mimir does not own ADB/Quest access.
- The old `scripts/start-nightwing-move-tracking.ps1` bring-up path is
  archived. It no longer starts a receiver/recorder pair or stages a socket
  witness worker. Nightwing Eye/Move evidence must enter through Muninn/Mimir
  typed CultMesh stream frames and Odin-discovered CultMesh documents. The blob
  stream remains optical witness evidence for later pose fusion, not the final
  6DoF pose owner.
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
- Muninn's Move evidence stream frame no longer hardcodes an empty optical
  candidate slice. The daemon frame uses the canonical
  `muninn.move_marker_candidate.v1` record shape, and the Odin unit
  `move_marker_candidates_publish_in_mimir_compatible_cultmesh_frame` proves a
  bright Y8 frame can pass through `muninn-move-tracker` and serialize as a
  non-empty marker candidate beside controller evidence. The daemon now has a
  source-local Y8 extraction/publish seam and a first `serve` camera producer:
  `--move-marker-camera <camera-id>=<device-path>` polls a Unix V4L2 YUYV frame,
  converts it to compact Y8, and feeds that same seam. Unit tests prove the
  configured camera tick publishes marker evidence through the shared frame
  contract; Nightwing hardware/live V4L2 proof is still pending.
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
  `relay` hosts the document relay on CultNet RUDP `3075`, `send` reads an
  MPEG-TS byte stream from stdin and publishes rolling
  `mimir.cultmesh_media_frame` documents, and `recv` subscribes to those
  documents and writes ordered MPEG-TS bytes to a Starfire-local UDP endpoint
  for compatibility sinks. Its C# implementation now uses explicit CultLib
  RUDP client/session helpers behind
  `cultmesh://asgard.yggdrasil.mimir/media/raven-primary-av`; the older
  `CultMesh.StartNodeAsync`/`CultMesh.ConnectClient` entrypoints no longer own
  the media transport lane, and sender/receiver launchers no longer accept raw
  relay host/port configuration.
- `scripts/start-raven-cultmesh-av-sender.ps1`,
  `scripts/start-yggdrasil-cultmesh-media-relay.ps1`, and
  `scripts/start-starfire-cultmesh-av-receiver.ps1` are the CultMesh bridge
  operators. `start-raven-cultmesh-av-sender.ps1` is the Mimir-owned
  `Mimir.CultMeshMedia` bootstrap/body-bridge lane; it is not the real Muninn
  OBS/SRT feed owner. The actual Raven OBS/plugin feed now routes through
  Odin's Muninn actuator `E:\Projects\Odin\scripts\activate-muninn-raven-av-srt.ps1`,
  with `scripts/start-raven-muninn-obs-feed.ps1` in this repo as a thin local
  wrapper around the real `GameCult-Muninn-Activate` task and
  `muninn.exe activate` body. `-LocalBootstrap` on the CultMesh sender remains
  a direct local bootstrap edge only for the separate body-bridge lane. Raven
  capture still defaults to FFmpeg desktop frames plus Mimir's WASAPI loopback
  capture muxed as H.264/AAC MPEG-TS; DirectShow audio remains an explicit
  fallback.
- `src/Mimir.EveDashboard` is archived until the dashboard returns as a pure
  CultMesh/Odin publisher and Eve lowering. The old HTTP/WebSocket deck broker,
  local health route, socket command channel, and provider route catalog are not
  daemon transport. Dashboard state must be published as typed CultMesh/Eve
  documents through Odin, and dashboard commands must arrive as typed Odin/
  CultMesh command documents.
- `src/Mimir.EveBrowserReference` serves static browser lowerings and can
  publish its own `idunn.daemon_health` record over
  `cultnet.transport.rudp.v0`. It remains a renderer/reference surface, not
  program authority.
- Documentation for OBS receiver setup, native rebuild boundaries, the viable
  stream app, and the Mimir Face.

## Temporary

- Audio and video may still traverse separate OBS/SRT endpoints during bridge
  testing, but Mimir/Eve owns independent controls.
- The CultMesh media bridge still lowers to local UDP for compatibility sinks
  because OBS is not a CultMesh consumer. Network transit between Raven,
  Yggdrasil, and Starfire is the Odin-discovered CultMesh/CultNet path;
  OBS-local UDP is an egress adapter only.
- CultLib RUDP is the default typed CultNet/CultMesh document transport for
  daemon truth. Idunn RUDP health publication is the current freshness witness;
  provider advertisements, command boundaries, transport profiles, and retained
  daemon state should follow the same path. Odin still owns Verse/service
  discovery, Idunn owns keepalive decisions, and Mimir-owned dashboard/reference
  surfaces only report their own observed state. Product/debug render surfaces
  stay lowerings or compatibility evidence.
- `Mimir.CultMeshMedia` has completed its explicit RUDP transport cut. Do not
  add another private bridge, status shim, or renderer-derived service truth
  while the RUDP document lane exists.
- Process-backed stream sources are only acceptable for network bridge feeds or
  diagnostics. Six-camera local ingest belongs behind direct capture drivers.
- Frame-event process sources are diagnostic only. They prove source cadence and
  runtime plumbing without dragging stdout bytes into the pixel hot loop.
- Calibration artifacts may remain on disk as evidence, but live state must be
  in memory inside Mimir/Fensalir/native runtime surfaces.

## Next

Before expanding Muninn media or Sleipnir input transport, apply the traffic
contracts distilled in
`docs/research/moonlight-muninn-sleipnir-study-2026-07-16.md`: latest-state,
ordered-edge, video-deadline, audio-playout, and reliable-control. Preserve the
existing Muninn packetizer/feedback foundations. The first implementation pass
should make input supersession and edge preservation structural, replace the
two-second media default with consumer-derived field budgets, and prove the
result under controlled loss/jitter/reorder before changing carriers.

The first cut landed in Odin on 2026-07-16: experimental CultLib snapshot
`8965f3c0`, epoch/sequence/edge-ack HID delivery, 100 ms default LAN media
deadline, CultNet `realtime` A/V delivery, bounded queues, expiring/late-aware
repair, and decode-chain-owned keyframe pressure. The production video path now
uses typed V4 Cauchy GF(256) FEC in independent 8-data/8-parity blocks. Each
block schedules data and parity as separate lanes, including fixed protection
for a short tail block. Canonical video and parity use CultNet's unreliable
`realtime` lane; selective repair and IDR recovery remain deadline-bound.
Canonical audio and its parity also use `realtime`; 4+2 FEC, reorder, and
concealment own continuity without an ACK/retransmit window. Sender access units enter the
handoff queue independently, and CultMesh catalog publication runs outside the
realtime media loop. The remaining work is the full direct/proxy impairment
matrix, Opus FEC/PLC, and long-duration mixed soak.

The socket harness and bounded PCM loss recovery have now landed. Odin's
`cultnet-impair` proxy supplies deterministic seeded loss/burst/reorder/
duplicate/jitter/stall profiles around real CultNet endpoints. Muninn emits a
typed fixed 4+2 audio parity block after each four constant-size PCM packets;
the production Mimir OBS receiver reconstructs up to two missing packets inside
the 40 ms reorder budget and feeds them through the existing playout owner.
Both sides use the experimental CultLib/CultMesh snapshot lineage used by Eve,
Aetheria, and VoidBot; no stable-branch transport shim was introduced.

Receiver pressure now closes into the long-lived NVENC encoder as bounded AIMD
bitrate control. Startup begins at half of the configured encoder ceiling
because fixed block parity can roughly double wire rate, and additive recovery
is capped at half of that configured ceiling. Late/decode/repair/queue
pressure backs off by 15 percent; clean recovery adds one-fiftieth of the safe
cap only after ten stable seconds. The live encoder proof reconfigured 12 Mbps to 6 Mbps without a
restart and emitted the required transition IDR. The exact remaining completion
gate is tracked in
`docs/research/moonlight-reliability-acceptance-2026-07-16.md`; no cross-host
claim is allowed until its Raven-to-Starfire field matrix passes.

The production receiver follow-up fixed two contract contradictions: early
video repair no longer declares the frame late (which had made Odin discard all
repair requests), and the OBS audio decoder now consumes Muninn's actual float
PCM contract rather than treating it as AAC. Audio reorder is bounded to 40 ms
with short silence concealment. The next audio cut is Opus with explicit
FEC/PLC for variable-rate compressed audio; PCM now has bounded 4+2 erasure
recovery plus concealment. The controllable video encoder owner now exists and
forces the next NVENC frame to IDR without restarting the video session. A live
D3D11 desktop proof verified the command-generated IDR. The Raven bundle and
OBS receiver are now deployed over the direct LAN route. Raven activation is an
interactive-token scheduled task whose PowerShell action uses
`-WindowStyle Hidden`; no WireGuard path or foreground terminal is part of the
runtime. The active field cut uses D3D11/NVENC video, WASAPI loopback audio,
typed CultNet media, and the experimental CultLib lineage. The named direct and
proxy impairment profiles and long soak remain the completion gate.

1. Replace the frame-event diagnostic bridge with concrete direct capture
   drivers for Leap stereo IR first, then the
   other cameras.
2. Cut the remaining dashboard service truth paths from older renderer-derived
   assumptions to explicit CultNet RUDP records,
   preferably authorized-peer dialing where the peer catalog exists. Preserve
   OBS-local UDP as egress only for compatibility sinks.
3. Feed those drivers into `MimirVideoCaptureDriverSource` and prove sustained
   frame cadence in the rolling buffers.
4. Promote the packet-song physical calibration receipt into the runtime
   receiver. The live decoder should keep its ear open for self-identifying
   song contours, extract intra-call time/frequency anchors from log-mel parts,
   apply learned per-output/mic path weighting, and feed a global
   delay/clock/path hypothesis. Keep chirp-bin calibration artifacts as
   reference data, not the runtime target.
5. Add the synchronization actuator: drive a variable-rate resampler and
   fractional delay line per non-reference stream from the smoothed
   `MimirAudioSynchronizationState`. First, prove the bioacoustic motif decoder
   through real loopback and microphone paths so every correctly heard word
   becomes a deterministic timeline anchor before the actuator moves samples.
6. Prove the bioacoustic hybrid fallback through real loopback and microphones
   with probe durations long enough to keep loopback and mic windows live.
7. Wire real Move proof production into `MimirMoveProofRuntimeDriver`: attach
   real Muninn Nightwing and Starfire evidence rings, require calibrated optical
   witnesses, publish `mimir:starfire:move-pose:<sequence>` through the shared
   runtime path, and replace the dev-gated presented-frame smoke with a
   same-sequence capture from real field evidence.
8. Bind Fensalir UI to the synchronization hub so buffer depth, stream cadence,
   source timestamps, and output settings are visible and adjustable.
9. Implement the Mimir program scene graph as the shared commit primitive for
   source subscription, transforms, crop, chroma key, visibility, layer order,
   preview, and output publication. Import the current OBS scene only as an
   initial mirror, then make Eve GUI/TUI the operator surface.
10. Add the Yggdrasil-facing site publisher daemon that consumes the Mimir
   program output and publishes it without owning a second composition.
11. Lower `AquariumBufferFieldFrame` spline tube fields into Fensalir compute:
   sample buffer-domain paths stochastically by visual contribution, emit SDF
   splat probes, write them into the spatiotemporal splat reservoir, and sample
   that reservoir in the temporally antialiased scene pass. The direct spline
   preview must stay a witness until this path owns rendering.
12. Move GPU feature extraction, fusion, material fitting, render budgeting, and
   Spout2 publication into Fensalir.
13. Move mic alignment, room suppression, voice separation, spatialization, and
   stem generation into Faust/native DSP.
14. Keep the OBS bridge witness ledger as evidence before expanding receiver
   machinery.
