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

Fensalir is not a conventional renderer bolted onto Mimir. It is the engine-side
field/evidence machine. Mimir must submit synchronized buffers, constraints,
surface intent, and calibration evidence; Fensalir lowers them into claims with
travel/depth, metadata, control, and reservoir-guide lanes. Any path that only
paints pixels is a fallback/debug draw and must not be mistaken for the
spatiotemporal machine.

Those claims are not inherently pixel-sized. Pixel-level resolve consumes the
reservoir, but claim support is chosen from the represented field. Smooth
surfaces should stay broad. Aquarium-style heightfields should be quadtree
surface domains with 2D brushes painting tile payloads, and SDF probes emitting
surface splats only where curvature, projected error, material/brush detail, or
silhouettes justify subdivision.

The current teardown/migration map lives in
[[fensalir-rendering-rebuild-migration|Fensalir Rendering Rebuild Migration]].
Mimir's side of that rebuild is to publish typed physical observations,
calibration constraints, and surface intent; Fensalir's side is to turn those
into field claims, selected lowerings, reusable evidence, temporal guides, and
program output.

Payload handles are now demoted to names inside a resource authority. Mimir
declares live native/GPU payloads as typed Fensalir resources with shape,
residency, shader access, validity, version, and native handle metadata; claims
and lowering requests reference those resource keys. A string handle without a
matching resource declaration is not payload truth.

The old script stack is gone. Do not add a compatibility edge unless it protects
a named invariant that the native runtime cannot protect yet.

## Implemented

- Mimir public identity, branding, and Face memory.
- `Mimir.slnx` with `src/Mimir.App` and `src/Mimir.Runtime`.
- Fensalir host bootstrapping from `Mimir.App`.
- [[eve-program-output|EVE Program Output]] now owns the first native EVE
  streaming contract: Fensalir can publish the completed D3D12 backbuffer into a
  named shared texture when `FENSALIR_PROGRAM_OUTPUT_D3D12=1`, and Mimir records
  the EVE-facing publication profile as a native D3D12 stream rather than a
  WebKit/dashboard path.
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
- `MimirRuntime` no longer submits the legacy direct `AquariumSplineFrame`
  spectrum dashboard. Live spectrum visualization authority is the
  `AquariumFieldEvidenceFrame`: Mimir declares the rolling resource, emits a
  Tube claim and `AquariumFieldTubeSplineLowering`, and Fensalir plans/expands
  the TubeField generated mesh. Mimir now normalizes current spectral frames
  into a row-major Float32 matrix, attaches that as a field resource upload, and
  Fensalir copies it into the resolved GPU structured buffer before TubeField
  compute reads. The old `AquariumBufferFieldFrame` / `ReservoirSplats`
  dashboard path is also silent. `Mimir.BufferSmoke
  --mimir-spectrum-upload-smoke` boots `MimirRuntime`, advances the synthetic
  spectrum preview path, and verifies that the frame contains the Float32
  resource upload plus planned TubeField packet while legacy spline and buffer
  field inputs stay empty. The same runtime path declares the local blackbody
  ramp PNG as a GPU-resident Texture2D resource and binds it by resource key.
  The uploaded spectrum resource is a rolling column matrix now: physical
  columns are flattened `(history age, source lane)` pairs, and Mimir emits one
  Tube claim/lowering per source with `ColumnStride=sourceCount` so each lane
  traverses its own age trail at `z = age * 0.1`. The resource uses a fixed
  history-column capacity so frame-to-frame content versions do not resize the
  GPU buffer while the history fills. `MIMIR_SPECTRUM_TUBE_SUBDIVISIONS`
  controls the Mimir-requested Catmull-Rom subdivision count so Fensalir's
  overflow report has a direct cost lever.
- `MimirFensalirFieldLowering` now emits `AquariumFieldResourceDeclaration`
  rows for live native/GPU payload views. Observation claims reference
  `mimir:resource:*` keys, and Fensalir validation/planning can reject or defer
  packets whose resources are missing, duplicated, CPU-only, or
  backend-incompatible.
  `Mimir.BufferSmoke --fensalir-field-evidence-smoke` proves the first
  hardware-free receipt with one declared resource, three claims, one planned
  resource-backed `TubeField` packet, and two deferred non-backend claims.
  `Mimir.BufferSmoke --fensalir-field-dsl-resource-smoke` proves Fensalir's DSL
  evidence compiler can bind a declared resource and produce one planned
  `TubeField` packet with no deferred requests.
- Fensalir now owns the first in-process D3D12 field resource resolver cut:
  shared structured/curve buffer resources import/alias GPU-resident handles,
  and Fensalir-owned resources allocate GPU slots only when Fensalir is the
  producer. Texture2D local assets can now be declared as field resources and
  bound automatically by DSL handles, so TubeField ramps use resource keys
  rather than raw paths as live authority. Mimir-declared rendering buffers are
  Fensalir GPU resources in the same runtime and should not round-trip through
  CPU payloads. The TubeField DSL can now describe a 2D rolling float buffer as
  Catmull-Rom XY tubes with modulo column addressing, amplitude power/
  normalization, radius, ramp texture resource, and emission scale. D3D12 now
  expands and renders those tubes from GPU-resident buffers, samples material
  per pixel, binds declared local ramp textures, and executes GPU-emitted
  indirect draw argument packets. Surface-page resources can now be declared and
  resolved as GPU-resident shader-readable 2D pages; render lowerings that
  consume those pages as height/SDF/material domains remain future engine cuts.
  VolumeTexture resources can now be declared and resolved as GPU-resident
  shader-readable 3D textures for future density/extinction/SDF3D lowerings.
  Mesh resources now have that explicit ownership contract: `Mesh.Vertices` and
  `Mesh.Indices` package the vertex/index GPU buffers with topology, index
  format, bounds, submesh count, and version. The Fensalir DSL can declare a
  mesh package, and the D3D12 registry allocates/reuses its GPU-resident vertex
  and index buffers under the mesh resource key. This is resolver authority
  only; mesh draw/material lowerings remain future engine work. Fensalir's
  evidence DSL can also create generic resource-backed claims, so mesh/page/
  volume resources can plan into backend packets before their selected render
  lowerings exist. Mesh layout authority is split by source: imported/user
  meshes use the standard `PositionNormalUvColor` vertex layout, while generated
  meshes declare `PipelinePrivate` and let the selected lowering own the minimal
  bytes it emits and consumes. TubeField is the first explicit generated-mesh
  render consumer: its compute path emits private vertex/index/indirect buffers,
  and render binds them as a `D3D12PipelinePrivateGeneratedMesh` before applying
  TubeField source/ramp/material state. The DrawIndexed indirect command
  signature is now generated-mesh-owned rather than TubeField-owned, so future
  generated lowerings can reuse the same draw ABI with their own producer
  buffers and material bindings. TubeField dispatch now obeys the selected
  backend packet plan; unplanned `TubeSplineLowering` records are counted but
  not expanded. Fensalir validation also rejects TubeSpline lowering metadata
  whose claim is not Tube-encoded or whose resource differs from the claim
  payload. Mimir's typed surface-intent lowering now emits the matching
  `AquariumFieldTubeSplineLowering` for audio spectrum/waveform Tube claims, so
  the real Mimir producer supplies both the planned packet and the generated
  mesh producer metadata.
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
  offload profiles, OBS publication profiles, CultMesh contracts, Fensalir
  lowering, and assembly plans. Use
  `Mimir.BufferSmoke --perfect-machine-profile-smoke` to prove the catalog
  assembles, `--perfect-machine-contract-smoke` to write a CultCache contract
  proof, `--perfect-machine-manifest` to export the module manifest for
  tooling/UI/remote witness use, and `--perfect-machine-lowering-benchmark` to
  measure the Mimir-to-Fensalir lowering path. The current six-camera/two-audio
  synthetic lowering benchmark runs at roughly 2.5 us per iteration with about
  2.3 KB allocated per iteration.
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
7. Tighten the spectrum spline tube temporal path until it behaves like a
   production debug surface: shader-owned tube SDF coverage, correct
   camera-travel metadata, deterministic direct rendering, and temporal
   accumulation from stable frame budgets. Do not route this dashboard back
   through fractal point splats.
8. Move GPU feature extraction, fusion, material fitting, render budgeting, and
   Spout2 publication into Fensalir. The D3D12 packet/resource resolver now has
   first authority for structured buffers, local textures, surface pages,
   volume textures, and mesh packages, and the evidence DSL can plan generic
   resource-backed claims against those resources. The next engine cuts are
   selected render lowerings that consume those resources as mesh geometry,
   height/SDF/material pages, and density/extinction/SDF3D volumes. The generic
   imported mesh ABI is no longer the blocker; TubeField now occupies the first
   generated-mesh lowering lane. The next renderer decisions are how to expose
   additional generated mesh producers and which page/volume/material lowering
   should follow.
9. Move mic alignment, room suppression, voice separation, spatialization, and
   stem generation into Faust/native DSP.
10. Keep the OBS bridge witness ledger as evidence before expanding receiver
   machinery.
