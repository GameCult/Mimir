# Current System Map

LocalCastBridge is intentionally thin.

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
    D --> K["delay + SRO alignment"]
    F --> K
    H --> K
    K --> L["aligned six-channel field"]
    L --> M["FOA encoder"]
    M --> N["AmbiX ACN/SN3D bus: W,Y,Z,X"]
```

Ownership:

- `config/audio-field.json` owns mic/speaker identity, machine/device mapping, clock domains, field channel order, geometry, gain, delay, polarity, role/quality priority, capture policy, and Ambisonic bus format.
- `scripts/audio_field.py` owns profile validation, local device checks, calibration stimulus generation, clock-domain planning, shared-input capture helpers, and FOA encoding of already aligned six-channel WAVs.
- The camera/sensor-fusion pipeline may publish world poses later; it does not own audio clocks or channel timing.
- OBS may ingest rendered output later; it is not the authority for the Ambisonic field.

Invariant: distributed camera/Focusrite microphones must be aligned and resampled into one reference timeline before FOA encoding. The local shielded cardioid and neighbor shotgun are the high-quality dialogue anchors; camera mics provide spatial/context evidence.
