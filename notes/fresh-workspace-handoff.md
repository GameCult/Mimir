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
- Mimir's Face doctrine lives in `docs/mimir-face.md`.
- VoidBot persona/state live under `.voidbot/voice/` and `.voidbot/state/`.
  Commit `.voidbot` dirt in a separate state commit whenever it appears.
- V1 still has FFmpeg/SRT/OBS bridge utilities for LAN ingest.
- The live stream app is C# plus Fensalir: `Mimir.slnx` contains
  `src/Mimir.App` and `src/Mimir.Runtime`.
- `src/Mimir.App` hosts Fensalir as the windowing/rendering/D3D12 bridge.
- `src/Mimir.Runtime` owns `MimirSynchronizationHub`, configurable five-second
  rolling buffers, stream descriptors, source adapters, direct native ingest,
  audio chirplet delay estimation, and the video capture driver seam.
- Local six-camera ingest should use direct driver adapters. Process-backed
  sources are bridge/network edges only.
- Leap stereo IR is the first timing-camera candidate for direct ingest.
- `native/reservoir` owns the lower shared-edge typed-handle invariant for
  Fensalir/Faust binding work.
- Fensalir owns production GPU fusion, UI, and Spout2 publication.
- Faust/native DSP owns hot audio alignment, separation, spatialization, and
  synchronized stems.
- OBS receives final program surfaces; it does not own synchronization.

## Current Pressure

- Implement the first concrete Leap direct capture driver and measure cadence.
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
  authority. `MimirChirpletTimeline` owns the structured birdsong-like timeline
  fingerprint: an order-3 de Bruijn sequence over 32 time/frequency
  constellation symbols. Start band, glide shape, duration, and following
  inter-chirp gap all carry code, so any three consecutive correctly detected
  symbols identify the event index inside the current operating horizon. The
  timeline owns Fensalir PCM segment rendering, transform-frame decoding,
  triplet anchor decoding, and per-band response hooks. Sync reports now include
  fractional delay and per-band matched energy. `MimirAudioSynchronizationState`
  now tracks smoothed delay and delay-slope/SRO ppm. `MimirChirpletSymbolCodebook`
  owns separable symbol definitions, and `MimirChirpletStreamDecoder` is the
  constrained chirplet-transform receiver: it emits transform frames with
  multiple phase-invariant symbol candidates and refined per-candidate sample
  offsets, selects code-valid triplet anchors through gap/clock coherence, and
  fits a source clock from a bounded PCM window. The canonical synthetic check is
  `dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --chirplet-self-test`;
  it currently detects all 15 emitted chirps and recovers 13 anchors for events
  0-12 with a 47999.999990 Hz clock fit and 0.000014 sample MAE. `MimirRuntime` runs sync analysis as a bounded rotating
  service and caches reports/states for UI and telemetry; readouts must stay
  passive. Audio sync mode is runtime-owned: `chirp-only` emits the active
  chirp-bin witness continuously, `passive` disables emission and uses
  program-audio phase correlation, and `hybrid` emits active pilot chunks only
  when passive confidence is weak. The active chirp-bin path now has its own
  short-window analyzer floor instead of inheriting the passive two-second gate.
  It uses cheap energy/onset proposals, dechirp plus fixed Goertzel bins,
  explicit time/frequency ambiguity candidates, and constrained local waveform
  correlation for final fractional delay. It also supports standalone source
  offset recovery from schedule/codebook state, which is the Raven/phone shape.
  Each classified chirp carries the full dechirped bin-energy surface; sync
  reports expose those bands so the same stream can become frequency-response
  calibration evidence.
  `Mimir.BufferSmoke
  --chirp-only-sync-self-test --sample-rate 192000` and
  `--hybrid-sync-self-test --sample-rate 192000` both recover a 1269.5-sample
  synthetic delay with printed 0.000 us error. Reports and sync states expose
  `delayUs`. `--standalone-chirp-bin-self-test --sample-rate 192000
  --delay-samples 96000` recovers a 500 ms delayed stream below printed
  microsecond precision without loopback.
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
  input 2 independently decoded a coherent clock, but its clock-offset
  difference was a period alias outside the live latency horizon and is rejected
  as insufficient anchors. Acoustic robustness is still open.
- Raven also has a 192 kHz loopback-capable Scarlett for co-streamer/game timing
  evidence. Do not move the heavy soundfield or sensor-fusion workload there.

## Immediate Re-entry Instruction

Prove the active chirp-bin path through real microphones, then feed Scarlett
ASIO callbacks into `Mimir.Runtime` and turn decoded clock fits into the
SRO/fractional-delay actuator. Keep loopback as timing authority. Do not call
synchronization analysis from UI/telemetry readouts. Do not restore deleted
script infrastructure because a stale doc once missed it.
