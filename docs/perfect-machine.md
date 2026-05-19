# Perfect Machine

## Objective

Build one synchronized spatial stream machine from six cameras, six microphones,
two speakers, Leap timing, Aquarium GPU rendering, Faust DSP, and OBS output.

The bridge scripts are not the product. They are scaffolding, launch surfaces,
calibration tools, and compatibility probes. The live hot path belongs to native
runtime code, GPU compute, and Faust.

## Current Mechanism

The repo currently proves the contracts:

```mermaid
flowchart TD
    A["sensor samples"] --> B["typed CultCache docs"]
    B --> C["Python deadline producers"]
    C --> D["deadline Spout texture"]
    D --> E["OBS"]
```

That was useful. It is not the final machine.

## Invariants

- One five-second spatiotemporal reservoir is the live authority.
- The reservoir is keyed by one shared presentation clock.
- Every sample has sensor id, local hardware time if available, arrival time,
  calibrated world transform, confidence, and provenance.
- Missing data is absence, not a stale substitute.
- Late data can refine the reservoir while it is inside the five-second window.
- Anything older than the live reservoir is offline calibration evidence, not
  broadcast geometry.
- Audio clocks, camera clocks, Leap timing, remote video, and OBS presentation
  are reconciled before rendering.
- Aquarium owns dense visual fusion, temporal accumulation, material fitting,
  brush/splat generation, final render target, and Spout publication.
- Faust owns hot audio DSP: voice separation, room suppression, Ambisonic/HOA
  spatialization, and loopback/source stem generation.
- LocalCastBridge owns configuration, calibration, typed contracts, launch,
  status, persistence, and compatibility surfaces.

## Native Runtime Shape

```mermaid
flowchart TD
    A["camera capture workers"] --> R["5s native reservoir"]
    B["mic/loopback capture workers"] --> R
    C["Leap worker"] --> R
    D["remote video/audio ingest"] --> R
    E["speaker probe scheduler"] --> R
    R --> F["clock/pose optimizer"]
    F --> R
    R --> G["Aquarium GPU fusion"]
    G --> H["multi-LOD surface/material field"]
    H --> I["brush/splat/TAA renderer"]
    R --> J["Faust DSP graph"]
    J --> K["program stems + spatial bed"]
    I --> L["Spout2 texture"]
    K --> M["OBS audio controls"]
    L --> N["OBS video source"]
```

## Reservoir Contract

The reservoir stores struct-of-arrays blocks, not one giant JSON-ish object.

Required rings:

- `camera_frame`: raw or GPU-importable image handles plus timestamps.
- `camera_feature`: keypoints, descriptors, optical flow, confidence, sensor id.
- `scene_ray`: calibrated rays from feature observations.
- `surface_claim`: triangulated or depth-estimated world samples.
- `material_claim`: albedo, roughness, metallic/specular hints, view direction.
- `audio_block`: aligned mic/loopback samples and per-source clock state.
- `phase_claim`: delay/SRO/phase/coherence estimates from chirplets and program
  reference.
- `event_claim`: claps, transients, gestures, active illumination pulses.
- `render_packet`: selected brush/splat work for the current presentation frame.

The reservoir owns retention. Producers append. Optimizers refine. Renderers
sample. No stage owns a private history that can outlive the reservoir.

## Visual Fusion

Camera fusion is feature matching across the reservoir, not a latest-frame
operation.

The visual path should:

- ingest all cameras continuously into pinned/native rings;
- extract features and flow on GPU where possible;
- match every plausible cross-view feature within time and epipolar bounds;
- use Leap as the strongest near-field timing/spatial witness when live;
- estimate surface points, normals, confidence, and material observations;
- keep multi-LOD cells resident for Aquarium compute reconciliation;
- emit only the frame budget needed for OBS while TAA accumulates detail.

The live target is not "points per frame." The live target is evidence per
reservoir window and how much of it Aquarium can reconcile before presentation.

## Audio Fusion

The audio path should:

- align all six microphones into one clocked field;
- use Focusrite dialogue mics as voice anchors;
- use camera mics as spatial/context witnesses;
- use loopback/program audio as ground truth where available;
- emit active probes only for channels that can observe them;
- continuously update delay, SRO, phase, frequency response, and confidence;
- feed Faust with aligned mic channels and control telemetry;
- produce host voice, co-streamer voice, ambient, transients, local loopback,
  co-streamer loopback, and spatial bed as synchronized outputs.

Raw phase is internal. Meaning crosses the boundary.

## Cut Line

Cut these from the hot path:

- Python dense rendering.
- Python camera feature extraction in the presentation loop.
- File polling as a native runtime API.
- OBS-side sync between independent raw sources.
- Stale geometry clamped into looking current.
- Probe scheduling against placeholder channels.

Keep these as tooling:

- Python calibration scripts.
- Python contract tests.
- Python device discovery.
- Compatibility Spout sender for diagnostics.
- Offline analysis and reproducible evidence ledgers.

## First Native Cut

1. Define the native reservoir ABI from `config/perfect-machine.example.json`.
2. Build a native reservoir process or Aquarium module that owns the five-second
   rings.
3. Move camera ingest into native capture workers that write reservoir samples.
4. Move audio ingest/phase claims into the same presentation-clock reservoir.
5. Let Aquarium consume reservoir surfaces/material claims directly on GPU.
6. Let Faust consume reservoir audio blocks and source controls directly.
7. Keep the old bridge only as a comparison and fallback surface.

