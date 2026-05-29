# D3D12 Stereo Depth Provenance

Date: 2026-05-29

## Purpose

This note records the current fast GPU depth-estimation search as provenance for
a future Mimir/Fensalir D3D12 compute implementation.

The north star is `libSGM`: not as a dependency, not as a CUDA bridge, and not
as a permission slip to add an adapter. The useful shape is a fast,
deterministic stereo disparity pipeline whose ownership can be rebuilt inside
Fensalir's D3D12 resource model.

## Authority Map

Owner:

- Fensalir owns the live D3D12 compute kernels, GPU resources, fences,
  scheduling, and depth/surface claim lowering.

Inputs:

- Rectified synchronized camera texture pairs from Mimir-declared
  `Texture2D` resources.
- Camera calibration: intrinsics, extrinsics, baseline, rectification map, and
  valid-time/version metadata.
- Rolling-window timing chosen by `MimirSynchronizedBufferPlanner`.

Outputs:

- GPU-resident disparity/depth resources, preferably fixed format and size per
  calibrated camera pair.
- FieldEvidence claims for depth support, confidence, invalid/occluded pixels,
  and later surface/material lowerings.
- Diagnostics: kernel timings, disparity range, confidence histogram, invalid
  ratio, calibration version, and source texture fence values.

Derived State:

- Depth is derived from synchronized stereo textures plus calibration.
- Per-frame disparity confidence is derived evidence, not a clock or camera
  authority.
- Monocular model depth remains optional reference/assistive evidence unless a
  later invariant gives it a named role.

Forbidden Writers:

- CUDA runtime code must not become the live path.
- TensorRT/ONNX monocular demos must not own visual geometry.
- CPU image copies/readback must not be inserted for convenience.
- OBS, EVE, or bridge utilities must not decide depth or synchronization.

Shared Paths:

- Direct user capture, replayed rolling-window frames, future Raven/phone
  camera feeds, and calibration-session frames must all enter through typed
  resources and use the same depth compute/claim path once they are calibrated
  stereo pairs.

Deletion Line:

- If a prototype requires persistent CPU depth images, a side transport daemon,
  or a CUDA compatibility layer, it is research-only and does not graduate into
  the live machine.

## Reference Shape: libSGM

Repository: https://github.com/fixstars/libSGM

License: Apache-2.0.

Why it matters:

- It is a CUDA implementation of Semi-Global Matching for stereo disparity.
- It was built by the Fixstars/adaskit ADAS effort, so it is pointed at the
  same autonomous-driving pressure field rather than generic image demos.
- Reported benchmark in the upstream README: `1024 x 440`, disparity `128`,
  four SGM paths, subpixel enabled:
  - GTX 1080 Ti: `2.0 ms` / `495.1 FPS`
  - RTX 3080: `1.5 ms` / `651.3 FPS`
  - Tegra X2: `28.5 ms` / `35.1 FPS`
  - Xavier MAXN: `9.0 ms` / `110.7 FPS`

Port lesson:

- Use the pipeline and data movement as a model: matching cost, path
  aggregation, winner/subpixel selection, confidence/invalid handling.
- Rebuild the kernels in HLSL/D3D12 compute against Fensalir-owned resources.
- Preserve the ADAS-style assumption that calibrated stereo gives metric depth
  when baseline and rectification are real.

## Secondary References

Fast-ACVNet: https://github.com/gangweiX/Fast-ACVNet

- License: MIT.
- Learned real-time stereo matching; useful for quality and failure-mode
  comparison.
- Upstream reports `45 ms` for Fast-ACVNet+ in its KITTI/Scene Flow table.
- Not the first Mimir implementation target because it brings model training,
  weights, and framework deployment complexity.

UniMatch / GMStereo: https://github.com/autonomousvision/unimatch

- License: MIT.
- Unified flow/stereo/depth research code.
- Useful as a modern learned-stereo comparison point and possible later
  confidence/feature guide, not a kernel blueprint.

Depth Anything V2 Small: https://github.com/DepthAnything/Depth-Anything-V2

- License caveat: V2 Small is Apache-2.0; Base/Large/Giant are CC-BY-NC-4.0.
- Excellent monocular relative-depth reference, especially through TensorRT
  ports, but monocular output is not metric scene truth by itself.

Depth Anything TensorRT CLI:
https://github.com/spacewalk01/depth-anything-tensorrt

- License: MIT.
- Reports fast RTX 4090 inference for Depth-Anything-S/B/L.
- Useful as performance context only. It must not displace calibrated stereo
  geometry or Fensalir's D3D12 ownership.

Fast-FoundationStereo:
https://github.com/NVlabs/Fast-FoundationStereo

- Important research reference for real-time zero-shot stereo.
- License is research/non-commercial only, so it is not a permissive live
  dependency candidate.

## D3D12 Compute Implementation Sketch

First implementation should stay boring and inspectable:

1. Rectify or require pre-rectified stereo pairs.
2. Convert sampled luma/feature texture into a compact matching domain.
3. Build per-pixel disparity matching costs over a fixed disparity range.
4. Aggregate along a small fixed path set, starting with four directions to
   match the `libSGM` reference benchmark shape.
5. Select winner disparity and subpixel refinement.
6. Emit disparity, confidence, and invalid/occlusion masks as GPU resources.
7. Lower those resources into FieldEvidence depth/support claims.

Important D3D12 resource notes:

- Prefer persistent UAV/SRV resources sized by camera pair profile.
- Use explicit producer/consumer fences already established for camera texture
  leases and program-output publication.
- Keep disparity range and path count as profile-level cost levers, not
  per-frame mode flags scattered through the renderer.
- Do not read back depth except for gated diagnostics and receipts.

## Initial Verification Targets

- Synthetic rectified stereo pair with known constant disparity.
- Slanted plane with known disparity gradient.
- Occlusion edge where invalid/confidence behavior is visible.
- Real Leap stereo IR pair once calibration is pinned.
- Full rolling-window path: camera textures -> depth compute -> FieldEvidence
  claim -> Fensalir debug overlay/resource receipt.

Negative checks:

- No CUDA runtime loaded.
- No CPU disparity image in the live path.
- No monocular model output accepted as metric depth without a named scale
  authority.
- No untyped texture handle accepted without resource declaration and fence
  provenance.
