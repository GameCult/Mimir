# Implementation Plan

## Implemented

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
  - `scripts/stream_spout.py` runs the deadline Spout sender loop from the typed cache with typed and JSON heartbeat status.
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
  - `audio_field.active_probe` wires low phase-field confidence into `ActiveProbeOptimizer`, emits bounded chirplet WAVs plus `active-probes.jsonl`, and can play probes through the default output device.
  - `scripts/start-live-audio-phase-field.ps1` starts the live dense harmonic confidence loop with near-ultrasonic probes; `scripts/stop-live-audio-phase-field.ps1` stops the PID recorded in `logs/audio-phase-field.pid`.

## Temporary

- Audio and video are separate SRT endpoints. This preserves OBS mixing authority but may need latency tuning.
- Audio defaults to AAC inside MPEG-TS for compatibility; test Opus later only if there is a concrete reason.
- Desktop capture uses `gdigrab` first because it is broadly available. `ddagrab` is a candidate once the installed FFmpeg build is confirmed.
- The scripts assume Windows sender and OBS receiver on the same LAN.

## Next

1. Open OBS and confirm the three `Neighbor PC` sources load.
2. Start the sender from Madman's interactive desktop, not SSH.
3. Smoke-test the video endpoint in OBS.
4. Smoke-test the Focusrite endpoint in OBS.
5. Smoke-test the system-loopback endpoint in OBS.
6. Tune SRT latency and FFmpeg buffering for the local network.
7. Decide whether a small OBS scene/source generator is worth adding.
8. Reconsider plugin/fork only if standard OBS Media Source cannot preserve the required behavior.
9. Create local `config/audio-field.json`, confirm local Kiyo/PS Eye/Focusrite device matches, and confirm the neighbor Focusrite shotgun capture/transport path.
10. Replace placeholder mic/speaker geometry with measured world coordinates, then build the delay/SRO alignment stage that feeds the bounded field cache and emits aligned six-channel blocks before FOA encoding.
11. Move the render-frame consumer into Aquarium Engine so dense brush/splat rendering replaces the deadline OpenGL point sink behind the same OBS Spout boundary.
12. Capture fixed-board ChArUco observations for every camera, solve `config/sensor-fusion.json`, and verify cross-view feature triangulation with `scripts/triangulate_surface_features.py`.
13. Replace synthetic live-fusion observations with PS3 Eye detector observations and calibrated surface-feature tracks.
14. Keep the neighbor loopback leg running through the scheduled interactive WASAPI capture path and use it as the timing witness once actual neighbor app audio is present.
15. Let the co-streamer surface delay drive the shared presentation buffer horizon for audio stems, AmbiX, and remote video.
16. Wire `stream_phase_field.py` to the real live aligned-field producer instead of replaying WAV artifacts, then feed emitted active chirps back through loopback/mic capture so confidence maintenance closes against fresh observations instead of dry-run probe manifests.
