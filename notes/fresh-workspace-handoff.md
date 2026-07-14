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
- V1 still has FFmpeg/SRT/OBS bridge utilities for LAN compatibility ingest.
- V1 now also has an explicit CultMesh media bridge for Raven program feeds:
  `src/Mimir.CultMeshMedia` sends rolling `mimir.cultmesh_media_frame`
  documents over CultNet reliable UDP through Yggdrasil and lowers them on
  Starfire to local MPEG-TS UDP for OBS. The bridge now uses explicit CultLib
  RUDP helpers/session plumbing; the old
  `CultMesh.StartNodeAsync`/`CultMesh.ConnectClient` path no longer owns this
  media lane.
- CultLib has moved the daemon-swarm direction to CultNet over RUDP across
  runtimes. Mimir's typed daemon truth should default to
  `cultnet.transport.rudp.v0`; TCP/HTTP/WebSocket are client lowerings,
  compatibility probes, or migration debt unless a specific runtime has not
  earned RUDP yet.
- Mimir's old Eve dashboard broker is archived and no longer publishes Idunn
  health, `/eve/deck`, `/eve/dashboard`, or WebSocket commands. The browser
  reference is a client lowering, not service truth. Dashboard state must return
  as typed CultMesh/Eve documents through Odin before deployment resumes.
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
- `docs/mimir-program-composition.md` is the current program-output authority
  map: Muninn observes on Starfire/Nightwing/Raven-class hosts, Mimir consumes
  selected streams and owns composition, Eve lowers controls/previews/stats,
  Yggdrasil publishes to the site, and OBS is compatibility-only.
- `src/Mimir.Runtime/Synchronization` now has typed program contracts for
  `mimir.program_scene.v1`, `mimir.program_output.v1`, and
  `mimir.eve_operator_surface.v1`; `Mimir.BufferSmoke
  --perfect-machine-contract-smoke` writes them into CultCache.
- `Mimir.BufferSmoke --import-obs-program-scene --output
  state/mimir-program-composition.cc` imports the current OBS scene JSON into
  Mimir-owned typed state. The latest imported scene has eight layers, six
  visible, two cropped, one chroma-keyed, and Raven mapped as
  `muninn:raven:monitor:primary`.
- Local six-camera ingest should use direct driver adapters. Process-backed
  sources are bridge/network edges only.
- Leap stereo IR is the first timing-camera candidate for direct ingest.
- `native/reservoir` owns the lower shared-edge typed-handle invariant for
  Fensalir/Faust binding work.
- Fensalir owns production GPU fusion, UI, and Spout2 publication.
- Faust/native DSP owns hot audio alignment, separation, spatialization, and
  synchronized stems.
- OBS compatibility adapters may receive final program surfaces; they do not
  own synchronization, composition, preview/control, or publication.

## Current Pressure

- Nightwing Move tracking is one Muninn provider with two private per-eye
  PSMoveAPI worker subprocesses. Eye 0 uses exposure 0.3; Eye 1 uses 0.1.
  Both workers calibrate four Moves and advance continuously. Mimir has now
  proved all four stable IDs on both eyes in the same frames. The latest
  15-second receipt contains 16 observations and eight pair records but only
  two unique aggregate frame IDs. Next measure parent publication and RUDP send
  counters directly, fix cadence, then collect diverse correspondences for
  camera calibration.

- Implement the first concrete Leap direct capture driver and measure cadence.
- Add remaining camera drivers through `IMimirVideoCaptureDriver`.
- Add native audio capture workers for mic, loopback, and network audio feeds.
- Turn the current loopback-referenced chirplet measurement into the real
  synchronization actuator: smooth delay/SRO, then drive fractional delay and
  variable-rate resampling.
- Bind Fensalir UI to stream health, buffer depth, timestamps, settings, and
  output management.
- Keep PowerShell/FFmpeg/SRT as bridge utilities until native Mimir program
  output, Eve operation, and Yggdrasil site publication replace them.
- Yggdrasil currently runs the CultMesh media relay from
  `/opt/gamecult/mimir-cultmesh-media/Mimir.CultMeshMedia`; senders and
  receivers target `cultmesh://asgard.yggdrasil.mimir/media/raven-primary-av`
  and let the CultMesh resolver choose the concrete RUDP route. Starfire writes
  `raven-primary-av` to `udp://127.0.0.1:5200`. That is the
  `Mimir.CultMeshMedia` body bridge, not the OBS plugin feed owner.
- The actual Raven OBS/SRT feed owner is Odin's Muninn on Raven. Use
  `scripts/start-raven-muninn-obs-feed.ps1` in this repo as the thin wrapper
  over `E:\Projects\Odin\scripts\activate-muninn-raven-av-srt.ps1`, which
  drives Raven's real `GameCult-Muninn-Activate` hidden task and
  `muninn.exe activate` path. `scripts/start-raven-cultmesh-av-sender.ps1`
  remains the separate CultMesh bridge/bootstrap lane.
- Rebuild Eve dashboard only as a CultMesh/Odin surface publisher plus renderer
  lowering. Provider advertisement, retained state, command boundary, transport
  profile, and command documents must be typed CultMesh records; no TCP, HTTP,
  or WebSocket surface should become daemon or transport truth.
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

The 2026-07-14 live two-wand session at
`artifacts/move-calibration/live-two-wand-20260714-004210.*` passed collection
coverage with 1,168 admitted same-frame pairs. The new Mimir-owned sphere
stereo fitter recovers synthetic 505/535 px focal lengths within 0.43 px and a
34 cm baseline within 0.22 mm, with 0.34/0.63 px held-out median/P95 error.
The live artifact remains rejected: after robust radius admission its consensus
fits at 0.51/2.08 px, but only 53.8 percent of held-out pairs agree and Eye 1's
focal estimate leaves the physical range. PSMoveAPI age is currently projected
as marker confidence by Muninn, so fresh wrong-color matches arrive near 0.996
confidence. Repair source-local observation quality/continuity and calibrate
per-Eye blob-radius response before asking the operator for another sweep. Do
not promote the nominal 22.5 mm diagnostic fit.

The active priority is Nightwing Move calibration. One public Muninn observer
owns two private Eye workers and one aggregate evidence stream. Odin commits
`39c839a`, `a340c33`, `8aefd1c`, and `f82ca7a` give daemon health a persistent
serviced RUDP session and move optical aggregation onto its own deadline-clocked
loop. The 2026-07-13 receipt at
`artifacts/move-calibration/muninn-clocked-aggregator.mpack` contains 622 frames,
2,647 observations, and 1,384 cross-camera correspondences in 15 seconds, with
all four Move IDs visible to both Eyes. Next collect spatially diverse samples,
calibrate intrinsics/extrinsics, and publish residual-bearing typed calibration.
Radius is a weighted range cue, not a substitute for camera calibration.

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
