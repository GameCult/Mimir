# Sensor Fusion Architecture

Visual fusion belongs in Fensalir over current Mimir runtime buffers.

## Live Flow

```mermaid
flowchart TD
    A["direct camera drivers"] --> B["Mimir.Runtime video buffers"]
    C["Leap stereo IR driver"] --> B
    B --> D["native reservoir handles"]
    D --> E["Fensalir GPU feature extraction"]
    E --> J["per-sensor LED curve solve"]
    E --> F["cross-view matching + flow"]
    J --> K["camera rig calibration solver"]
    K --> F
    F --> G["surface/material claims"]
    G --> H["brush/splat render budget"]
    H --> I["Spout2/program video"]
```

## Invariants

- Device timestamps beat arrival timestamps when available.
- Leap is the first timing-camera candidate.
- Process capture is a bridge edge, not the local six-camera foundation.
- Fensalir owns GPU extraction, fusion, material fitting, and render budgeting.
- Runtime buffers own retention and stream health, not scene reconstruction.
- LED strips are active calibration evidence. Each camera publishes ordered
  bright-curve observations in its own local frustum/clipspace; only the global
  residual calibration owner may fit those frustums into scene-space camera
  intrinsics/extrinsics.
- Stable LED index identity requires temporal/color/address coding or another
  correspondence source; uncoded identical lights are spline constraints, not
  metric depth authority.
- The first camera-rig solver fits bounded camera translation updates from a
  scene-anchored LED curve by minimizing ray-to-curve distance. Rotation,
  intrinsics, and distortion are future owners.
- Once camera frustums are coherent, every detected surface candidate can be
  resampled across the synchronized view set before Fensalir resolves it into
  spatiotemporal surface/splat claims.

## Next Cut

Replace provisional Leap projection constants with calibrated intrinsics and
add the global residual owner that consumes Leap surface errors, LED spline
observations, and later PS3 Eye/Kiyo/Eve feature tracks through one calibration
commit path.
