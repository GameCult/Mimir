# Sensor Fusion Architecture

## Objective

Produce an aligned, timestamped point cloud from the local visual rig while allowing latency, buffering, and cache convergence. The live render path can lag behind capture; it may not lie about coordinate ownership.

## Current Mechanism

The rig currently has usable capture surfaces:

- PS3 Eyes through FFmpeg DirectShow device names `PS3 Eye Universal` and `PS3 Eye Universal2`, verified at dual `320x240@60`.
- Kiyo-class RGB cameras through OpenCV/DirectShow/MSMF for color evidence.
- LeapUVC raw IR through OpenCV for near-field evidence.
- ChArUco tooling for intrinsics capture, but no shared fusion state yet.

That is capture. Capture is not a world model.

## Invariants

- World coordinates own truth.
- Capture adapters emit timestamped observations; they do not own fusion semantics.
- Fusion core is pure and testable: calibrated camera models plus 2D observations in, 3D points/confidence out.
- Point clouds and later brush/splat packets are cached render lowerings, not the canonical scene.
- A missing high-detail update renders stale-but-bounded state until better evidence arrives.

## Intended Change

The first pipeline slice adds these boundaries:

```mermaid
flowchart TD
    A["Capture adapters"] --> B["FramePacket ring"]
    B --> C["Detection adapter"]
    C --> D["Observation2D"]
    E["SensorRig calibration"] --> F["Fusion core"]
    D --> F
    F --> G["TrackCache"]
    G --> H["PointCloud PLY"]
    G --> I["Future brush/splat packets"]
```

## Ownership

- `localcast.sensor_fusion.core` owns calibrated camera models, 2D observations, DLT triangulation, reprojection gating, confidence scoring, point-cloud export, and track cache expiry.
- `localcast.sensor_fusion.adapters` owns driver-facing frame ingress. Its first concrete adapter is an FFmpeg raw BGR reader for the PS3 Eye DirectShow path.
- `scripts/sensor_fusion.py` owns CLI composition for offline observation files.
- `config/sensor-fusion.example.json` owns the declarative rig shape: capture hints, intrinsics, extrinsics, latency, and fusion thresholds.

## Cache Discipline

This follows the same architectural lesson as `fractal-domains-cache-that-bites.md`: state begins as semantic claims in a named domain, then lowers into cheaper packets.

For this project:

```text
sensor observation
-> calibrated world ray
-> triangulated marker claim
-> cached track
-> point cloud / brush packet / splat packet
-> renderer
```

The cache is not a bucket. `TrackCache` has a TTL and confidence update rule so the system can buffer and converge without pretending stale points are fresh. Later GPU buffers should mirror this shape: observation SoA, track SoA, brush packet SoA.

## Test Boundaries

The core can be tested without cameras:

- synthetic cameras triangulate a known point
- timestamp windows reject stale pairs
- track cache expiry is deterministic
- raw-frame adapter can be tested from an in-memory byte stream

Those tests matter because camera drivers are not unit-test dependencies. They are weather.

## Next Cut

1. Add detector adapters that turn PS3 Eye frames into `Observation2D` marker detections.
2. Load real ChArUco intrinsics/extrinsics into `config/sensor-fusion.json`.
3. Record an observation JSONL/JSON run from the two PS3 Eyes.
4. Generate a live sparse PLY stream or debug preview.
5. Lower cached tracks into brush/splat packets once the point cloud is stable.

## References

- OpenCV multi-camera calibration and triangulation are the geometry spine. See `research/visual-spatial-map/summary.md`.
- Jacob and Haeb-Umbach 2015 motivates audio-visual coordinate alignment. See `research/visual-spatial-map/mirrors/krekovic-2015-audio-visual-geometry-calibration.pdf`.
- Kerbl et al. 2023 and RTG-SLAM motivate splats as render packets, not state ownership. See `research/visual-spatial-map/summary.md` and `research/gpu-fusion-splatting/summary.md`.
- `E:\Projects\gamecult-site\GameCult\Blog\fractal-domains-cache-that-bites.md` supplies the cache discipline: semantic authority first, backend packet lowering second.
