# Current System Map

Source-level code ownership is mapped in [[docs/code-algorithm-map|Code Algorithm Map]].
The indexed problem-domain map is
[[docs/perfect-machine-domain-index|Perfect Machine Domain Index]]; the
current architecture/optimization/sample-code study lives under
`research/perfect-machine-study-2026-05-23/`.

Mimir is the public Face and product name for this repo. A few lower native ABI
names still use `localcast` until a deliberate rename cut exists.

The live target is the native rolling field machine described in
[[docs/native-rebuild-plan|Native Rebuild Plan]],
[[docs/perfect-machine|Perfect Machine]], and
`config/perfect-machine.example.json`.

## Perfect Machine Target

```mermaid
flowchart TD
    A["direct camera drivers"] --> R["Mimir.Runtime rolling buffers"]
    B["mic/loopback drivers"] --> R
    C["Leap timing/IR driver"] --> R
    D["network feed producers"] --> R
    R --> N["native reservoir handles"]
    N --> E["Fensalir GPU fusion + UI"]
    N --> F["Faust/native DSP"]
    E --> G["Spout2/program video"]
    E --> V["EVE native shared-texture stream"]
    F --> H["program stems + spatial bed"]
    G --> I["OBS"]
    H --> I
```

Ownership:

- Mimir owns configuration, calibration truth, launch, status, persistence, and
  runtime contracts.
- Mimir is a CultMesh app: typed state surfaces for codebooks, schedules,
  calibration receipts, path response, and offload work must be mesh-syncable so
  phones, microcontrollers, Raven, and Starfire can participate without
  becoming independent clock authorities.
- `Mimir.Runtime` owns app-level stream buffers and synchronization.
- Native capture workers own device reads.
- Fensalir owns dense visual fusion, material/brush/splat reconciliation,
  D3D12 interop, runtime UI, Spout2 publication, and the completed backbuffer
  texture copied into the EVE shared D3D12 program-output surface.
- Faust/native DSP owns hot audio alignment, suppression, separation,
  spatialization, and stem generation.
- OBS owns broadcast controls.
- EVE owns native decode/composite for its fullscreen display path; it does not
  own layout, WebKit compatibility, or timing authority.

Invariant: the live window is bounded, in memory, and has one timing authority.
No private history outlives the rolling buffer.

## Viable Stream App

[[docs/viable-stream-app|Viable Stream App]] defines the near-term app target.
Fensalir hosts the running Mimir app, keeps the default five-second runtime in
memory, exposes debug/settings/output controls, and emits synchronized OBS
program video plus separately controllable audio stems.

`Mimir.Runtime` currently provides:

- `MimirSynchronizationHub`;
- `MimirRollingStreamBuffer`;
- stream descriptors/settings;
- `IMimirStreamSource`;
- `MimirNativeIngestStreamSource`;
- `MimirProcessStreamSource`;
- `MimirFrameEventProcessStreamSource`;
- `MimirVideoFrameDescriptor`;
- `MimirAudioSynchronizationAnalyzer`;
- `IMimirVideoCaptureDriver`;
- `MimirVideoCaptureDriverSource`.

Process-backed sources are bridge/network edges. Local cameras should feed
native descriptors with device timestamps and optional native/GPU handles.
The `frame-events`/`json-lines` adapter is a diagnostic witness only: native
probes can emit per-frame JSON metadata so Fensalir sees real sensor cadence in
the rolling buffers while the direct ABI driver is being cut. It does not carry
pixels and does not own the final six-camera hot path. Multi-camera probes are
one process with declared accepted source ids, not one process per camera.
The `ffmpeg-rawvideo` adapter is the live network-pixel edge for Raven setup:
FFmpeg owns SRT receive/decode and writes exact raw frames to stdout;
`MimirFfmpegRawVideoStreamSource` owns frame-boundary reads, BGRA/NV12 payload
geometry, sample descriptors, and insertion into the normal synchronization
path. `config/mimir-runtime.raven-eve.example.json` declares `raven-display`
on SRT port `5200`. EveCanvas owns Eve camera/mic capture directly and sends
typed frame-events to `Mimir.EveSensorReceiver` on WebSocket ports `8793` and
`8794`.

## OBS Bridge Utility

```mermaid
flowchart TD
    A["config/localcast.json"] --> B["sender-start.ps1"]
    C["FFmpeg on sender"] --> D["Windows desktop capture"]
    C --> E["DirectShow audio capture"]
    D --> F["NVENC encode"]
    E --> G["audio encode"]
    F --> H["SRT video endpoint"]
    G --> I["SRT audio endpoint(s)"]
    H --> J["OBS Media Source"]
    I --> K["OBS Media Source per audio source"]
```

The bridge is useful because it is inspectable and already speaks OBS. It does
not become the synchronized program authority.

## Audio Field

The six-microphone path is separate from the bridge. Scarlett speaker loopback
is the current timing authority when active calibration is playing, but Scarlett
production capture belongs on ASIO rather than WASAPI shared mode.
`MimirBioacousticTimeline` owns the active runtime watermark described in
[[docs/bioacoustic-timeline-watermark|Bioacoustic Timeline Watermark]]: a
low-gain birdsong-like word language with 128 self-identifying word positions,
left-speaker and right-speaker variants, four formant-rich syllables per word,
rhythm variation, and direct word identity. Any correctly decoded word identifies
the event index inside the current operating horizon. Mimir queues the left
vocabulary to the left speaker and the right vocabulary to the right speaker
through Fensalir audio, then decodes timing as `bioacoustic` evidence.
`MimirAudioSynchronizationSettings.Mode` chooses whether active calibration is
allowed: `chirp-only` emits the active bioacoustic witness, `passive` stays silent
and uses program-audio phase correlation, and `hybrid` uses passive evidence by
default while emitting active pilot chunks only when passive confidence is weak.
Active decoding does not inherit the passive two-second analysis floor: passive
still needs a longer program-audio window, while the bioacoustic witness can
decode once at least one self-identifying song word is present. The active
decoder is a song-contour anchor machine, not a de Bruijn sequence receiver:
one call carries enough contour, syllable timing, formant, payload, rhythm, and
speaker-tint evidence to identify canonical time inside the operating horizon
and pin multiple time/frequency anchors at once. The bioacoustic detector now
uses bounded motif proposals, matched motif scoring, direct word anchors,
source clock fitting, and fractional waveform refinement.
Pairwise sync compares matched anchors first, then only accepts independent
clock-fit offsets inside the live latency horizon so period aliases do not
become absurd reports. The old chirp-bin detector remains as a
calibration/reference artifact: it carries the full dechirped bin-energy surface
and aggregates decodes into a `MimirChirpBinCalibrationModel` with measured
usable bands, expected-symbol versus observed-bin confusion observations,
timing residuals, delay hypotheses, phase summaries, and an adaptive codebook
plan. The analyzer keeps raw profiles even when no timing report is accepted,
so physical mic failures can still guide bioacoustic motif weighting. Reports/states
expose `delayUs` next to fractional sample delay.
`Mimir.BufferSmoke --bioacoustic-self-test` proves direct word anchors.
`--standalone-bioacoustic-self-test --sample-rate 48000 --delay-samples 1269.5`
proves a receiver with only codebook/schedule state can recover delayed source
time to below printed microsecond precision. `--chirp-only-sync-self-test
--sample-rate 48000` recovers a 317.375-sample synthetic delay with printed
0.000 us error using `evidence=bioacoustic`.
`--bioacoustic-train` is now the receipt-backed tuning harness: it runs
multiple indexed cepstral decoder hypotheses across mel-cepstral warp/blur
degradations and writes typed CultCache results plus pre-warp, post-warp, and
reconstructed-from-detections WAV artifacts. The latest local receipt under
`artifacts/bioacoustic-training/bioacoustic-20260524-103438/` shows identity
survives many degradations, but timing still fails under warped domains, so the
next receiver cut is a global delay/clock/path hypothesis over detected words.
Reports now carry fractional delay and per-band matched energy. The older
`MimirChirpletSymbolCodebook` / `MimirChirpletStreamDecoder` path remains a
diagnostic reference for constrained chirplet-transform work. The active
receiver is the bioacoustic motif timeline; the analyzer only emits active
timing reports from matched canonical anchors. The hub also owns cached reports
plus smoothed per-source sync state with delay-slope/SRO in ppm.
`MimirRuntime` runs analysis
online as a bounded rotating service and can print live telemetry with
`MIMIR_SYNC_TELEMETRY_SECONDS`. UI and telemetry are passive readers of cached
sync state; they must not invoke the analyzer. Current app testing proves
loopback wakeup, live mic buffers, and confident online sync states, but the
next proof is stable canonical anchors through real mic streams. Camera mics are
spatial/context witnesses; Focusrite devices are dialogue anchors. Fractional
delay and the hot resampler belong in Faust/native DSP.

The WASAPI cadence probe is now a format/state diagnostic: it can request
shared or exclusive sample rates, bit depths, channel counts, and float/PCM
formats, then report the selected or closest format. The Focusrite driver stack
is now installed and registers `Focusrite USB ASIO`. The Scarlett Solo 4th Gen
is attached to Starfire and exposes 4 ASIO inputs / 2 outputs: `Input 1`,
`Input 2`, `Loopback 1`, and `Loopback 2`. `native/probes/asio_audio_cadence`
can instantiate that driver, verify 44.1-192 kHz support, and capture nonzero
4-channel `Int32LSB` callbacks at 192 kHz with 192-frame preferred buffers. The
runtime analyzer accepts Float32, Int16, Int24, and Int32 PCM windows so
ASIO/native capture can preserve the interface format. Raven also has a
loopback-capable Scarlett ASIO path at 192 kHz for co-streamer/game timing
evidence; Starfire still owns the heavy soundfield and sensor-fusion work.
The ASIO probe can now play raw mono Float32 timeline audio through the
Focusrite outputs while capturing every ASIO input as raw interleaved Float32.
`Mimir.BufferSmoke --analyze-asio-f32` feeds those captured channels into the
same runtime analyzer. `--calibrate-chirp-bin-asio-f32` computes and persists
the response/confusion/delay model per output/mic path, and
`--analyze-asio-f32 --calibration ...` loads it into the chirp-bin reference
decoder.
`native/asio_capture` and `MimirAsioStreamSource` are now the runtime Scarlett
path: Focusrite ASIO callbacks feed sample-bearing 192 kHz Float32 blocks into
`Mimir.Runtime` in process, without the diagnostic JSON/stdout bridge. The
minimal `config/mimir-runtime.asio.example.json` proof ingested more than
12,000 sample-bearing 192 kHz blocks across `asio-ch0` through `asio-ch3` in
two seconds and retained 2,048 blocks per channel, all from the same ASIO
callback stream. A real
192 kHz chirp-bin Scarlett artifact decoded
`Loopback 1 -> Loopback 2` at exactly `0.000 us` with 12 matched anchors and
0.999 confidence. The same analyzer now prints calibration profiles for
decoded sources. Physical input 1 still fails pairwise timing in the stored
artifact, but it leaves a concrete response profile: 14 frames, 12 anchors,
0.865 clock confidence, and strongest bins around 4525, 4075, and 7225 Hz.
Acoustic robustness is now the open problem, not clean loopback timing,
standalone decoder shape, or basic response evidence.
`Mimir.BufferSmoke --calibrate-contestant-asio-f32` now owns the active
packet-song physical calibration receipt. It runs the canary packet decoder
against interleaved ASIO Float32 captures, performs a fast per-channel global
delay hypothesis search, then tight scheduled packet scoring with sub-sample
refinement. The latest persisted model at
`calibration/bioacoustic/scarlett-canary-packet-192k-rerun.json` ran at 10.7x
realtime across four 192 kHz channels and records per-channel polarity,
schedule offset, payload reliability, gain, response-normalization bands, and
pairwise propagation delay. Current physical precision is loopback 2.524 us
MAE, co-streamer shotgun 58.785 us MAE, and cardioid 90.558 us MAE; do not
claim physical microsecond sync until those mic MAEs collapse by another order
of magnitude.
A fresh 192 kHz room run after the song-contour authority cut is persisted at
`calibration/bioacoustic/scarlett-canary-packet-192k-contour-fresh.json`.
It clears the 10x realtime budget, keeps loopback at 2.524 us MAE, improves the
co-streamer shotgun to 37/37 payload with 34.083 us MAE, and improves the
cardioid event count to 31/37 while still measuring 92.996 us MAE. The next
precision cut must extract intra-call contour anchors, not only one offset per
packet word.
The first anchor-rich canary packet is persisted at
`calibration/bioacoustic/scarlett-canary-packet-anchor-rich-192k.json`.
It adds timing chips, formant pivots, harmonic-envelope notches, payload
ornaments, renderer-level template caching, and per-event intra-call anchor
measurements. It clears 10x realtime and improves loopback to about 2.23 us
MAE, but physical mics do not improve yet: shotgun is 36/37 payload at
56.365 us MAE and cardioid is 27/37 payload at 101.522 us MAE. Treat this as
anchor observability, not a finished anchor geometry. The next cut should learn
which anchor kinds survive each acoustic path and weight or reshape them.
Path-level loopback truth is now part of the calibration receipt: candidate mic
anchors are matched against loopback anchors, then event-local waveform
correlation against the captured loopback packet refines path delay. On the
stored anchor-rich capture this improves physical path precision to 7.576 us
for the cardioid and 6.578 us for the shotgun while Release runs at 21.1x
realtime. A fresh capture at
`calibration/bioacoustic/scarlett-canary-packet-anchor-rich-latest-192k.json`
keeps the shotgun around 5.916 us but worsens the cardioid to 18.930 us. The
rejected razor timing-chip mutation made the mics worse and should not be
revived without a better hypothesis. The remaining gap to one microsecond is
phase/group-delay correction or a more survivable direct-path anchor family.
A naive recursive waveform phase-lock pass was also rejected: it improved one
cardioid receipt but worsened the shotgun and locked a fresh cardioid run onto
a later reflection lobe. Recursive refinement remains a good architecture only
after the path model can distinguish direct arrival, phase/group delay, and room
reflection energy. The follow-up sonar/DSP research note at
`research/sonar-dsp-recursive-refinement-2026-05-25.md` maps that correction:
use complex matched filters, phase-slope/group-delay fitting, acquisition versus
tracking loop bandwidths, and sparse multipath residuals before attempting
another recursive fitter.
The first implementation slice now exists in
`MimirComplexContourMatchedFilterBank` and `MimirDirectPathTracker`: known
canary packet anchors become complex matched-filter responses with multiple
candidate lobes, then the tracker uses the current path prediction as authority,
selects the coherent direct-path cluster inside that gate, and reports later
clusters as reflection taps. Synthetic 192 kHz reflection smoke currently lands
about 5.249 us from the expected delay; stored Scarlett shotgun runs land within
about 5.860 us and 7.247 us of the seeded path fits, while cardioid runs land
within about 15.817 us and 8.963 us. This is the correct receiver shape, but
not yet the final phase/group-delay channel model. The tracker now emits
per-band delay/phase residual observations and has a
`MimirDirectPathChannelModel` correction surface for later multi-window
calibration; one-window self-correction is not allowed to become authority.
`calibration/bioacoustic/complex-contour-replay-panel.json` is the current
persisted receipt for this surface across stored/fresh shotgun/cardioid cases.
`MimirDirectPathChannelModel` now applies learned per-band delay and phase
correction when explicitly supplied, and downweights bands outside the learned
usable surface. `Mimir.BufferSmoke --learn-complex-contour-channel-model`
persists the current path-scoped model at
`calibration/bioacoustic/complex-contour-channel-model.json`; it learns three
usable cardioid bands and six usable shotgun bands from the four-case replay
receipt. `--evaluate-complex-contour-channel-model` writes
`calibration/bioacoustic/complex-contour-channel-model-evaluation.json`: the
model improves 3/4 absolute path-seed errors and lowers mean cluster MAE, with
the fresh cardioid reaching about 0.202 us from the seed, but the stored
cardioid still worsens to about 17.476 us. Treat the model as explicit
calibration evidence, not default runtime authority, until more captures prove
the path surface stable.
The complex contour receiver now has a live runtime lane instead of living only
inside BufferSmoke artifact replay. When `enableComplexContourRuntime` is true,
`MimirRuntime` emits the configured `bioacousticWitnessProfileId` through
`MimirBioacousticContestantRenderer`, `MimirSynchronizationHub` loads
`complexContourChannelModelPath`, and `MimirComplexContourRuntimeAnalyzer`
extracts Float32 windows from rolling audio buffers to publish
`evidence=complex-contour` reports. The synthetic runtime-shaped proof
`--complex-contour-runtime-self-test` at 192 kHz recovers a 693.5-sample delay
with about 0.219 us error from rolling buffers. The next real-world proof is to
run that lane through live Scarlett loopback and mics, not only stored ASIO
artifacts.
That real-world proof now has a first receipt:
`calibration/bioacoustic/complex-contour-live-20260525-063229.md`. A freshly
rendered canary-packet witness was played through Focusrite ASIO and captured
from all four 192 kHz Scarlett inputs. Loopback 1 to Loopback 2 measured
-0.014 us, the shotgun path landed -3.984 us from its prior path seed, and the
cardioid path landed +1.547 us from its prior path seed with the current
channel model loaded. This proves the contour/channel-model path survives a
fresh DAC/speaker/room/mic/ADC pass, but low physical confidence means the next
cut is stronger direct-path confidence and independently measured path truth,
not a victory lap.
A follow-up receipt,
`calibration/bioacoustic/complex-contour-live-20260528-002313.md`, keeps the
interface loopback proof healthy: `asio-ch2` to `asio-ch3` measured `-0.062 us`
with `0.956` confidence and 257 direct hits. Physical inputs are not currently
usable timing signals: `asio-ch0` and `asio-ch1` produced zero contour hits
against 1480 reference hits, with very low RMS. The next physical proof is
blocked on mic gain/routing/source placement before receiver logic changes.
After arming both mics, `complex-contour-live-20260528-080604.md` showed both
physical inputs were alive but was accidentally routed to headphones. The
monitor-routed proof `complex-contour-live-20260528-080727.md` is the first
credible fresh physical contour receipt: loopback is `-0.014 us` at 0.971
confidence, `asio-ch0` lands `-0.410 us` from its stored path seed at 0.524
confidence with 24 direct hits, and `asio-ch1` lands `-2.467 us` at 0.363
confidence with 14 direct hits. User-provided physical mapping: `asio-ch0` is
the shotgun, about 1.5 m from the right monitor and 3.0 m from the left;
`asio-ch1` is the cardioid, about 0.5 m from the left monitor and 1.5 m from
the right. Repeat captures are needed before updating the channel model or
letting physical mic reports drive actuator authority.
`complex-contour-monitor-split-20260528-081813.md` adds
`--play-output-channel` to the ASIO probe and captures output 0 and output 1
separately. Loopback proves the ASIO output buffers are distinct, but physical
mic delays are nearly identical across outputs: shotgun differs by about
20.885 us and cardioid by about 24.789 us. That does not match the declared
left/right monitor geometry, so the downstream monitor path is probably
summing, mirroring, or otherwise not isolating the physical speakers.
The first actuator proof now exists: `faust/mimir_alignment_actuator.dsp` owns
six channels of bounded fractional delay/gain controls for Faust/native DSP,
and `Mimir.BufferSmoke --bioacoustic-actuator-self-test --sample-rate 48000
--delay-samples 317.375` proves the control loop shape by estimating a
bioacoustic delay, applying fractional correction, and remeasuring residual
below printed microsecond precision. `MimirAlignmentActuatorBank` is now the
runtime command owner above that Faust surface: it converts smoothed sync states
into nonnegative holdback commands, keeps source-to-`sourceN` slots stable,
reports overflow beyond the six-source profile, and exposes the reference
holdback separately. `MimirRuntime` updates that command frame on the sync
analysis cadence, queues it into `AquariumAudioDocument` as an engine audio
control frame, declares `faust/mimir_alignment_actuator.dsp` as an
`AquariumStreamingDspProgram`, and telemetry can print the current DSP targets.
Mimir also queues sample-bearing `AquariumStreamingAudioBlock` lanes from the
latest actuator source buffers, using the bank-assigned Faust `sourceN` control
path as channel authority. Fensalir can compile/host/process that persistent
Faust graph now. The engine now publishes processed output lanes as
`AquariumAudioStemFrame` records through `IAquariumAudioStemBus`, with Mimir's
actuator program declaring deterministic aligned-source stem names. OBS-facing
publication now has a first readiness surface:
`MimirObsStemPublicationState` consumes those frames without copying audio and
reports ready, missing, and unconfigured stems against the alignment actuator
stem-bus profile. `MimirObsStemSharedMemoryPublisher` writes the validated stem
frames into `Local\MimirObsStemBus`, and `native/obs_stem_source` is the first
OBS source plugin bundle: `Mimir Audio Stem` reads one named stem from that map
and submits it through libobs audio, while `Mimir Program Texture` opens
Fensalir's named shared D3D12 program-output texture and draws it through an
OBS-readable shared D3D11 bridge texture. The video path is GPU-to-GPU and does
not require Spout2 or CPU readback; the remaining copy is the explicit D3D12 to
libobs-D3D11 boundary. Fensalir publishes `Global\MimirFensalirProgramFence` as
a program-output publication fence, and the OBS source opens that fence before
copying. This prevents blind read-before-producer-completion without coupling
OBS to Fensalir's private frame fence. An optional texture ring is available
when Fensalir and OBS agree on `FENSALIR_PROGRAM_OUTPUT_RING_COUNT`; OBS selects
the latest completed slot from the program-output fence value. OBS can publish
`Global\MimirObsProgramConsumerFence`; when Fensalir is launched with
`FENSALIR_PROGRAM_OUTPUT_CONSUMER_FENCE_NAME` pointing at that fence, it skips
publication instead of reusing a ring slot OBS has not acknowledged.
`scripts/build-obs-stem-plugin.ps1` stages the upstream OBS plugin-template SDK
under `artifacts/obs-sdk/` and builds the plugin DLL locally.
The rendering migration's program-output receipt phase is structurally
complete: OBS consumes final Mimir/Fensalir surfaces and stems through the
native plugin, raw feeds remain debug inputs, and the ledger records the live
program texture, texture ring, dedicated producer fence, and consumer-fence
proofs.
The public self-hosted edge has its first deployable path. `src/Mimir.Broadcast`
builds the Starfire-side FFmpeg/NVENC push command for
`rtmp://127.0.0.1:11935/live/mimir`; the port is an SSH local forward to
Yggdrasil's localhost RTMP listener. Yggdrasil owns nginx RTMP ingest and HLS
segment serving only. It must not transcode or compose in v1. The public static
viewer route is `https://gamecult.org/livestream`, owned by the root
`gamecult-site` repo, and it reads
`https://streampixels.gamecult.org/mimir/live/hls/mimir.m3u8`. On 2026-05-30
the deployed Yggdrasil origin validated nginx, listened on `127.0.0.1:1935`,
returned `mimir-live-ok`, and generated four HLS segments plus `mimir.m3u8`
from an eight-second Starfire synthetic NVENC push. `live.mimir.gamecult.org`
is no longer required for v1 because StreamPixels already has DNS and TLS on
Yggdrasil, and Mimir's own static site does not own the viewer.
`MimirSynchronizedBufferPlanner` is now the low-level aligned-buffer primitive:
it picks one canonical presentation time inside the retained rolling window and
returns per-stream slices for cameras, network display feeds, and audio. Timing
corrections may target either one source or a shared `clockDomainId`. The Raven
shape is explicit: Raven controls both display pixels and an audio timing signal
routed into Scarlett, so Scarlett-decoded Raven audio evidence can earn a
`raven-sync` correction that is applied to the `raven-display` buffer. Network
arrival timestamps remain metadata, not timing authority.
The first operational ingest config is `config/mimir-runtime.raven-eve.example.json`:
Raven screen capture arrives over SRT and decodes to raw BGRA through FFmpeg;
Eve camera/mic arrive as EveCanvas-native WebSocket frame-events. All three
enter the same rolling-buffer model as local sources. Eve's sensor authority now
lives in EveCanvas, not an external camera-streaming app.
`MimirPresentationControlState` now owns the Fensalir program-control intent:
video feed visibility/solo/opacity/layer order, audio mute/solo/gain, and
global LUT preset selection. The `Mimir Program` panel exposes those controls
without giving OBS or bridge endpoints composition authority. Video controls
filter production surface intents before Fensalir composition; audio controls
modify Faust gain controls and sample gain before Fensalir streaming DSP. LUT
presets are typed postprocess state, with exposure/bloom currently mapped into
`GraphicsSettings` and LUT texture sampling left as the next renderer hook.
`MimirSceneEditorState` is now the separate Mimir-window editor owner. It owns
the editor camera, dynamic scene nodes, selected node, visibility/lock state,
2D-plane transforms, reset commands, and grab/rotate/resize gizmo mode. Rolling
video buffers derive sensor-feed panel nodes; SDF text panels and model import
requests create editor graph nodes. `MimirRuntime` presents that editor through
the Fensalir camera plus derived spline outlines, selection handles, and the
`Mimir Editor` hierarchy/transform panel. This editor is not the OBS program
output. World SDF glyph rendering, ASSIMP-style mesh decoding/upload, and
pixel-accurate gizmo hit-testing are explicit Fensalir renderer cuts, mapped in
[[docs/scene-editor-control-surface|Mimir Scene Editor Control Surface]].
`Mimir.EveDashboard` is the first native Eve operator dashboard server. It
serves `/eve/dashboard` snapshots and accepts selected-node transform,
visibility, and reset commands. EveCanvas renders the dashboard locally in
UIKit, including the scene graph and multitouch pan/pinch/rotate source panels.
The current server state is fixture state for the transport/control proof; the
next cut is binding it to live `MimirPresentationControlState` and
`MimirSceneEditorState`.

## Visual Fusion

Visual fusion belongs in Fensalir over current reservoir claims. Native capture
workers provide frames; Fensalir owns feature extraction, matching, material
fitting, render budgeting, and publication.

Fast stereo depth is now mapped as a future Fensalir D3D12 compute lane, not an
external library dependency. The provenance note
`research/d3d12-stereo-depth-provenance-2026-05-29.md` records `libSGM` as the
current permissive north-star reference: Mimir/Fensalir should rebuild the SGM
shape over synchronized rectified texture pairs, calibration state, typed GPU
resources, and explicit fences. CUDA, TensorRT, and monocular depth demos remain
research references only.
The first live contract exists: `MimirStereoDepthConfigurations` names the
libSGM-provenance D3D12 SGM profile, and
`MimirFensalirFieldLowering.BuildStereoDepthCandidateFrame` lowers caller-
declared rectified stereo input textures into a GPU-resident compute-writable
disparity SurfacePage, confidence texture, Height FieldEvidence claim, and
`AquariumFieldStereoDepthLowering` sidecar. The live
`BuildLeapPackedStereoDepthCandidateFrame` path now recognizes a live
`LeapStereoIr` rolling video window, reuses its declared packed R8G8 texture as
both left and right resource identity, names the left/right observations as
packed R/G lanes, and emits the same depth lowering automatically from the
runtime frame. The lowering sidecar is the dispatch contract: it binds the
libSGM-provenance profile, calibration id, camera pair, inputs, disparity
output, min disparity, disparity levels, aggregation paths, census radius,
P1/P2 smoothness penalties, and depth range. The current D3D12 SGM profile
publishes min disparity 0, 128 disparities, four paths, census radius 2, P1 8,
and P2 96. Fensalir now has a first packed-Leap D3D12 kernel that writes R16F
disparity through a crude SAD/block-match pass. This is intentionally a small
kernel-shaped proof, not full libSGM-style SGM or calibrated metric depth yet.
The first point-cloud root is also code-visible now:
`MimirPointCloudConfigurations.LeapDisparityPointCloudRoot` records the
stereo-disparity projection profile and provenance, while
`MimirFensalirFieldLowering.BuildLeapPackedStereoPointCloudCandidateFrame`
declares a GPU-resident `FieldMesh` point-list resource derived from the Leap
R16F disparity SurfacePage. `MimirRuntime` merges that Mesh claim into the same
evidence frame as the Leap stereo-depth lowering. `Mimir.BufferSmoke
--leap-point-cloud-root-smoke` proves the combined depth/point-cloud contract
plans as one `SurfacePage` packet and one `Mesh` packet with zero deferred
requests. Fensalir branch `codex/leap-packed-depth` now has the first render
lane too. It preserves Mesh `SourceUri=derived-from:*`, allocates generated
point-list vertex/index buffers as UAV-capable GPU resources, fills them from
the disparity SurfacePage in `D3D12PointCloudFromDisparityCS`, and renders the
standard `PositionNormalUvColor` PointList through `D3D12PointCloudPS`. The
remaining truth gap is live Leap/editor verification, calibrated projection
constants, full SGM, and a global residual/calibration owner.

Fensalir must not be treated as a traditional rendering pipeline. Mimir does
not ask it to "draw a thing" and hope post-processing makes the result true.
Mimir submits evidence, buffers, constraints, and surface intent; Fensalir
lowers those into field claims with travel/depth, metadata, control, and
reservoir-guide lanes. If a path only paints pixels, it is a fallback/debug
draw, not the Perfect Machine surface.

The live payload boundary is now explicit. Mimir declares each live native/GPU
payload view as an `AquariumFieldResourceDeclaration` with resource key, kind,
residency, shader access, format, shape, valid time range, version, and native
handle metadata. Field claims and lowering requests reference those
`mimir:resource:*` keys. The handle string is only a name; Fensalir validation,
planning, and renderer-owned resource slots own typed resource resolution before
shader lowering. Mimir-declared rendering buffers are Fensalir GPU resources in
the same runtime; native/shared handle metadata is an import edge, not a
separate authority regime. Rendering-relevant buffers move to GPU residency as
early as possible and stay there; CPU readback/tessellation is diagnostic only.
Camera descriptors now enter this bridge too. The preferred hot path is
Fensalir-owned texture leasing: the runtime receives `AquariumRuntimeServices`,
asks Fensalir's field resource broker for a keyed D3D12 `Texture2D` lease, the
camera/decode producer writes that shared texture and signals the producer
fence, then Mimir carries the same resource key through FieldEvidence. Fensalir
waits on committed producer fence values before resolving shader reads. Shared
foreign texture handles remain an import edge, not the primary camera authority.
`MimirVideoCaptureDriverSource` propagates the lease client to drivers that
implement `IMimirFensalirTextureLeaseReceiver`, so direct camera drivers can
request the destination texture before emitting a frame descriptor.
Each camera backend must use the closest-to-device path available and expose
its unavoidable copy count. Managed/process wrappers are diagnostic bring-up
surfaces, not the production camera hot path.
For raw single-plane system-memory frames, `MimirVideoCaptureDriverSource`
performs the one declared CPU-to-GPU upload into the Fensalir lease, drops the
managed payload from the live sample, and increments `UnavoidableCopyCount`.
NV12 CPU upload is rejected until Fensalir owns a real planar upload path;
device/GPU NV12 producers should write the leased texture directly.
`MimirKsVideoCaptureDriver` plus `native/camera_capture/mimir_camera_capture.dll`
is the first production-shaped KS/UVC driver: it opens a Kernel Streaming pin
in process, queues uncompressed UVC frames, and feeds the same
`IMimirVideoCaptureDriver` source path. MJPG/H264 still need a device/GPU
decode producer; the KS driver rejects compressed capture formats.
`MimirPs3EyeVideoCaptureDriver` plus
`native/camera_capture/mimir_ps3eye_capture.dll` does the same for PS3 Eye raw
WinUSB/libusb capture, emitting Bayer8 frames through the common upload lane.
Driver smokes have pulled and uploaded real frames for LeapUVC 640x240 YUY2,
both PS3 Eyes at 320x240 Bayer8, Kiyo Pro 1920x1080 YUY2, and regular Kiyo
640x480 YUY2. Regular Kiyo 1280x720 YUY2 did not open.
`MimirMediaFoundationGpuVideoCaptureDriver` plus
`native/camera_capture/mimir_mf_gpu_capture.dll` is the compressed camera path:
Media Foundation SourceReader runs with a D3D11 device manager, decodes MJPG or
H264 camera frames on the GPU, copies the decoded GPU surface into a shared BGRA
D3D11 texture, and publishes a `shared-d3d11-texture` handle with no live CPU
payload bytes. Kiyo Pro 1920x1080 MJPG->RGB32 and H264->RGB32 smokes both
produced valid GPU handles/resources. Direct NV12 shared texture creation
failed in D3D11, so NV12 needs plane-aware interop or a GPU bridge-copy cut.
Metadata-only cadence frames do not create camera surface intents or fake
payload requests. The old direct `AquariumGpuSensorFrame` bridge proof has been
removed from Mimir's active proof path. Camera image claims currently defer
until Fensalir owns a selected visual-fusion lowering for camera textures.
The current smokes prove planning, not visible packet rendering:
`--fensalir-field-evidence-smoke` now produces one planned resource-backed
`TubeField` packet from Mimir's spectrum intent, and
`--fensalir-field-dsl-resource-smoke` proves the Fensalir DSL can bind the same
kind of declared resource directly. `--fensalir-camera-observation-smoke`
proves the camera observation/resource split. Fensalir now has the first D3D12 resolver
cut for structured/curve buffers: it imports shared GPU buffers by handle and
allocates engine-owned GPU slots only for Fensalir-produced resources. The DSL
can describe a 2D rolling float buffer as Catmull-Rom XY tubes with modulo
column addressing, amplitude power/normalization, radius, ramp texture path, and
emission scale. Fensalir now resolves the first non-buffer resource family too:
Texture2D local assets bind as TubeField ramps, surface pages and volume
textures allocate shader-readable GPU textures, and mesh packages own
`Mesh.Vertices`/`Mesh.Indices` GPU buffer shape under one resource key. The DSL
can plan generic resource-backed claims over those declarations. The next
blocker is no longer resolver ownership; it is selected render lowerings that
consume camera/feature textures, mesh/page/volume resources as geometry,
height/SDF/material pages, or density/extinction/SDF3D domains. Mesh layout
authority is split by source:
imported/user meshes use the standard `PositionNormalUvColor` layout, and
generated meshes can be `PipelinePrivate` so each lowering owns the bytes it
emits and consumes. TubeField is the first concrete generated-mesh consumer:
its compute pass emits private vertex/index/indirect buffers and render binds
them as `D3D12PipelinePrivateGeneratedMesh` before applying source/ramp/material
state. The DrawIndexed indirect command signature has been lifted to the
generated-mesh lane instead of being TubeField-owned. TubeField expansion now
requires a planned TubeField backend packet for the lowering's claim, and
validation rejects TubeSpline metadata whose claim/resource authority is split.
Mimir's typed surface-intent lowering now emits the matching TubeSpline
lowering for audio spectrum/waveform Tube claims.
Mimir's active proof path also no longer uses the direct
`AquariumAcousticFieldFrame` builder: sync states lower into FieldEvidence
calibration constraints. Fensalir now selects audio-path `Phase` and
`Confidence` claims as `DebugOverlay` backend packets, so calibration
timing/path evidence can be inspected through the same FieldEvidence authority
while true acoustic source candidates and confidence volumes remain later
fusion work. The first synthetic acoustic source candidate proof now exists:
Mimir lowers an SRP/PHAT-style localized source with calibration/source
identity and a world-space confidence envelope, and Fensalir plans it as a
FieldEvidence `DebugOverlay` packet. Live room geometry must still come from an
explicit measured coordinate frame, not inferred from the recent two-distance
monitor notes. Phase 5's synthetic marker proof now exists too: Mimir lowers a
deterministic multi-camera marker candidate with calibration/marker identity and
world-space support, and Fensalir plans it as `DebugOverlay` evidence while
ambiguous raw camera features still defer.

The current teardown/migration map is
`docs/fensalir-rendering-rebuild-migration.md`, paired with Fensalir's
`docs/rendering-teardown-rebuild-protocol.md`. The intended cut is explicit:
Mimir publishes typed physical observations, calibration constraints, and
surface intent; Fensalir owns field domains, field claims, candidate selection,
track/reservoir state, selected lowerings, temporal guide lanes, and
presentation.

The reservoir can resolve pixel-level claims without requiring pixel-sized
contents. Claim support belongs to the represented domain. A smooth flat surface
can be a few huge surface claims; a heightfield terrain belongs in a quadtree of
brush-painted surface tiles, subdivided only where projected footprint,
curvature, material/brush detail, silhouette risk, or temporal uncertainty make
larger claims dishonest. Fensalir's SDF probe/lowering stage should emit
surface splats at that detail-adaptive density, not one splat per pixel by
default.

The live debug spectrum view is now field-evidence owned. Mimir no longer
submits direct `AquariumSplineFrame` trails for the spectrum dashboard and does
not submit `AquariumBufferFieldFrame` / fractal `ReservoirSplats` for it either.
Mimir declares the rolling resource, publishes a Tube claim plus
`AquariumFieldTubeSplineLowering`, and Fensalir owns planning, generated mesh
expansion, and TubeField material rendering. The spectrum resource is not just
a handle: Mimir uploads the current spectral amplitudes as row-major Float32
data into a Fensalir-owned GPU structured buffer before TubeField compute reads
it. TubeField writes stable field ids, real tube normals, coverage/confidence,
and domain-validity guide data into the same scene metadata/control/reservoir
guide targets consumed by reservoir resolve for spatiotemporal reconstruction.
Fensalir's shared reservoir history update now runs as a compute pass over
four structured rows per pixel and emits the resolved HDR field texture for
bloom/presentation, so the presentation shader is no longer a hidden history
owner.
TubeField now feeds the shared reservoir from GPU-resident rolling-buffer
column packets instead of generated segment packets. Fensalir emits one column
packet per logical spectrum history/source column, bins those columns into
screen tiles, and evaluates Catmull-Rom tube SDF/material samples directly from
the source buffer during the ReSTIR passes. Generated mesh draw args are zeroed
for the live path and remain only a diagnostic reference.
`Mimir.BufferSmoke --mimir-spectrum-upload-smoke` verifies this through the
actual `MimirRuntime` frame path and confirms the legacy direct spline and
buffer-field dashboard inputs are empty. The same path declares the local
blackbody ramp as a Texture2D field resource and binds the TubeField material by
resource key. Runtime spectrum columns now represent `(history age, source
lane)` pairs rather than only the latest spectra; Mimir emits one TubeField
claim/lowering per source, each striding through the shared resource to render
its own age trail at `z = age * 0.1`. The resource advertises fixed history and
source-lane capacities, so content updates and source topology changes do not
resize the GPU buffer while the rolling trail fills. `MIMIR_SPECTRUM_SOURCE_LANES`
sets the fixed lane budget; surplus sources are truncated and reported in the
runtime UI. Physical history slots are now addressed as a ring with
`RollingOffset`, so logical age no longer requires moving every column before
shader sampling. Mimir emits one Float32 resource upload for the newest
spectrum ring slot with an explicit element offset instead of uploading the
full fixed-capacity backing buffer every frame. Fensalir owns rolling-slot
validity and clamps TubeField dispatch so invalid older slots are not sampled
after renderer allocation, reset, or partial update.
`MIMIR_SPECTRUM_TUBE_SUBDIVISIONS` is the runtime cost lever for lowering
Catmull-Rom subdivision count when Fensalir reports TubeField truncation.

## Known Risks

- Windows device names vary by driver and localization.
- Some FFmpeg builds omit SRT or NVENC.
- Separate bridge endpoints can drift.
- OBS SRT reconnection behavior can be fussy.
- Direct driver work must prove sustained cadence before it becomes timing
  authority.
- PS3 Eye audio endpoints are enumeration/runtime-fragile. A later replug made
  both mic buffers emit 480-frame WASAPI blocks again while both PS3 Eye camera
  buffers were live.
