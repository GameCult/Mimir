# Perfect Machine

## Objective

Build one synchronized spatial stream machine from six cameras, six microphones,
program loopback, speakers, Leap timing, Fensalir GPU rendering, Faust DSP, and
OBS output.

For the larger public-facing field article that includes direct and networked
cameras, tracked smartphones, glowing motion-capture markers, chirplet room
mapping, and the shared spatiotemporal reservoir, read
[[perfect-machine-full-field|The Perfect Machine Full Field]].

The product is not a pile of bridge scripts. The product is a coherent live
volumetric field with explicit owners: visual evidence becomes Fensalir sensor
fusion, audio evidence becomes a synchronized sound field, and OBS receives
only program outputs.

## Current Mechanism

```mermaid
flowchart TD
    A["direct camera / audio / network producers"] --> B["Mimir.Runtime rolling buffers"]
    B --> C["native reservoir handles"]
    C --> D["Fensalir GPU + UI"]
    C --> E["Faust/native DSP"]
    D --> F["Spout2/program video"]
    E --> G["program stems"]
    F --> H["OBS"]
    G --> H
```

## Invariants

- One five-second live window is the timing authority.
- Every stream has a bounded rolling buffer.
- Missing data is absence, not a stale substitute.
- Late data can refine the live window only while it remains inside the window.
- Fensalir owns dense visual fusion, temporal accumulation, material fitting,
  rendering, D3D12 interop, UI, and Spout publication.
- Faust/native DSP owns hot audio alignment, suppression, voice separation,
  spatialization, and stem generation.
- Mimir owns configuration, calibration truth, runtime contracts, launch,
  status, and persistence.

## Reservoir Contract

The native reservoir stores time-ordered sample handles with typed views. It
owns retention and lookup, not payload memory. Producers append. Optimizers
refine. Fensalir/Faust consume.

Required sample kinds include camera frames, camera features, scene rays,
surface claims, material claims, audio blocks, phase claims, event claims, and
render packets.

`LocalcastRuntime` and `LocalcastProducer` are the lower native boundary.
`Mimir.Runtime` is the app-level synchronization surface that Fensalir hosts and
debugs.

## Volumetric Sensor Fusion

Camera fusion is cross-view evidence across the rolling window, not
latest-frame display. Leap stereo IR is the timing ground-truth candidate. The
driver path should deliver frame descriptors with device timestamps and native
or GPU handles, then Fensalir should do feature extraction, flow, matching,
material estimation, brush/splat budgeting, and final presentation on GPU.

## Volumetric Audio Field

The audio path aligns all microphones and loopbacks into one presentation
timeline, feeds Faust/native DSP with bounded blocks, and emits host voice,
co-streamer voice, ambient, transients, local loopback, co-streamer loopback,
and spatial bed as synchronized outputs. Starfire currently owns the local
Focusrite USB ASIO path: Scarlett Solo 4th Gen input channels plus ASIO
`Loopback 1/2` at up to 192 kHz. That loopback is the local program/timing
reference; room microphones are aligned to it before any volumetric field or
stem claim is allowed to cross into OBS. Raven also has a 192 kHz
loopback-capable Scarlett for co-streamer/game timing evidence, but the heavy
soundfield and sensor-fusion work belongs on Starfire.

Raw estimator detail stays inside the audio runtime. Meaning crosses the
boundary.

## Cut Line

Cut these from the hot path:

- file polling as a runtime API;
- process capture as the six-camera local foundation;
- OBS-side synchronization between raw sources;
- stale geometry clamped into looking current;
- probe scheduling against placeholder channels.

Keep PowerShell/FFmpeg/SRT only as bridge utility while native ingest matures.
