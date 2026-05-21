# Implementation Plan

## Current Cut Line

`docs/native-rebuild-plan.md` is now the active plan.

No more Python live hot path. No more OpenGL production Spout sink. No more JSON
live data stores. Edge JSON may define CultNet schema or export diagnostics, but
the real process/network data stays typed because every subsystem is under our
control.

The native reservoir is now one time-ordered rolling buffer with typed views.
The previous shared-edge typed rings proved the retention invariant; the new
shape removes independent per-kind storage from the live foundation.

The near-term viable stream app is Aquarium-hosted: it ingests local or
networked video/audio feeds as producers into the same five-second in-memory
`LocalcastRuntime`, exposes debug/settings/output control through Aquarium UI,
and emits synchronized OBS program video plus separately controllable audio
stems. Mimir now owns its own C# solution while referencing Aquarium Engine as
the windowing/rendering library and D3D12 bridge. `docs/viable-stream-app.md`
is the current cut line for that app.

The v1 OBS bridge is now gated by a bounded smoke-test witness ledger. No
plugin/native receiver expansion is justified until
`calibration/runs/obs-v1-smoke-ledger.json` records sender capture, SRT receive,
and OBS presentation timestamps for every planned endpoint with bounded drift,
latency, and confidence.

## Implemented

- Mimir public identity and repo Face:
  - `README.md` now presents the project as Mimir: local sensors, coherent
    field.
  - `Mimir.png` is the user-repixelized 256px avatar source.
  - `assets/branding/mimir-avatar-256.png` is the project branding copy.
  - `.voidbot/voice/mimir.png` and `.voidbot/voice/identity.json` are the
    VoidBot-facing persona surfaces for the Mimir Face.
  - `.voidbot/state/mimir.cc` is the typed persistent Face state. It is seeded
    with durable birth memories for Mimir identity, native-reservoir target,
    ownership boundaries, audio-field discipline, and heartbeat agency pressure.
  - `docs/mimir-face.md` defines Mimir's jurisdiction, voice, heartbeat
    behavior, and inherited memories. VoidBot is only the mouth/scheduler.
- Perfect Machine contract:
  - `docs/perfect-machine.md` defines the target native machine: one five-second spatiotemporal reservoir, Aquarium-owned dense visual fusion/material/brush rendering, Faust-owned hot audio DSP, OBS-owned broadcast controls, and Mimir-owned calibration/config/status.
  - `docs/viable-stream-app.md` defines the first useful application cut:
    Aquarium hosts the in-memory five-second runtime, owns runtime UI and
    output management, treats local/networked feeds as producers, and emits
    synchronized OBS program surfaces.
  - `Mimir.slnx` contains `src/Mimir.App` and `src/Mimir.Runtime`.
    `Mimir.App` launches `AquariumHost` from Aquarium Engine, and
    `Mimir.Runtime` supplies the Aquarium client runtime factory.
  - `Mimir.Runtime` now owns `MimirSynchronizationHub`,
    `MimirRollingStreamBuffer`, stream descriptors, and the `IMimirStreamSource`
    adapter seam. It initializes one bounded rolling buffer per configured
    audio/video stream and polls registered local/network sources each frame.
    Local devices should feed through native push adapters; process-backed
    sources are compatibility edges for network/diagnostic command ingest, not
    the six-camera capture foundation.
  - `MimirVideoFrameDescriptor` is the first video-frame hot-path descriptor:
    dimensions, pixel format, stride, device timestamp, and optional native/GPU
    handle. Leap stereo IR should enter Mimir through this native descriptor,
    with OpenCV kept out of the hot loop.
  - `IMimirVideoCaptureDriver` and `MimirVideoCaptureDriverSource` define the
    live driver-facing seam. LeapUVC/libusb, LeapC images, Media Foundation,
    DirectShow, or GPU shared texture capture should plug in here.
  - `config/perfect-machine.example.json` declares the contract shape for six cameras, six microphones, reservoir typed views, native authorities, outputs, and the demotion of bridge scripts to tooling.
  - `native/reservoir` is the first native Rust crate. It implements one
    shared-edge five-second rolling buffer with typed views and proves that
    every kind expires from the newest live sample.
  - `native/reservoir/include/localcast_reservoir.h` exposes the initial
    Aquarium/Faust C ABI: opaque reservoir create/destroy, sample-handle push,
    edge/window queries, ring counts, and latest sample lookup by sensor hash.
    The ABI now exposes total rolling-buffer length and typed-view length.
  - `localcast_hash_source_id` owns stable FNV-1a source hashing for native
    producers; empty/null source ids produce zero and are rejected.
  - `localcast_producer_create_for_source` is the preferred producer constructor
    for adapters because it keeps source-id hashing inside the native ABI.
  - `LocalcastRuntime` wraps the current reservoir with typed native producer
    calls and total/typed read functions. It is the intended live spine for
    Aquarium/Faust bindings.
  - `LocalcastProducer` owns native ingress source identity and sequence
    assignment before appending live handles into `LocalcastRuntime`.
  - `LocalcastAudioBlockDescriptor` is the first typed payload descriptor for
    caller-owned float32 interleaved audio blocks. The reservoir stores only the
    descriptor pointer as a payload handle; audio memory remains owned by
    native/Faust producers.
  - `LocalcastRenderPacketDescriptor` gives render packets the same typed
    descriptor boundary: caller-owned point buffer, point count/stride, target
    size, source window, present time, audio-alignment time, and optional
    metadata handle.
  - `LocalcastRenderPoint` is the first fixed point-buffer element ABI for
    Aquarium decoding: stable-key hash, source timestamp, position, radius,
    color/alpha, and confidence.
- Repo-local persistence machinery.
- First architecture map.
- Example config for one video source plus two audio sources.
- Sender device discovery script.
- Sender launch script with dry-run mode and per-source FFmpeg commands.
- OBS receiver setup notes.
- Neighbor sender deployment under `C:\Meta\Mimir`.
- Madman's desktop start/stop launchers for the sender.
- Receiver OBS scene has `Neighbor PC - Video` and `Neighbor PC - Focusrite` Media Sources.
- Direct co-streamer loopback capture is now attempted with the repo WASAPI shim instead of Voicemeeter.
- Receiver OBS scene has `Neighbor PC - System Audio` on SRT port `5102`.
- Sender script uses `-nostdin` and repo-root logs so desktop launchers stay alive and diagnostics land in `C:\Meta\Mimir\logs`.
- Sender video config is `1920x1080` for the interactive desktop. The `1024x768` SSH session size is not the streaming target.
- Desktop `.cmd` launchers delegate to PowerShell wrappers so paths with spaces and apostrophes are handled in one place.
- Audio-field sidecar profile and tool:
  - `config/audio-field.example.json` declares the actual distributed six-mic rig: two Kiyo mics, two PS Eye mics, local Focusrite shielded cardioid, neighbor Focusrite shotgun on the co-streamer, two local speakers, placeholder geometry, clock domains, capture policy, calibration sweep settings, and FOA AmbiX ACN/SN3D output.
  - `scripts/audio_field.py` lists devices, validates shared or distributed profiles, checks local distributed sources, generates calibration sweeps, summarizes clock-domain sync requirements, preserves shared-input capture helpers, and encodes already aligned offline WAVs to FOA.
  - `audio_field/` contains testable core modules for source buffering, bounded latency convergence, injectable capture/alignment/encoder/sink ports, and pipeline orchestration.
  - `docs/audio-field.md` maps the audio pipeline and its invariants.
- Sensor-fusion render bridge:
  - `localcast.sensor_fusion.calibration_space` solves fixed-board ChArUco observations into camera poses in one common world frame.
  - `localcast.sensor_fusion.surface_features` matches cross-view features and triangulates calibrated surface tracks.
  - `localcast.sensor_fusion.render_bridge` emits render-frame packets with visual/audio timing metadata and Spout sender identity.
  - `localcast.diagnostics.visual_cache` stores diagnostic visual state as typed
    CultCache MessagePack documents. It is not the production live boundary.
  - `localcast.diagnostics.render_math` owns pure diagnostic render budgeting,
    camera projection, brush lowering, and CPU rasterization tests without an
    OpenGL or OpenCV requirement.
  - The Python/OpenGL Spout publisher and launcher have been deleted.
    Production Spout2 publication belongs to Aquarium.
  - `localcast.diagnostics.visual_producer` contains the old Python visual
    producer code. It is diagnostic/migration code, not production runtime, and
    no longer has a script launcher.
  - The live visual producer writes a multi-LOD scene cache with source kind and priority. Real Leap frames are promoted as the highest-priority visual timing/spatial evidence; Leap fallback frames remain lower-priority diagnostics.
  - Live clap calibration is wired into the visual producer. It keeps a rolling frame window from the Kiyo pair plus Leap, reads the live spatial audio frame for transient candidates, publishes `clap-events.msgpack`, injects clap calibration markers into the render frame, and writes clap evidence into the LOD cache. Kiyo stereo owns the current rough 3D solve; Leap owns the best visual timing witness until its geometric model is calibrated.
  - Clap peaks now update a per-camera clock sync model. The point-cloud builder can sample camera history buffers at `oracle_time - camera_offset` instead of using whichever frame arrived last.
  - The live visual producer now uses an explicit five-second spatiotemporal reservoir. RGB/Leap frame history expires from the newest shared sample, the multi-LOD cache only accepts evidence inside that same window, and `RenderFramePacket.source_time_min_ns/source_time_max_ns` declares the reservoir slice being rendered.
  - Multi-LOD cells now carry first-pass material estimates: weighted albedo plus roughness/metallic hints derived from source evidence. Aquarium should treat this as relightable material evidence to refine, not as the final BRDF solve.
  - The Spout/audio overlay boundary clamps stale visual packets to the current audio reservoir edge. If camera geometry ages out, it is dropped instead of being broadcast out of sync.
  - Live fallback-only RGB/Leap mode exists for deadline operation when OpenCV/MSMF/DirectShow reads block longer than the five-second reservoir. It keeps the reservoir and OBS output alive while real camera capture is moved to nonblocking ingress.
  - OpenCV camera reads now run behind `LatestFramePump` workers so blocking drivers cannot freeze the fusion hot loop. The live deadline command uses lower per-frame sample density (`--rgb-room-step 16 --leap-step 16 --points 64`) so the reservoir/TAA path stays fresh enough for Spout to render visual points inside the five-second budget.
  - `localcast.sensor_fusion.chirp_pose` converts live phase-field delay meaning for Kiyo/PS Eye microphone sources into camera-body range constraints and per-camera pose-correction estimates. It can load camera-mic and speaker geometry from the audio-field profile, with the example profile used as the live fallback until measured local geometry exists. The diagnostic visual producer can inject those constraints as `camera-chirp:*` and `camera-pose-correction:*` render points plus `chirp-camera-pose` LOD evidence.
  - Diagnostic render math still has a named `kiyo-mid-deru` virtual camera
    preset and point budget for CPU tests. Aquarium should own the production
    version of that policy.
  - `docs/obs-spout-streaming.md` documents OBS setup and the Aquarium replacement boundary.
  - `docs/typed-visual-state.md` documents the diagnostic CultCache file shape
    and the CultNet visual schema target.
- OBS synchronized program surface:
  - `scripts/obs_smoke_test.py` creates the v1 OBS smoke-test endpoint plan,
    event template, and witness ledger.
  - `localcast.obs_smoke` owns the pure endpoint planning and ledger summary
    logic: per-stage timestamps, matched end-to-end latency, endpoint drift, and
    confidence.
  - `docs/obs-v1-smoke-test.md` is the runbook for collecting
    `sender_capture`, `srt_receive`, and `obs_present` evidence before receiver
    expansion.
  - `scripts/setup_obs_synced_program.py` derives OBS-controllable stems from an aligned program audio timeline: host voice, co-streamer voice, ambient, transients, co-streamer loopback, and local loopback.
  - `scripts/capture_co_streamer_surfaces.py` captures neighbor Focusrite and neighbor loopback with local loopback ground truth, estimates the late remote-family offset, and writes aligned co-streamer surfaces for the stem packer.
  - `scripts/wasapi-loopback-capture.ps1` is the direct primary-playback loopback path. It must run in the neighbor's interactive console session; SSH-only capture sees the device but receives no render packets.
  - The tool creates/updates local OBS Media Sources for those stems and mutes/disables raw unsynchronized inputs.
  - Strict mode disables every scene item except the synchronized Mimir program video and stem controls.
- Live phase-field meaning:
  - `audio_field.phase_meaning` extracts actionable delay, coherence, confidence, suppression, correction-energy, and active-probe need from internal phase/chirplet evidence.
  - `localcast.audio.phase_field` is a typed CultCache document at `calibration/runs/audio-phase-field.msgpack`.
  - `localcast.diagnostics.audio_phase_field` can replay an aligned mic field plus known program/loopback reference into the phase-field document for diagnostics. It is not a live bridge authority.
  - `localcast.diagnostics.audio_phase_field` can write `audio-phase-field-status.json` during replay so estimator behavior remains inspectable while the native ingest path is built.
  - `audio_field.active_probe` wires low phase-field confidence into `ActiveProbeOptimizer`, emits bounded chirplet WAVs plus `active-probes.jsonl`, and can play probes through the default output device.
  - `localcast.diagnostics.audio_live_field` preserves the old local WASAPI/PortAudio capture experiment as diagnostic code only. Production local mic and loopback ingest belongs in native capture workers that append typed handles through `LocalcastProducer`.
  - Active probe maintenance only targets source ids backed by live local capture devices. Missing distributed channels remain stable placeholders in the mic/phase field, but they do not consume chirp budget.
  - Active probe artifacts are bounded: emitted chirps rotate through a fixed slot set and the manifest rotates at a byte cap. The calibration loop is allowed to be noisy; it is not allowed to become an unbounded filesystem leak.
- Faust voice-separation boundary:
  - `localcast.audio.mic_field` publishes aligned six-mic float32 blocks at `calibration/runs/audio-mic-field.msgpack`.
  - `faust/localcast_voice_separation.dsp` is the first Aquarium-hosted graph surface for host voice, co-streamer voice, ambient, transient, and loopback stems.
  - `localcast.diagnostics.faust_mic_field` can replay aligned WAV blocks into the old mic-field document for smoke tests. It is not a live publisher.

## Temporary

- Audio and video are separate SRT endpoints. This preserves OBS mixing authority but may need latency tuning.
- The OBS v1 smoke ledger currently owns the receiver expansion gate. Until a
  real run passes, endpoint coherence is unknown rather than assumed.
- Audio defaults to AAC inside MPEG-TS for compatibility; test Opus later only if there is a concrete reason.
- Desktop capture uses `gdigrab` first because it is broadly available. `ddagrab` is a candidate once the installed FFmpeg build is confirmed.
- The scripts assume Windows sender and OBS receiver on the same LAN.
- The Python visual producer is a diagnostic/migration fossil only. The OpenGL
  Spout sink has been deleted.
- Python audio publishers are diagnostic fossils only. The AmbiX, phase-field,
  live-local-capture, and Faust mic-field publisher modules live under
  `localcast.diagnostics.*`; the live PowerShell launchers for phase-field and
  Faust mic-field publication have been deleted.

## Next

1. Delete remaining diagnostic command dependence on `localcast.diagnostics.visual_producer`,
   diagnostic JSON render-frame adapters, and diagnostic JSON LOD adapters.
   Reservoir-window clipping, Leap packed transforms, RGB reference splats, and
   clap calibration have been split out of the producer monolith.
2. Bind Aquarium/Faust to the rolling-buffer `LocalcastRuntime`.
   Aquarium now has a safe-code native reservoir binding layer, injected frame
   source seam, and native `ILocalCastVisualFrameSource` that reads render
   packet handles through `ILocalCastNativeRuntime` with an injected payload
   decoder. It also has the matching safe-code audio block and render packet
   descriptor bindings, plus a render descriptor decoder that reads timing and
   target metadata from native payload handles and decodes the first native
   point-buffer ABI by default. Aquarium also exposes the native source-id hash
   wrapper and source-id producer constructor. The next Aquarium cut is wiring
   native runtime creation into the app path once a producer exists to feed it.
3. Move camera/mic/loopback ingest into native capture workers that use
   `LocalcastProducer` to append typed sample handles.
4. Move feature extraction, flow, cross-view matching, LOD reconciliation,
   material fitting, brush/splat rendering, and Spout2 publication into
   Aquarium GPU compute.
5. Move mic alignment, room suppression, voice separation, Ambisonic/HOA
   spatialization, and stem generation into Faust/native DSP.
6. Keep FFmpeg/SRT scripts as simple LAN ingest/capture utilities, not the
   synchronized program authority.
7. Run the v1 OBS smoke test against the actual sender/receiver setup and keep
   the ledger as evidence before any receiver/plugin/native OBS expansion.
