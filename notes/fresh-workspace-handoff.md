# Fresh Workspace Handoff

This is the short re-entry packet for `E:\Projects\Mimir`.

Do not trust this file for exact branch, HEAD, or dirty state. Ask git.

## Rehydrate

```powershell
git status --short --branch
git log --oneline -5
Get-Content .\state\map.yaml
Get-Content .\notes\current-system-map.md
Get-Content .\docs\implementation-plan.md
Get-Content .\state\evidence.jsonl -Tail 8
```

## Current Shape

- Public brand/Face is Mimir.
- Mimir's Face doctrine lives in [[docs/mimir-face|Mimir Face]].
- VoidBot persona/state live under `.voidbot/voice/` and `.voidbot/state/`.
  Commit `.voidbot` dirt in a separate state commit whenever it appears.
- V1 still has FFmpeg/SRT/OBS bridge utilities for LAN ingest.
- The live stream app is C# plus Fensalir: `Mimir.slnx` contains
  `src/Mimir.App` and `src/Mimir.Runtime`.
- `src/Mimir.App` hosts Fensalir as the windowing/rendering/D3D12 bridge.
- `src/Mimir.Runtime` owns `MimirSynchronizationHub`, configurable five-second
  rolling buffers, stream descriptors, source adapters, direct native ingest,
  audio chirplet delay estimation, and the video capture driver seam.
- [[docs/code-algorithm-map|Code Algorithm Map]] and
  [[docs/perfect-machine-domain-index|Perfect Machine Domain Index]] are
  the fastest re-entry maps for source ownership and problem-domain cuts.
- `research/perfect-machine-study-2026-05-23/` contains the architecture
  rumination, optimization ledger, references, boundary maps, benchmark plan,
  calibration-session spec, distributed receiver spec, failure ledger, data
  dictionary, low-level implementation notes, and sample code sketches for the
  next decoder/DSP/native-ring/Fensalir implementation passes. Start with
  [[research/perfect-machine-study-2026-05-23/reading-guide|Reading Guide]].
- Local six-camera ingest should use direct driver adapters. Process-backed
  sources are bridge/network edges only.
- Leap stereo IR is the first timing-camera candidate for direct ingest.
- `native/reservoir` owns the lower shared-edge typed-handle invariant for
  Fensalir/Faust binding work.
- Fensalir owns production GPU fusion, UI, and Spout2 publication.
- `MimirSceneEditorState` is currently a 2D OBS-shaped program mixer owner, not
  a camera/3D scene editor. Synced video buffers become program layers with
  normalized placement; the Eve/CultUI surface is Video Sources on the left, a
  chrome-free program canvas in the center, Audio Mixer docked underneath that
  canvas, and a Unity-style Inspector on the right for the selected layer's
  properties. The center preview requests Fensalir's current output-buffer blit
  as its background and overlays Mimir's normalized layer placements on top.
  Camera/SDF/model editor affordances have no live UI authority, and editor
  camera/SDF/spline output no longer contributes to the runtime frame. Smoke
  with `--scene-editor-smoke`.
- The Audio Mixer dock is a horizontal CultUI channel-strip bank, not a
  selected-stem inspector form. Six fixed slots read the live
  `presentationControls.AudioFeeds` order so Scarlett ASIO mono inputs,
  stereo loopback, and Raven loopback can appear as parallel strips with M/S,
  gain, and compact sync/chirp-response readouts derived from
  `MimirSynchronizationHub.AudioSynchronizationStates`. Do not move sample
  alignment, chirp response mapping, or ambient/noise neutralization authority
  into the UI; the surface only controls and observes runtime audio state.
- Program surface placement is seeded from CultCache config
  `config/mimir-program-surface.cc` unless `--cache`,
  `AQUARIUM_CULTCACHE_PATH`, or `MIMIR_PROGRAM_SURFACE_CONFIG` points
  elsewhere. The default `mimir.program.default` document puts
  `raven-display` full-frame at layer 0 and `kiyo-pro-rgb` as a top-left
  picture-in-picture at layer 1. `MimirSceneEditorState` applies matching
  source placements once when those feeds appear, then inspector/drag edits own
  subsequent placement.
- The left Video Sources rail is a direct source selector, not a previous/next
  navigator. It renders visible rows for live video feeds using CultUI
  row-level visibility, with a Select action and placement summary per source.
  Keep configured/offline source inventory as a future source-state owner;
  the current rail lists live synced video feeds.
- Live camera image buffers should use Fensalir-owned texture leases when they
  are rendering inputs: Mimir asks the engine broker for a keyed D3D12
  Texture2D/fence lease, writes decoded frames into that texture, commits the
  producer fence value, and lowers the same resource key through FieldEvidence.
  `MimirVideoCaptureDriverSource` forwards that lease client to direct drivers
  that implement `IMimirFensalirTextureLeaseReceiver`.
  Camera backends must use the closest-to-device path available and report
  unavoidable copies; process or managed convenience layers are diagnostics.
  Raw single-plane CPU-origin frames can use the explicit broker upload lane,
  which increments `UnavoidableCopyCount` and clears live managed payload bytes
  after upload. NV12 CPU upload is rejected until Fensalir owns planar copy;
  device/GPU NV12 producers should write the leased texture directly.
  The first direct KS/UVC driver is `MimirKsVideoCaptureDriver` backed by
  `native/camera_capture/mimir_camera_capture.dll`; the example config
  `config/mimir-runtime.ks-camera.example.json` targets LeapUVC 640x240 YUY2.
  The same native folder also has `mimir_ps3eye_capture.dll` and
  `MimirPs3EyeVideoCaptureDriver` for PS3 Eye Bayer8 frames through WinUSB/libusb.
  Fresh driver smokes pulled and uploaded real frames for LeapUVC 640x240 YUY2,
  both PS3 Eyes at 320x240 Bayer8, Kiyo Pro 1920x1080 YUY2, and regular Kiyo
  640x480 YUY2. Regular Kiyo 1280x720 YUY2 did not open.
  Compressed Kiyo Pro MJPG/H264 now have a first GPU-resident path:
  `MimirMediaFoundationGpuVideoCaptureDriver` uses
  `native/camera_capture/mimir_mf_gpu_capture.dll` to decode through Media
  Foundation with a D3D11 device manager, copy into a shared BGRA D3D11 texture,
  and publish `shared-d3d11-texture` with zero live CPU payload bytes.
  1920x1080 MJPG->RGB32 and H264->RGB32 smokes passed. Direct shared NV12
  texture creation failed, so NV12 remains future plane-aware interop or
  GPU bridge-copy work.
  Foreign shared texture handles are import edges, not the primary hot path.
- The current Mimir/Fensalir rendering teardown map is
  [[docs/fensalir-rendering-rebuild-migration|Fensalir Rendering Rebuild
  Migration]]. Mimir's production visual output path must publish typed
  physical observations, calibration constraints, and surface intent; Fensalir
  turns those into field claims, selected lowerings, reusable evidence,
  temporal guide lanes, and presentation.
- Fast depth provenance now lives at
  [[research/d3d12-stereo-depth-provenance-2026-05-29|D3D12 Stereo Depth
  Provenance]]. `libSGM` is the current permissive north-star reference for a
  future Fensalir-owned D3D12 stereo SGM compute lane. Do not import CUDA or
  TensorRT as live depth authority.
  The first code contract is in `MimirStereoDepthConfigurations` and
  `MimirFensalirFieldLowering`. Run
  `dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --stereo-depth-contract-smoke`
  to verify caller-declared rectified left/right input textures, or
  `dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --leap-packed-stereo-depth-smoke`
  to verify live `LeapStereoIr` packed R8G8 lowering. The Leap path reuses the
  same declared texture as left/right resource identity and treats R/G as the
  packed observation lanes. Fensalir now has a first packed-Leap D3D12
  SAD/block-match disparity writer, not full libSGM-style SGM or calibrated
  metric depth. Mimir also now declares the first Leap point-cloud root:
  `MimirPointCloudConfigurations.LeapDisparityPointCloudRoot` and
  `BuildLeapPackedStereoPointCloudCandidateFrame` emit a GPU-resident
  `FieldMesh` point-list resource derived from that disparity SurfacePage. Run
  `dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --leap-point-cloud-root-smoke`
  to verify the combined `SurfacePage` plus `Mesh` packet plan. This is a
  typed socket on the Mimir side. Fensalir branch `codex/leap-packed-depth` now
  has the first matching render lane: generated point-list Mesh buffers are
  filled from the disparity SurfacePage on D3D12 and rendered through the scene
  candidate targets. The next cut is live Leap/editor verification plus
  calibrated projection constants and global residual/calibration ownership.
- Fensalir also owns the EVE-facing dashboard pixels through
  `Global\MimirFensalirProgramTexture`. `src/Mimir.EveRelay` opens that shared
  texture, encodes H.264 Annex-B with NVENC by default, and serves EveCanvas
  over `/stream`; EveCanvas decodes with `AVSampleBufferDisplayLayer`. The
  launch script uses an SSH reverse tunnel to Eve when Windows firewall blocks
  direct inbound TCP.
- `src/Mimir.Broadcast` is the first self-hosted public sender. Starfire pushes
  one final H.264/AAC NVENC RTMP stream to
  `rtmp://127.0.0.1:11935/live/mimir` through an SSH local forward to
  Yggdrasil. Yggdrasil's nginx RTMP/HLS origin is deployed and has produced
  HLS segments from a bounded synthetic push. Public HLS is served through the
  existing StreamPixels TLS host, not a new Mimir media hostname. The public
  static viewer belongs to the root GameCult site at
  `https://gamecult.org/livestream` and reads
  `https://streampixels.gamecult.org/mimir/live/hls/mimir.m3u8`.
- Raven/Eve ingest now has a network-pixel receiver config:
  `config/mimir-runtime.raven-eve.example.json` starts an FFmpeg-backed
  raw-video listener for `raven-display` on SRT port `5200` and Eve sensor
  frame-event receivers for `eve-camera` on WebSocket port `8793` and `eve-mic`
  on port `8794`. `scripts/start-raven-screen-capture-sender.ps1` launches
  Raven desktop capture with `gdigrab` plus NVENC. EveCanvas owns Eve camera/mic
  capture directly through AVFoundation/AVAudioEngine; no external iOS camera
  app owns that path.
- Faust/native DSP owns hot audio alignment, separation, spatialization, and
  synchronized stems.
- The current listenable dual-mic receipt is the Faust-chain bridge, not the
  older C# composite. `scripts/record-kiyo-dual-mic-composite.ps1` records Kiyo
  plus Scarlett ASIO, then `--asio-dual-mic-faust-cleaner-wav` compiles/runs
  `faust/mimir_alignment_actuator.dsp` before
  `faust/mimir_dual_mic_dialogue_cleaner.dsp` and muxes the dialogue stem into
  `artifacts/runtime/kiyo-dual-mic-composite/kiyo-dual-mic-composite-latest.mp4`.
  Fresh run `20260601-051044` measured cardioid delay 359.398 samples
  (7487.452 us) at 48 kHz and applied holdback 359.398/0.000 to
  shotgun/cardioid before cleanup.
- `MimirProgramRecorder` exposes local broadcast-proof recording as a Mimir app
  feature. The Video Sources rail has `Record` / `Stop`, status text, and latest
  output under
  `artifacts/runtime/stream-proof/mimir-program-record-latest.mp4`. It currently
  delegates to `scripts/record-stream-proof-output.ps1`, so the app records the
  same Raven + Kiyo PIP + Starfire Scarlett NVENC/AAC composite intended for
  Yggdrasil/public judgment. `MIMIR_RECORD_START_RAVEN=1` asks it to launch the
  Raven sender too. Native program-output recording should replace that
  actuator behind the same owner.
- OBS receives final program surfaces; it does not own synchronization.

## Current Pressure

- Verify the live Leap point cloud in the Mimir editor, then add calibrated
  projection constants and the global residual/calibration owner.
- Cut the Phase 1 Mimir-to-Fensalir bridge DTOs for rolling windows,
  observations, calibration constraints, and surface intent before adding
  another backend-specific visual path.
- Add remaining camera drivers through `IMimirVideoCaptureDriver`.
- Add native audio capture workers for mic, loopback, and network audio feeds.
- Turn the current loopback-referenced chirplet measurement into the real
  synchronization actuator: smooth delay/SRO, then drive fractional delay and
  variable-rate resampling.
- Bind Fensalir UI to stream health, buffer depth, timestamps, settings, and
  output management.
- Keep PowerShell/FFmpeg/SRT as bridge utilities until native program output
  replaces them.
- Kiyo Pro has two UVC extension units. Moving it to a motherboard USB3 port
  changed the descriptor path to `root_hub30` / `bcdUSB=0x0320` and exposed
  720p/1080p YUY2/MJPG/H264/NV12 at 60 fps, but Windows still reports
  `UsbHighSpeed` rather than `UsbSuperSpeed`; measured cadence remains about
  25 fps across all tested formats. Verify cable/port SuperSpeed before poking
  writable vendor selectors.
- First simultaneous local pull only saw LeapUVC, Kiyo Pro, and one PS3 Eye.
  The KS multi-stream harness and raw PS3 Eye probe can pull all three at once,
  but Leap drops hard under shared USB load: 40.12 fps when the PS3 Eye runs
  640x480@60, and 81.10 fps when the PS3 Eye runs 320x240@187. The regular
  Kiyo and second PS3 Eye were absent, so six-camera viability is not proven.
- Current active sync smoke uses Scarlett speaker loopback as timing
  authority. `MimirBioacousticTimeline` now owns the active runtime watermark:
  low-gain birdsong-like words with log-spaced roots, left/right speaker
  variants, four formant-rich syllables, rhythm variation, and direct word
  identity. Any correctly decoded word identifies the event index inside the
  current operating horizon. Runtime active sync reports
  `evidence=bioacoustic`; [[docs/bioacoustic-timeline-watermark|Bioacoustic
  Timeline Watermark]] is the current map. Sync reports now include
  fractional delay and per-band matched energy. `MimirAudioSynchronizationState`
  now tracks smoothed delay and delay-slope/SRO ppm. The older
  `MimirChirpletSymbolCodebook` / `MimirChirpletStreamDecoder` path remains a
  diagnostic reference for constrained chirplet-transform work; it is not the
  active runtime witness. `MimirRuntime` runs sync analysis as a bounded rotating
  service and caches reports/states for UI and telemetry; readouts must stay
  passive. Audio sync mode is runtime-owned: `chirp-only` emits the active
  bioacoustic witness continuously, `passive` disables emission and uses
  program-audio phase correlation, and `hybrid` emits active pilot chunks only
  when passive confidence is weak. The active bioacoustic path has its own
  short-window analyzer floor instead of inheriting the passive two-second gate.
  The active receiver is now a song-contour anchor machine, not a de Bruijn
  sequence receiver: one heard call should carry enough syllable timing,
  formant, payload, rhythm, speaker tint, and log-mel contour evidence to
  identify canonical time and pin multiple time/frequency anchors at once. It
  uses energy/onset proposals, dense fallback probes, bounded motif matching,
  direct word anchoring, clock fit, and constrained local waveform
  correlation for final fractional delay. It also supports standalone source
  offset recovery from schedule/codebook state, which is the Raven/phone shape.
  Each classified motif carries per-band response evidence. The older
  `MimirChirpBinCalibrationModel` remains the controlled chirp-bin
  response/confusion/delay reference surface; use it to inform bioacoustic motif
  weighting, not as the runtime sound.
  `Mimir.BufferSmoke --bioacoustic-self-test` proves direct word anchors.
  `--standalone-bioacoustic-self-test --sample-rate 48000 --delay-samples
  1269.5` recovers delayed canonical time below printed microsecond precision
  without loopback. `--chirp-only-sync-self-test --sample-rate 48000` recovers a
  317.375-sample synthetic delay with printed 0.000 us error using
  `evidence=bioacoustic`. Reports and sync states expose `delayUs`.
  Actual
  Mimir.App testing proves Fensalir audio can wake Scarlett
  loopback, keep mic buffers live, and produce confident online passive sync
  states. The latest live hybrid smoke did not prove acoustic chirp-bin decode
  because Scarlett loopback stalled after one block in that run; treat that as
  loopback freshness evidence, not duration evidence. PS3 Eye audio is
  enumeration/runtime-fragile: one run saw both mic buffers empty while both
  cameras were live, then a replug made both PS3 Eye mic buffers emit
  480-frame WASAPI blocks again.
- Scarlett ASIO loopback is usable on Starfire. The Scarlett Solo 4th Gen
  (`USB\VID_1235&PID_8218`) is now local; `native/probes/asio_audio_cadence`
  opens `Focusrite USB ASIO`, sees 4 inputs / 2 outputs including
  `Loopback 1/2`, 192-frame preferred buffers, 44.1-192 kHz support, and
  captures nonzero 4-channel `Int32LSB` input callbacks at 192 kHz. It can play
  raw mono Float32 chirplet timelines through ASIO outputs and capture all ASIO
  inputs as raw interleaved Float32 for runtime analysis. The old arbitrary
  chirplet artifact proved correctness but took roughly 100 seconds per
  comparison and is now diagnostic only. A real Scarlett chirp-bin artifact run
  at 192 kHz decoded `Loopback 1 -> Loopback 2` at `0.000 us` delay with 12
  matched anchors and 0.999 confidence in the normal active analyzer. Physical
  inputs still do not earn accepted timing against loopback, but
  `--calibrate-chirp-bin-asio-f32` now persists a real response/confusion/delay
  model and `--analyze-asio-f32 --calibration ...` consumes it. In the stored
  artifact, physical input 1 produced two reliable symbols and a different
  strongest-bin profile while failing pairwise timing. `native/asio_capture`
  plus `MimirAsioStreamSource` now load Focusrite ASIO in process and feed
  sample-bearing 192 kHz Float32 blocks directly into runtime buffers. The
  minimal `config/mimir-runtime.asio.example.json` proof ingests more than
  12,000 blocks across `asio-ch0` through `asio-ch3` in two seconds and retains
  2,048 blocks per channel. Acoustic robustness is still open, but failed
  timing now leaves response evidence.
- Raven also has a 192 kHz loopback-capable Scarlett for co-streamer/game timing
  evidence. Do not move the heavy soundfield or sensor-fusion workload there.

## Immediate Re-entry Instruction

The corrected two-hour study pass ran from
`2026-05-23T23:54:42.9041588+01:00` to at least
`2026-05-24T01:54:56.9747630+01:00`. Use its map first, then implement the
active song-contour receiver: promote packet-song physical calibration from the
BufferSmoke receipt into runtime state, extract intra-call time/frequency
anchors from log-mel contour parts, apply learned path weighting, drive the
fractional-delay/SRO actuator, then continue native camera payload handles and
Fensalir contract lowering. Keep loopback as timing authority and keep Scarlett
capture on the in-process ASIO source. Do not call synchronization analysis
from UI/telemetry readouts. Do not restore deleted script infrastructure
because a stale doc once missed it.
