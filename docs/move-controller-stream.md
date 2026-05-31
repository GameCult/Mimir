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

On Raven, start screen capture from the logged-in desktop session:

```powershell
E:\Projects\Mimir\scripts\start-raven-screen-capture-sender.ps1 -TargetHost 192.168.1.66 -Port 5200
```

Route Raven program audio into the Scarlett path that appears on
`asio-ch2/asio-ch3`. That audio is the timing witness that earns the
`raven-sync` correction for `raven-display`.

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
