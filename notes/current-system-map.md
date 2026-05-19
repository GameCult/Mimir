# Current System Map

LocalCastBridge is intentionally thin. The target live machine is not the
deadline bridge; it is the native rolling reservoir described in
`docs/native-rebuild-plan.md`, `docs/perfect-machine.md`, and
`config/perfect-machine.example.json`.

## Perfect Machine Target

```mermaid
flowchart TD
    A["native camera workers"] --> R["single 5s rolling reservoir"]
    B["native mic/loopback workers"] --> R
    C["Leap timing/IR workers"] --> R
    D["remote video/audio workers"] --> R
    R --> E["Aquarium GPU fusion + material field"]
    R --> F["Faust DSP"]
    E --> G["Spout2 texture"]
    F --> H["program stems + spatial bed"]
    G --> I["OBS"]
    H --> I
```

Ownership:

- LocalCastBridge owns configuration, calibration, contract tests, launch, status, and persistence.
- Aquarium owns dense visual fusion, material/brush/splat reconciliation, final render target, and Spout publication.
- Faust owns hot audio DSP and program stem generation.
- OBS owns broadcast controls.
- Python remains tooling only: calibration, contract tests, device discovery,
  offline analysis, and diagnostics. It is not the stream hot path.
- OpenGL is not the production Spout sink. Aquarium owns production Spout2
  publication.
- `native/reservoir` is the first native crate. It now uses one time-ordered
  rolling buffer with typed views. Retention has one owner.

Invariant: the five-second spatiotemporal reservoir is the live authority.
Producers append sample handles through typed native calls, optimizers refine,
Aquarium/Faust sample and interpret the pointed-to payloads. No private history
outlives the reservoir, and typed views do not own retention. The reservoir does
not pretend to own GPU image memory or Faust audio buffers. Edge JSON may
declare schema or export diagnostics; live process/network data is typed
CultNet documents.

```mermaid
flowchart TD
    A["config/localcast.json"] --> B["sender-start.ps1"]
    C["FFmpeg on sender"] --> D["Windows desktop capture"]
    C --> E["DirectShow audio capture"]
    D --> F["h264_nvenc encode"]
    E --> G["AAC encode"]
    F --> H["SRT MPEG-TS video endpoint"]
    G --> I["SRT MPEG-TS audio endpoint(s)"]
    H --> J["OBS Media Source"]
    I --> K["OBS Media Source per audio source"]
    J --> L["OBS scene composition"]
    K --> L
```

## Ownership

- Config owns source identity, receiver address, ports, and encoding knobs.
- FFmpeg owns capture, encoding, and network transport.
- OBS owns source activation, layout, filters, monitoring, and recording/streaming.
- This repo owns repeatability and memory.

## Why Not Plugin First

An OBS plugin would be justified if OBS could not ingest stable LAN streams, or if a plugin were needed to expose independent audio controls. V1 gets independent audio controls by making each source an OBS Media Source. That is boring. Boring is allowed to win when it is correct.

## Known Risks

- Windows audio source names vary by driver and localization.
- Some FFmpeg builds omit SRT or NVENC.
- Separate endpoints can drift; local latency should be tuned before adding a synchronization layer.
- OBS SRT reconnection behavior can be fussy. Use stable ports and source reactivation before treating port changes as a fix.

## Audio Field Sidecar

The six-microphone Ambisonic path is separate from the OBS endpoint path.

```mermaid
flowchart TD
    A["config/audio-field.json"] --> B["scripts/audio_field.py"]
    C["local Focusrite shielded cardioid"] --> D["local reference timeline"]
    E["neighbor Focusrite shotgun"] --> F["remote dialogue capture"]
    G["Kiyo + PS Eye camera mics"] --> H["spatial/context captures"]
    I["2 speaker outputs"] --> J["calibration sweep"]
    J --> D
    J --> F
    J --> H
    R["confidence probe optimizer"] --> P["known speaker chirplets"]
    P --> Q["runtime delay/SRO/phase estimator"]
    D --> Q
    F --> Q
    H --> Q
    Q --> S["phase-meaning extractor"]
    S --> T["audio-phase-field.msgpack"]
    S --> K["delay + SRO alignment"]
    K --> L["bounded field cache"]
    L --> M["aligned six-channel blocks"]
    M --> N["FOA encoder"]
    M --> V["Faust mic-field publisher"]
    V --> W["localcast.audio.mic_field"]
    W --> X["Aquarium Faust voice-separation graph"]
    N --> O["AmbiX ACN/SN3D bus: W,Y,Z,X"]
```

Ownership:

- `config/audio-field.json` owns mic/speaker identity, machine/device mapping, clock domains, field channel order, geometry, gain, delay, polarity, role/quality priority, capture policy, and Ambisonic bus format.
- `scripts/audio_field.py` owns profile validation, local device checks, calibration stimulus generation, clock-domain planning, shared-input capture helpers, and FOA encoding of already aligned six-channel WAVs.
- `audio_field/` owns unit-testable buffering, bounded-latency convergence, injectable port protocols, and pipeline orchestration.
- Runtime sync owns per-block chirplet observations from known speaker output and updates delay/SRO/phase estimates with confidence gates before alignment.
- `audio_field.phase_meaning` owns extracting usable meaning from internal phase/chirplet evidence. The live cache publishes delay correction, coherence, confidence, distance-equivalent delta, reference-bleed/suppression estimates, correction energy, and active-probe need; raw phase bands stay inside the estimator.
- `scripts/stream_live_audio_field.py` is the current live local capture owner. It publishes `localcast.audio.mic_field` and `localcast.audio.phase_field` from the visible local Kiyo/PS Eye/Scarlett inputs, uses Scarlett loopback as the known reference, and keeps missing neighbor/Kiyo-right channels as explicit placeholders until synchronized feeds exist.
- `audio-phase-field-status.json` declares the phase runtime mode. Current deadline mode is `live-local-capture` with closed-loop probe capture when Scarlett loopback is available.
- Active probe optimization owns extra chirplet emission when confidence drops, bounded by level/spacing/audibility budget. `audio_field.active_probe` is the runtime join that converts phase-field confidence into emitted chirplet WAVs/manifests and playback. Probe WAVs rotate through fixed slots and the manifest rotates at a byte cap; calibration may be dense, but artifacts must stay bounded.
- The camera/sensor-fusion pipeline may publish world poses later; it does not own audio clocks or channel timing.
- OBS may ingest rendered output later; it is not the authority for the Ambisonic field.
- Aquarium/Faust owns hot voice separation after LocalCastBridge publishes `localcast.audio.mic_field`; LocalCastBridge owns alignment, timing, mic roles, graph id, and controls.

Invariant: distributed camera/Focusrite microphones must be aligned and resampled into one reference timeline before FOA encoding. Latency is allowed as bounded buffering, but cache depth must converge toward real-time. Speaker output chirplets are live telemetry; delay/SRO/phase state must update during runtime, not only during setup. Extra chirplets may be emitted automatically when confidence drops, but only under the active probe optimizer's budget. The local shielded cardioid and neighbor shotgun are the high-quality dialogue anchors; camera mics provide spatial/context evidence.

## Visual Fusion Cut Line

```mermaid
flowchart TD
    A["native camera/Leap sample handles"] --> R["rolling reservoir"]
    B["audio/phase/event handles"] --> R
    R --> C["Aquarium GPU feature/fusion/material/brush"]
    C --> D["Aquarium Spout2 sender"]
    R --> E["typed CultNet status/docs"]
    D --> F["OBS Spout2 Capture"]
```

Ownership:

- Native capture workers own device reads and append handles. They do not own
  scene reconstruction.
- The rolling reservoir owns the live edge, retention, ordering, and typed
  lookup.
- Aquarium owns dense visual fusion, material fitting, point/brush/splat
  lowering, render budgeting, and Spout2 publication.
- CultNet typed documents carry status/control/process state. JSON is schema or
  diagnostic export only.
- `scripts/live_sensor_fusion.py`, Python fallback producers, JSON LOD stores,
  and `localcast.sensor_fusion.spout_output` are diagnostics/migration fossils.
  They must not be extended as production surfaces.

Invariant: fallback/demo evidence cannot enter ground-truth reservoir kinds.
Render packets, LOD cells, brush packets, and overlays are derived from reservoir
claims. The renderer cannot invent scene priority from string prefixes.

## OBS Program Surface

OBS should not expose raw unsynchronized ingest as broadcast controls once a synchronized audio/video program is available.

```mermaid
flowchart TD
    A["aligned audio program timeline"] --> B["setup_obs_synced_program.py"]
    B --> C["Host Voice stem"]
    B --> D["CoStreamer Voice stem"]
    B --> E["Ambient stem"]
    B --> F["Transients stem"]
    B --> G["CoStreamer Loopback stem"]
    B --> H["Local Loopback stem"]
    I["CultCache visual/audio timing"] --> J["Spout/Aquarium program video"]
    C --> K["OBS synchronized scene"]
    D --> K
    E --> K
    F --> K
    G --> K
    H --> K
    J --> K
```

Ownership:

- `scripts/setup_obs_synced_program.py` owns packaging aligned program audio into OBS-controllable stems and muting/disabling raw unsynchronized OBS inputs.
- `scripts/capture_co_streamer_surfaces.py` owns the late-arriving neighbor surface import: it captures neighbor Focusrite and neighbor loopback over SSH while recording local loopback, estimates the remote-family offset, and writes aligned co-streamer surfaces for the OBS stem packer.
- `scripts/wasapi-loopback-capture.ps1` owns the direct primary-playback loopback path. It bypasses virtual mixers and asks Windows Core Audio for the default render endpoint loopback stream. It must run in the neighbor's interactive console session to receive render packets.
- OBS owns final source volume, track assignment, filters, and stream/record output.
- Aquarium/Spout owns synchronized program video, not raw remote desktop capture.

Invariant: raw SRT ingest, Desktop Audio, Mic/Aux, and other non-program scene items may exist for diagnostics, but they must be disabled or muted when broadcasting the synchronized program. Missing synchronized feeds should appear as explicit silent placeholders, not as live unsynchronized substitutes. The co-streamer Focusrite, co-streamer loopback, and remote video are the latest-arriving surface family; their measured delay should set the presentation buffer horizon instead of being patched in after the local field. Do not reintroduce Voicemeeter as the loopback authority without a concrete invariant; prefer direct WASAPI loopback or a sender FFmpeg build with native WASAPI input.
