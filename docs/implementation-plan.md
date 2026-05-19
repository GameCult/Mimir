# Implementation Plan

## Implemented

- Perfect Machine contract:
  - `docs/perfect-machine.md` defines the target native machine: one five-second spatiotemporal reservoir, Aquarium-owned dense visual fusion/material/brush rendering, Faust-owned hot audio DSP, OBS-owned broadcast controls, and LocalCastBridge-owned calibration/config/status.
  - `config/perfect-machine.example.json` declares the contract shape for six cameras, six microphones, reservoir rings, native authorities, outputs, and the demotion of bridge scripts to tooling.
  - `native/reservoir` is the first native Rust crate. It implements shared-edge five-second reservoir rings and proves that all rings expire against the newest sample, not private per-source clocks.
  - `native/reservoir/include/localcast_reservoir.h` exposes the Aquarium/Faust C ABI: opaque reservoir create/destroy, sample-handle push, edge/window queries, ring counts, and latest sample lookup by sensor hash. Payload memory remains owned by Aquarium/GPU/Faust; the reservoir owns timing and retention.
  - `LocalcastRuntime` now wraps the reservoir with typed native producer calls for camera frames/features, scene rays, surface/material claims, audio blocks, phase claims, events, and render packets. It also exposes native status with shared edge/window and ring counts, replacing the Python file-cache path as the intended live spine.
- Repo-local persistence machinery.
- First architecture map.
- Example config for one video source plus two audio sources.
- Sender device discovery script.
- Sender launch script with dry-run mode and per-source FFmpeg commands.
- OBS receiver setup notes.
- Neighbor sender deployment under `C:\Meta\LocalCastBridge`.
- Madman's desktop start/stop launchers for the sender.
- Receiver OBS scene has `Neighbor PC - Video` and `Neighbor PC - Focusrite` Media Sources.
- Direct co-streamer loopback capture is now attempted with the repo WASAPI shim instead of Voicemeeter.
- Receiver OBS scene has `Neighbor PC - System Audio` on SRT port `5102`.
- Sender script uses `-nostdin` and repo-root logs so desktop launchers stay alive and diagnostics land in `C:\Meta\LocalCastBridge\logs`.
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
  - `localcast.sensor_fusion.cultcache_docs` stores live visual state as typed CultCache MessagePack documents.
  - `localcast.sensor_fusion.spout_output` renders render-frame packets into a GPU texture and publishes it as a named Spout sender for OBS.
  - `scripts/live_sensor_fusion.py` writes fused render frames into `calibration/runs/visual-state.msgpack`.
  - The live visual producer writes a multi-LOD scene cache with source kind and priority. Real Leap frames are promoted as the highest-priority visual timing/spatial evidence; Leap fallback frames remain lower-priority diagnostics.
  - Live clap calibration is wired into the visual producer. It keeps a rolling frame window from the Kiyo pair plus Leap, reads the live spatial audio frame for transient candidates, publishes `clap-events.msgpack`, injects clap calibration markers into the render frame, and writes clap evidence into the LOD cache. Kiyo stereo owns the current rough 3D solve; Leap owns the best visual timing witness until its geometric model is calibrated.
  - Clap peaks now update a per-camera clock sync model. The point-cloud builder can sample camera history buffers at `oracle_time - camera_offset` instead of using whichever frame arrived last.
  - The live visual producer now uses an explicit five-second spatiotemporal reservoir. RGB/Leap frame history expires from the newest shared sample, the multi-LOD cache only accepts evidence inside that same window, and `RenderFramePacket.source_time_min_ns/source_time_max_ns` declares the reservoir slice being rendered.
  - Multi-LOD cells now carry first-pass material estimates: weighted albedo plus roughness/metallic hints derived from source evidence. Aquarium should treat this as relightable material evidence to refine, not as the final BRDF solve.
  - The Spout/audio overlay boundary clamps stale visual packets to the current audio reservoir edge. If camera geometry ages out, it is dropped instead of being broadcast out of sync.
  - Live fallback-only RGB/Leap mode exists for deadline operation when OpenCV/MSMF/DirectShow reads block longer than the five-second reservoir. It keeps the reservoir and OBS output alive while real camera capture is moved to nonblocking ingress.
  - OpenCV camera reads now run behind `LatestFramePump` workers so blocking drivers cannot freeze the fusion hot loop. The live deadline command uses lower per-frame sample density (`--rgb-room-step 16 --leap-step 16 --points 64`) so the reservoir/TAA path stays fresh enough for Spout to render visual points inside the five-second budget.
  - `localcast.sensor_fusion.chirp_pose` converts live phase-field delay meaning for Kiyo/PS Eye microphone sources into camera-body range constraints and per-camera pose-correction estimates. It can load camera-mic and speaker geometry from the audio-field profile, with the example profile used as the live fallback until measured local geometry exists. `scripts/live_sensor_fusion.py` injects those constraints as `camera-chirp:*` and `camera-pose-correction:*` render points plus `chirp-camera-pose` LOD evidence.
  - `scripts/stream_spout.py` runs the deadline Spout sender loop from the typed cache with typed and JSON heartbeat status.
  - The deadline Spout renderer has a named `kiyo-mid-deru` virtual camera preset: eye at the midpoint between the two Kiyo-class cameras and target on the co-streamer body volume. It also applies a renderer-owned point budget with pinned calibration/cross-modal constraints, stable high-confidence anchors, and frame-varying remainder samples so downstream TAA/supersampling can accumulate detail without forcing every source claim into every OBS frame. Its JSON heartbeat reports the prefix counts that survived the render budget.
  - `docs/obs-spout-streaming.md` documents OBS setup and the Aquarium replacement boundary.
  - `docs/typed-visual-state.md` documents the CultCache/CultNet visual boundary.
- OBS synchronized program surface:
  - `scripts/setup_obs_synced_program.py` derives OBS-controllable stems from an aligned program audio timeline: host voice, co-streamer voice, ambient, transients, co-streamer loopback, and local loopback.
  - `scripts/capture_co_streamer_surfaces.py` captures neighbor Focusrite and neighbor loopback with local loopback ground truth, estimates the late remote-family offset, and writes aligned co-streamer surfaces for the stem packer.
  - `scripts/wasapi-loopback-capture.ps1` is the direct primary-playback loopback path. It must run in the neighbor's interactive console session; SSH-only capture sees the device but receives no render packets.
  - The tool creates/updates local OBS Media Sources for those stems and mutes/disables raw unsynchronized inputs.
  - Strict mode disables every scene item except the synchronized LocalCastBridge program video and stem controls.
- Live phase-field meaning:
  - `audio_field.phase_meaning` extracts actionable delay, coherence, confidence, suppression, correction-energy, and active-probe need from internal phase/chirplet evidence.
  - `localcast.audio.phase_field` is a typed CultCache document at `calibration/runs/audio-phase-field.msgpack`.
  - `scripts/stream_phase_field.py` publishes the live meaning document from an aligned mic field plus known program/loopback reference without exposing raw phase bands as the renderer API.
  - `stream_phase_field.py` also writes `audio-phase-field-status.json`, which declares whether the phase estimator is running against replayed WAVs or closed-loop live capture. Current deadline mode is explicit `wav-replay` with open-loop probe playback.
  - `audio_field.active_probe` wires low phase-field confidence into `ActiveProbeOptimizer`, emits bounded chirplet WAVs plus `active-probes.jsonl`, and can play probes through the default output device.
  - `scripts/stream_live_audio_field.py` is the live local capture owner for the deadline rig. It publishes `localcast.audio.mic_field` and `localcast.audio.phase_field` from visible local WASAPI/PortAudio mics plus Scarlett loopback, reports missing rig channels as explicit placeholders, plays confidence probes through the selected Scarlett output, and resamples probe playback when the output device runs at 44.1 kHz while the field timeline remains 48 kHz.
  - Active probe maintenance only targets source ids backed by live local capture devices. Missing distributed channels remain stable placeholders in the mic/phase field, but they do not consume chirp budget.
  - Active probe artifacts are bounded: emitted chirps rotate through a fixed slot set and the manifest rotates at a byte cap. The calibration loop is allowed to be noisy; it is not allowed to become an unbounded filesystem leak.
  - `scripts/start-live-audio-phase-field.ps1` starts the live dense harmonic confidence loop with near-ultrasonic probes; `scripts/stop-live-audio-phase-field.ps1` stops the PID recorded in `logs/audio-phase-field.pid`.
- Faust voice-separation boundary:
  - `localcast.audio.mic_field` publishes aligned six-mic float32 blocks at `calibration/runs/audio-mic-field.msgpack`.
  - `faust/localcast_voice_separation.dsp` is the first Aquarium-hosted graph surface for host voice, co-streamer voice, ambient, transient, and loopback stems.
  - `scripts/start-live-faust-mic-field.ps1` starts the mic-field publisher for Aquarium/Faust.

## Temporary

- Audio and video are separate SRT endpoints. This preserves OBS mixing authority but may need latency tuning.
- Audio defaults to AAC inside MPEG-TS for compatibility; test Opus later only if there is a concrete reason.
- Desktop capture uses `gdigrab` first because it is broadly available. `ddagrab` is a candidate once the installed FFmpeg build is confirmed.
- The scripts assume Windows sender and OBS receiver on the same LAN.
- The Python live producers/renderers are compatibility diagnostics. They are no longer the intended runtime API.

## Next

1. Add the Aquarium-side binding that loads `localcast_reservoir`, creates `LocalcastRuntime`, and maps GPU/audio buffer handles to `LocalcastSampleHandle`.
2. Move camera ingest into native capture workers that call `localcast_runtime_push_camera_frame`.
3. Move mic/loopback ingest into native capture workers that call `localcast_runtime_push_audio_block`.
4. Move feature extraction, flow, cross-view matching, LOD reconciliation, and material fitting into Aquarium GPU compute.
5. Move mic alignment, voice separation, room suppression, Ambisonic/HOA spatialization, and stem generation into Faust/native DSP.
6. Replace the deadline Spout sink with Aquarium-owned Spout publication from the native render target.
7. Feed the neighbor Focusrite shotgun and neighbor loopback into the reservoir as live synchronized audio inputs.
8. Replace placeholder mic/speaker/camera geometry with measured `config/audio-field.json` and `config/sensor-fusion.json` coordinates.
9. Calibrate Leap as a geometric camera so Leap contributes direct 3D rays, not only timing evidence.
10. Keep the Python bridge only for calibration, contract tests, device discovery, status, and offline analysis.
