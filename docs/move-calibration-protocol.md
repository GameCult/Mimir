# Move Calibration Protocol

Mimir owns Move calibration and sensor fusion. Muninn owns source-local facts:
camera marker candidates, USB controller IMU/button state, and local Move light
commands. Odin discovers and projects the surfaces. Fensalir consumes Mimir's
resolved poses after Mimir has earned them.

## Current Stream Shape

- Starfire should run Muninn as a local capture/control daemon. Its Move stream
  address is `muninn:starfire:move-evidence`.
- Nightwing runs Muninn for its USB Move and local camera witnesses. Its Move
  stream address is `muninn:nightwing:move-evidence`.
- Mimir on Starfire publishes resolved pose frames at
  `mimir:starfire:move-controller-poses`.
- Quest can participate as an external tracked VR reference over USB:
  `quest:usb:headset-pose`, `quest:usb:left-controller-pose`, and
  `quest:usb:right-controller-pose`.
- Starfire ASIO/Scarlett loopback can provide cue timing, but it is not a Move
  pose authority.

The Move hot path is CultMesh stream frames. On the same host, those frames use
shared-memory bytes rings. Across hosts, the remote peer still crosses the
Verse/network boundary. The current Raven program media bridge is not same-host
zero-copy into Mimir/Fensalir; it sends CultNet/CultMesh media documents and
lowers to local UDP for OBS compatibility.

## Required Capture Phases

1. `preflight-streams`: hold both Moves still and visible; prove stream freshness,
   clock skew, and controller ids.
2. `dark-stillness`: lights off, both Moves still; estimate gyro bias, gravity,
   and magnetometer baseline.
3. `lit-stillness`: light one Move at a time, then both; map light commands to
   controllers and measure centroid stability.
4. `axis-sweeps`: slow pitch/yaw/roll sweeps; fit gyro scale, accelerometer
   frame, and optical angular consistency.
5. `figure-eight`: broad figure-eight motion; fit magnetometer hard/soft iron
   and cross-axis coupling.
6. `quest-reference`: optional Quest headset/controller reference with the Move
   between the two Quest controllers in front of the headset.
7. `validation-pass`: held-out free motion; promote only if triangulated optical
   residuals and IMU prediction residuals pass.

## Quest Access Preflight

The Quest must be authorized for USB debugging before Starfire Muninn can query
it. Muninn owns the access surface:

```powershell
muninn serve --store C:\Meta\Odin\state\muninn.telemetry.cc --host starfire --quest-adb --quest-serial 1WMHHB68PG1515
```

When the Quest is authorized, Muninn publishes `muninn.quest_access.v1` and
advertises `muninn:<host>:quest-input`, `muninn:<host>:quest-poses`, and
`muninn:<host>:quest-warped-video-input`. Mimir consumes the access/pose
surfaces as calibration evidence; it does not own Quest USB access.

```powershell
muninn quest-access-status --store C:\Meta\Odin\state\muninn.telemetry.cc
```

ADB authorization proves USB access, device identity, and basic headset state
only. It does not expose Quest headset or controller poses; those still require
a Quest/OpenXR witness bridge that publishes pose samples through Muninn. The
current calibration protocol can still run without Quest, but Quest poses are
the preferred external reference when the headset and both controllers are
sitting around the Move.

## Data Products

- `mimir.move_fusion_rig_calibration.v1`: camera intrinsics/extrinsics, clock
  association policy, and gyro unit scale.
- `mimir.move_imu_calibration.v1`: per-controller gyro bias/scale,
  accelerometer gravity frame, magnetometer correction, and noise estimates.
- `mimir.move_controller_identity_map.v1`: stable association between Muninn
  USB ids, light receipts, and optical orb identities.
- `mimir.move_calibration_receipt.v1`: sample counts, residuals, rejected
  phases, and promotion decision.

## Preflight Command

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --move-calibration-protocol-smoke --output artifacts\move-calibration\protocol.cc
```

That writes the typed protocol as `mimir.move_calibration_protocol.v1` into a
CultCache receipt. The real hardware runner should consume this document plus
Muninn's `muninn.quest_access.v1` when Quest is available, then append capture
receipts instead of inventing a parallel checklist.
