# Sensor Fusion Architecture

## Objective

Produce an aligned, timestamped point cloud from the local visual rig while allowing latency, buffering, and cache convergence. The live render path can lag behind capture; it may not lie about coordinate ownership.

## Current Mechanism

The rig currently has usable capture surfaces:

- PS3 Eyes through FFmpeg DirectShow device names `PS3 Eye Universal` and `PS3 Eye Universal2`, verified at dual `320x240@60`.
- Kiyo-class RGB cameras through OpenCV/DirectShow/MSMF for color evidence.
- LeapUVC raw IR through OpenCV for near-field evidence.
- ChArUco tooling for intrinsics capture, but no shared fusion state yet.

That is capture. Capture is not a world model. The previous live producer also
painted camera colors onto guessed body and room surfaces. That was useful
deadline scaffolding, but it is not enough for detailed reconstruction.

## Invariants

- World coordinates own truth.
- Capture adapters emit timestamped observations; they do not own fusion semantics.
- Fusion core is pure and testable: calibrated camera models plus 2D observations in, 3D points/confidence out.
- Point clouds and later brush/splat packets are cached render lowerings, not the canonical scene.
- A missing high-detail update renders stale-but-bounded state until better evidence arrives.

## Intended Change

The live pipeline now has two measurement-quality paths: sparse marker fusion
for calibrated tracked objects and GPU-owned dense RGB stereo/flow for surface
detail. A CPU dense stereo matcher exists only as a debug reference and is
opt-in through `--cpu-dense-stereo`; production dense fusion may not route
through Python loops. The provisional Kiyo geometry remains a lie detector, not
truth, until `config/sensor-fusion.json` carries measured extrinsics.

The first pipeline slice adds these boundaries:

```mermaid
flowchart TD
    A["Capture adapters"] --> B["FramePacket ring"]
    B --> C["Camera quality controller"]
    C --> D["manual exposure/gain/focus commands"]
    B --> E["Detection adapter"]
    E --> F["Observation2D"]
    B --> G["Dense stereo matcher"]
    H["SensorRig calibration"] --> I["Fusion core"]
    F --> I
    I --> J["TrackCache"]
    G --> K["RGB surface claims"]
    J --> L["PointCloud PLY"]
    J --> M["CultCache RenderFrame document"]
    K --> M
    M --> N["Spout sink / Aquarium GPU renderer"]
    N --> O["Spout2 sender texture"]
    O --> P["OBS Spout2 source"]
```

## Ownership

- `localcast.sensor_fusion.core` owns calibrated camera models, 2D observations, DLT triangulation, reprojection gating, confidence scoring, point-cloud export, and track cache expiry.
- `localcast.sensor_fusion.adapters` owns driver-facing frame ingress. Its first concrete adapter is an FFmpeg raw BGR reader for the PS3 Eye DirectShow path.
- `localcast.sensor_fusion.camera_control` owns measurement-quality policy: luminance, clipping, contrast, and sharpness in; normalized exposure/gain/focus commands out. Driver adapters own translating those commands to OpenCV, DirectShow, vendor tools, or no-op mocks.
- `localcast.sensor_fusion.dense_stereo` owns a debug CPU reference for dense calibrated RGB surface claims. It is useful for tests and shader parity, but the live million-splat path belongs to Aquarium/GPU compute.
- `localcast.sensor_fusion.render_bridge` owns the in-process render-frame ABI: cached point claims plus target dimensions, Spout sender name, source timestamp range, intended visual presentation time, and ambisonic/audio alignment time.
- `localcast.sensor_fusion.cultcache_docs` owns the typed CultCache document boundary for live visual state.
- `localcast.sensor_fusion.spout_output` owns the deadline OBS publication sink: render-frame packet in, GPU texture plus Spout sender heartbeat out.
- `scripts/sensor_fusion.py` owns CLI composition for offline observation files.
- `scripts/stream_spout.py` owns the live Spout sender loop for OBS.
- `config/sensor-fusion.example.json` owns the declarative rig shape: capture hints, intrinsics, extrinsics, latency, and fusion thresholds.

## Cache Discipline

This follows the same architectural lesson as `fractal-domains-cache-that-bites.md`: state begins as semantic claims in a named domain, then lowers into cheaper packets.

For this project:

```text
sensor observation
-> calibrated world ray
-> triangulated marker claim
-> cached track
-> render frame packet
-> Aquarium brush/splat packet
-> Spout sender texture
-> OBS source
```

The cache is not a bucket. `TrackCache` has a TTL and confidence update rule so the system can buffer and converge without pretending stale points are fresh. Later GPU buffers should mirror this shape: observation SoA, track SoA, brush packet SoA.

## Test Boundaries

The core can be tested without cameras:

- synthetic cameras triangulate a known point
- timestamp windows reject stale pairs
- track cache expiry is deterministic
- raw-frame adapter can be tested from an in-memory byte stream
- adaptive camera control responds to overexposure without live hardware
- dense stereo turns shifted textured camera pairs into calibrated RGB surface claims

Those tests matter because camera drivers are not unit-test dependencies. They are weather.

## OBS And Spout Boundary

OBS should see a named Spout sender, not a camera window, file tail, or desktop capture. Current references confirm the ordinary Windows path: the Off World Live OBS Spout2 plugin receives Spout shared textures, while the Spout2 SDK is the app-side texture sharing surface.

The intended production handoff is:

```text
LocalCastBridge TrackCache
-> CultCache render-frame document
-> Aquarium Engine typed runtime state
-> GPU point/brush/splat buffer
-> D3D render target
-> Spout2 sender named "LocalCastBridge Point Cloud"
-> OBS Spout2 Capture source
```

`RenderFramePacket` is the in-process shape. The live boundary is now a typed CultCache MessagePack document, with CultNet `document_put` as the intended process/network API boundary. JSON render-frame files are compatibility scaffolding only.

The current streamable cut is:

```text
CultCache render-frame document
-> compact anisotropic screen brush lowering
-> OpenGL texture upload
-> SpoutGL sendTexture
-> OBS Spout2 Capture source
```

This is deliberately a sink boundary, not a competing world model. Aquarium should replace the renderer body, not the capture/fusion/timing contract.

## Latency Contract

Latency is allowed. Drift is not.

Each render frame carries:

- `source_time_min_ns` and `source_time_max_ns`: the observation window that produced the cached points.
- `present_time_ns`: when the visual frame wants to be presented after visual buffering.
- `audio_alignment_time_ns`: the corresponding ambisonic output time.

That lets the final stream compositor buffer point-cloud visuals until they align with the bounded audio-field cache. The renderer may show a slightly older world if the packet is coherent and labeled. It may not quietly mix fresh audio with stale visual geometry and call that sync.

## Next Cut

1. Load real ChArUco intrinsics/extrinsics into `config/sensor-fusion.json`; the current dense Kiyo pair geometry is provisional and should not be mistaken for truth.
2. Move dense matching from CPU block search to GPU-resident stereo/flow so the million-sample target is not murdered by Python loops.
3. Add detector adapters that turn PS3 Eye frames into `Observation2D` marker detections.
4. Record an observation run from the two PS3 Eyes into typed CultCache docs.
5. Feed `RenderFramePacket` into Aquarium Engine.
6. Add Aquarium-side GPU point/brush rendering to a D3D render target.
7. Publish the render target as a Spout2 sender and receive it in OBS.
8. Replace compatibility JSON render-frame paths once no deadline script depends on them.

## References

- OpenCV multi-camera calibration and triangulation are the geometry spine. See `research/visual-spatial-map/summary.md`.
- Jacob and Haeb-Umbach 2015 motivates audio-visual coordinate alignment. See `research/visual-spatial-map/mirrors/krekovic-2015-audio-visual-geometry-calibration.pdf`.
- Kerbl et al. 2023 and RTG-SLAM motivate splats as render packets, not state ownership. See `research/visual-spatial-map/summary.md` and `research/gpu-fusion-splatting/summary.md`.
- Spout2 and the OBS Spout2 plugin are the Windows/OBS texture-sharing bridge. See `research/spout-obs-bridge/summary.md`.
- `E:\Projects\gamecult-site\GameCult\Blog\fractal-domains-cache-that-bites.md` supplies the cache discipline: semantic authority first, backend packet lowering second.
