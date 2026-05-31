# Move Controller Stream

## Objective

Ship the first Mimir program surface around one concrete show shape:
Kiyo Pro is the RGB program view, PS3 Eyes track multiple PS Move spheres,
Raven screen capture enters as a synchronized video source, and Scarlett ASIO
provides the two hero mics plus Raven program-loopback timing evidence.

## Authority Map

- Owner: `Mimir.Runtime` owns sample ingest, rolling buffers, and typed visual
  evidence. Fensalir owns overlay presentation and final program pixels.
- Inputs: Kiyo Pro YUY2 video, both PS3 Eye Bayer8 feeds, Scarlett ASIO
  channels `asio-ch0..asio-ch3`, and Raven SRT screen capture on port `5200`.
- Outputs: `kiyo-pro-rgb` is the base program view; PS Move controller histories
  lower as Feature evidence through `MimirMoveControllerTracker`; `raven-display`
  stays in the `raven-sync` clock domain; ASIO labels identify shotgun,
  cardioid, and Raven loopback lanes.
- Derived state: Move 2D histories are observations, not 6DoF pose. Raven
  display timing is derived from Scarlett audio evidence, not SRT arrival time.
- Forbidden writers: PS3 Eye blob tracks do not own camera calibration; Raven
  network timestamps do not own global sync; Kiyo Pro does not own high-rate
  motion truth.
- Cut line: this profile is the first streamable overlay product, not the full
  online calibration solve.

## Nightwing Eyes/Moves Split

Nightwing is the right body for the PS3 Eyes and PS Move controllers: it has
the spare USB topology, BlueZ/Bluetooth, and the local HID path. That does not
make it a camera-placement authority.

The split is:

- Nightwing-local authority: read PS3 Eye frames, talk to PS Move HID/Bluetooth,
  apply LED schedules, preserve device timestamps, and extract compact sphere,
  marker, and sparse Eye feature observations.
- Starfire authority: own the canonical rolling window, Scarlett timing,
  global residual solve, camera placement, Kiyo/Raven program sync, and OBS
  program publication.
- Fensalir authority: own D3D12 dense feature extraction/fusion, surface claims,
  splat/reservoir resolution, and program pixels.

`MimirDistributedWitnessConfigurations.NightwingEyesMoves` records this as a
typed witness: it may emit `mimir.move_controller_observation_state`,
`mimir.camera_feature_track_state`, and `mimir.visual_marker_state`, but it must
not stream raw Eye media as the normal live contract and must not own the
canonical clock. Raw frames from Nightwing are diagnostic receipts only.

The first Nightwing receipt is good enough to build on: both PS3 Eyes enumerate
as stock Linux `ov534`/`gspca` V4L2 cameras on `/dev/video2` and `/dev/video3`.
Default V4L2 cadence is only 30 fps, but explicitly setting
`timeperframe=1/187` before streaming makes both cameras deliver about 187 fps
at 320x240 with zero sequence gaps. The witness worker must set frame interval;
otherwise Linux will quietly look worse than it is.

Nightwing also has a typed witness publisher staged as
`~/.local/bin/nightwing_typed_witness_publisher.py`. The repo source is
`tools/nightwing_typed_witness_publisher.py`. It sends binary MessagePack
`mimir.eve_sensor_observation.v1` records to `Mimir.EveSensorReceiver` on
`ws://192.168.1.66:8796/eve/periwinkle`; the receiver normalizes them into the
Mimir observation ledger, Odin projects them as `observation-stream` nodes, and
Nightwing's Eve TUI lowers those nodes in the `CultMesh Witness Streams` panel.
The current status records are camera-device receipts for `/dev/video*` and a
`nightwing-leap-tracking` placeholder that is intentionally `0` until real Leap
tracking owns that signal. Values are `[present, videoIndex, isPs3Eye]` for
camera-device receipts.

`MimirRoomCalibrationLockSolver` is the first room-lock coordinator. It consumes
the LED string candidate/residual, the camera-rig pose update frame, Move
controller feature-history candidates, and PS3 Eye sparse-feature candidates.
It only reports `Locked` when every witness family is present: multiple cameras
share enough LED indices, the rig solver produced enough pose updates with low
mean ray residual, multiple Move identities have stable history, and multiple
Eyes contribute enough stable feature tracks. Missing families are reported by
name instead of being papered over. `Mimir.BufferSmoke
--room-calibration-lock-smoke` proves a synthetic three-camera LED string, two
Moves, and two Eyes lock at confidence `0.778`, while removing Move history
correctly drops the result to `InsufficientWitnesses`.

## Runtime Profile

For the current stream-proof demo, use the corrected Starfire/Nightwing/Raven
profile:

```powershell
E:\Projects\Mimir\scripts\start-stream-proof.ps1
```

This profile keeps Nightwing raw Eye pixels off Starfire. Starfire ingests local
Scarlett ASIO, local Leap stereo IR, local Kiyo Pro RGB, Raven screen capture,
and Raven Realtek loopback PCM. Nightwing publishes compact Eye/Move
observations through `/eve/periwinkle`; those observations are calibration and
overlay evidence, not raw media ownership.

The profile declares:

- `leap-stereo-ir`: LeapUVC stereo IR / depth root.
- `kiyo-pro-rgb`: Kiyo Pro AR program view.
- `raven-display`: Raven screen capture from the muxed Raven A/V stream,
  locally demuxed to SRT port `5210`, in the `raven-sync` clock domain.
- `raven-realtk-loopback`: Raven Realtek render loopback from the same muxed
  Raven A/V stream, locally demuxed as f32 PCM to SRT port `5212`, also in
  `raven-sync`.
- `focusrite-asio`: Starfire hero mics plus local Raven/program loopback lanes
  on `asio-ch0..asio-ch3`.

For the narrower Leap/Eyes/Kiyo Pro bring-up path, use:

```powershell
E:\Projects\Mimir\scripts\start-mvp-leap-eyes-kiyo.ps1
```

This launches Starfire with only local Scarlett ASIO, local Leap stereo IR, and
local Kiyo Pro RGB in the Mimir rolling buffers. Nightwing does not stream PS3
Eye pixels to Starfire. The launcher stages `nw_eye_cap.py`,
`nw_move_hint.py`, and `nightwing_typed_witness_publisher.py` on Nightwing,
then starts the typed witness publisher with `--track-eyes`. Nightwing reads
`/dev/video2` and `/dev/video3`, extracts compact Move-sphere observations,
and sends `mimir.move_controller_observation_state.v1` claims to the
`/eve/periwinkle` CultMesh observation receiver. Starfire/Fensalir can consume
those claims for overlays and calibration without inheriting Nightwing's raw
camera bandwidth or local tracking authority.

The MVP profile refuses to start when LeapUVC is not enumerated. That is
intentional: the Leap is the dense close-range geometry root, not an optional
checkbox pretending to be present.

Use:

```powershell
$env:MIMIR_RUNTIME_CONFIG = "E:\Projects\Mimir\config\mimir-runtime.move-stream.local.json"
dotnet run --project .\src\Mimir.App\Mimir.App.csproj
```

The profile declares:

- `kiyo-pro-rgb`: Kiyo Pro program view, 640x480 YUY2 at the current reliable
  25 fps class.
- `ps3-eye-0`, `ps3-eye-1`: high-rate Move sphere visual witnesses.
- `focusrite-asio`: one ASIO producer that emits:
  `asio-ch0` shotgun hero mic, `asio-ch1` cardioid hero mic,
  `asio-ch2` Raven program loopback L, and `asio-ch3` Raven program loopback R.
- `raven-display`: Raven screen capture over SRT port `5200`.

Run the synthetic overlay smoke:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --move-controller-overlay-smoke
```

Run the live ingest smoke once Raven is sending:

```powershell
$env:MIMIR_RUNTIME_CONFIG = "E:\Projects\Mimir\config\mimir-runtime.move-stream.local.json"
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --seconds 5 --require-samples --sync-reference asio-ch2
```

## Raven Sender

On Raven, start the muxed A/V sender from the logged-in desktop session:

```powershell
E:\Projects\Mimir\scripts\start-raven-av-sender.ps1 -TargetHost 192.168.1.66 -Port 5200
```

That sender captures the desktop through `gdigrab`, encodes video with
`h264_nvenc`, captures Realtek/default render loopback through the repo WASAPI
loopback script, encodes audio as AAC, and muxes both into one MPEG-TS SRT
stream. On Starfire, `scripts\start-stream-proof.ps1` starts
`scripts\start-raven-av-demux.ps1`, which listens on port `5200` and splits the
single transport into local Mimir ingest legs:

```powershell
raven-display          local SRT 5210, copied H.264/MPEG-TS for decode
raven-realtk-loopback  local SRT 5212, decoded f32 PCM
```

If Raven program audio is also routed into Starfire Scarlett on
`asio-ch2/asio-ch3`, that remains another strong local timing witness. The
Realtek loopback is now part of the same Raven transport as the display before
Starfire demuxes it for Mimir's raw ingest APIs.

Raven display sync is not allowed to ride on SRT arrival time. Both
`raven-display` and `raven-realtk-loopback` are in the `raven-sync` clock
domain. When audio sync estimates the Realtek loopback offset against the
Scarlett reference, `MimirSynchronizedBufferPlanner` applies that correction to
the whole `raven-sync` domain, including the display. The LAN transport is now
one muxed A/V stream; the local split exists only at the Starfire demux edge
because Mimir currently ingests raw video and PCM through separate source
adapters.

## Kiyo AR Trail Receipt

The first offline proof that the Kiyo view can accept Move trails is:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\solve_stream_proof_frustum.py .\artifacts\runtime\all-sensor-articulated-20260531-232349\sweep\calibration-observations.json
```

That solver uses Nightwing's two matched optical witnesses as a provisional
local witness space, fits the Kiyo Pro observations into it, and writes a
`mimir.stream_proof_frustum_solve.v1` receipt plus an AR trail preview. It is
enough for the stream demo and for Starfire's global residual owner to consume;
it is not final metric room calibration.

## Multiple Move Controllers

Do not collapse Move observations into a generic bright blob. Each controller
has its own `controllerId` and expected RGB/time code. The PS3 Eyes can track
the sphere as a high-rate brightness witness; Kiyo Pro can later confirm color
identity; the commanded flash schedule ties the observed marker to the
controller identity.

`MimirMoveControllerTracker` keeps a bounded history per controller and lowers
those histories into the existing Feature evidence path. This is deliberately
2D observation history. 6DoF pose waits for calibrated frustums and the global
residual owner.

## Music-Keyed Calibration Words

`tools/music_keyed_move_chirp_sync.py` is the first planner for calibration
words that are both musical and machine-decodable. It listens to Scarlett ASIO,
uses the same Perlines-style adaptive FFT delta whitening as
`tools/nightwing_psmove_music_pulse.py`, autocorrelates the whitened broadband
spectral-rise function for tempo, estimates a lightweight root from recent
best-fit fundamentals, then writes one shared plan:

- beat-aligned chirps quantized to the estimated minor-pentatonic scale;
- de Bruijn-coded event symbols so adjacent timing/frequency/color words remain
  distinguishable;
- per-Move visual words using stable controller identities and unique colors;
  the default is no longer a square flash, but a sampled RGB contour with
  attack/release envelope, symbol-dependent hue glide, tremolo articulation,
  and an emphasized controller lane;
- a JSON receipt shaped like AquaSynth song analysis vocabulary:
  `tempo_bpm`, `beat_seconds`, `tempo_confidence`, `root_note`,
  `suggested_scale`, `scale_frequencies_hz`, and
  `whitened_spectral_autocorr`.

The synthetic smoke is:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\music_keyed_move_chirp_sync.py --mode synthetic --dry-run --events 8 --out-dir artifacts/runtime/music-keyed-move-chirp-sync-smoke
```

The live dry-run against Scarlett is:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\music_keyed_move_chirp_sync.py --dry-run --analyze-seconds 5 --events 8 --out-dir artifacts/runtime/music-keyed-move-chirp-sync-live-dry
```

For double-time material:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\music_keyed_move_chirp_sync.py --dry-run --tempo-grid doubletime --tempo-max-bpm 260 --analyze-seconds 5 --events 8 --out-dir artifacts/runtime/music-keyed-move-chirp-sync-dirtyphonics-dry
```

On the 2026-05-31 live dry-run, the planner produced a usable but not final
receipt: about `92.31` BPM, beat `0.650s`, tempo confidence `0.343`, root `A`,
key confidence `0.716`. That is enough to prove the dataflow, not enough to
claim final musical tempo authority.

For dense double-time material, use `--tempo-grid doubletime`; the planner then
selects the double-time member of the autocorrelation tempo family instead of
preferring the slower parent grid. On the Dirtyphonics-style live source this
produced about `171.43` BPM, beat `0.350s`, tempo confidence `0.358`, root `A`,
key confidence `0.703` after widening the search ceiling with
`--tempo-max-bpm 260`. The receipt records `tempo_family` so later decoders can
see the selected grid alongside half-time/double-time alternatives.

Visual calibration words are deliberately shaped like the audio words: one
gesture carries more than one bit. `--visual-gesture contour` records a
`mimir.move_visual_contour_word.v1` per Move per event, with `sample_rate_hz`,
duration, base identity color, symbol emphasis, and the exact RGB samples sent
over HID. Start conservatively around `--visual-gesture-hz 80`; the HID writer
accepted a short 80 Hz contour test on Nightwing, but the useful rate is the
rate cameras can recover cleanly, not merely the rate the controller accepts.
`--visual-gesture square` remains available as a baseline comparator.

`tools/sync_sweep.py` can now consume that exact plan with `--music-plan`.
That makes the plan the single authority for audio chirps, sampled Move RGB
contours, and offline video scoring. The sweep writes `gesture-plan.json`,
`event-schedule.json`, copied/rendered `chirp-train.wav`, per-sensor captures,
and `summary.json`. RGB camera receipts include a `visual_contour` block with
brightness-envelope correlation and color-chroma error against the expected
Move contour. The old peak-only score is still present as a timing witness; it
no longer pretends square flashes are the only visual language.

When `--music-plan` is used, the sweep also runs typed Mimir runtime sensor
receipts unless `--skip-mimir-live` is passed. The default receipts are:

- `config/mimir-runtime.perfect-machine.local.json` for local Leap stereo IR,
  Kiyo Pro, Kiyo, and local camera texture/depth/field-resource ingress;
- `config/mimir-runtime.raven-eve.example.json` for Raven display and Eve
  camera/mic frame-event listeners.

That receipt path is not raw media archival yet. It is the audit hook that
prevents Leap or Eve from being omitted by a DirectShow/Nightwing-only sweep.

After a sweep, run `tools/crunch_sync_sweep.py` over the sweep directory to
extract a compact calibration observation table. It merges Nightwing's typed
Move observations with local Kiyo/Kiyo Pro frame analysis, estimates each
local video's schedule shift against the event train, and writes
`calibration-observations.json` plus `calibration-observations.csv`.

Example using an already-written musical plan:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\sync_sweep.py --music-plan artifacts/runtime/music-keyed-move-chirp-sync-live/music-keyed-move-chirp-plan.json --out-dir artifacts/runtime/all-sensor-articulated-sync
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\crunch_sync_sweep.py artifacts/runtime/all-sensor-articulated-sync
```

The non-dry run targets the two wireless Nightwing Moves by default:
`00:07:04:a6:be:5f` on `/dev/hidraw2` and `00:06:f5:23:e2:d1` on
`/dev/hidraw3`. Nightwing now carries `/etc/udev/rules.d/70-move.rules`, which
sets Bluetooth PS Move hidraw nodes to `root:input 0660`. The `metacrat` user
is in `input`, so the non-root SSH writer can drive both spheres. Root SSH is
available as the short local alias `nwroot` for maintenance only; the hot LED
path should stay non-root.
