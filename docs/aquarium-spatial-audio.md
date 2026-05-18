# Aquarium Spatial Audio

## Objective

Publish the live spatial audio field through the same typed state discipline as the splat renderer. Aquarium consumes the AmbiX bus and hands it to native Faust DSP; LocalCastBridge owns capture, sync, calibration, and stream timing.

## Current Mechanism

```mermaid
flowchart TD
    A["aligned microphone field"] --> B["FOA encoder"]
    B --> C["AmbiX ACN/SN3D W,Y,Z,X blocks"]
    C --> D["localcast.audio.spatial_frame"]
    A --> H["source-event analysis"]
    H --> I["localcast.audio.source_events"]
    D --> E["audio-state.msgpack / CultNet document_put"]
    I --> J["audio-events.msgpack / CultNet document_put"]
    E --> F["Aquarium client"]
    J --> F
    F --> G["native Faust spatial DSP + volumetric renderer"]
```

Live files:

- `calibration/runs/audio-state.msgpack`: latest typed AmbiX audio block.
- `calibration/runs/audio-stream-status.msgpack`: audio publisher heartbeat.
- `calibration/runs/audio-events.msgpack`: latest typed dialogue-focus and transient vector field.

Document type:

- `localcast.audio.spatial_frame`
- schema id: `gamecult.localcast.audio.spatial_frame.v1`
- key: `localcast.audio.spatial-frame.live`
- payload: MessagePack array decoded as `CultSpatialAudioFrame`
- audio format: interleaved `float32`, 48 kHz, AmbiX ACN/SN3D channels `W,Y,Z,X`

Source event document type:

- `localcast.audio.source_events`
- schema id: `gamecult.localcast.audio.source_events.v1`
- key: `localcast.audio.source-events.live`
- payload: MessagePack array decoded as `CultAudioSourceEvents`
- contents: dialogue anchor weights plus witness-dominant transient events with sample time, estimated room position, direction, energy, confidence, and kind.

Publisher smoke command:

```powershell
.\.venv\Scripts\python.exe .\scripts\stream_spatial_audio.py --input .\calibration\runs\audio-full-sync-20260518-165751\field-foa-ambix.wav --loop --duration 5 --smoke-readback
```

Source-event analysis command:

```powershell
.\.venv\Scripts\python.exe .\scripts\audio_field.py analyze-source-events --profile .\calibration\runs\audio-field-loud-profile.json --input .\calibration\runs\<run-folder>\field-cleaned.wav --cache .\calibration\runs\audio-events.msgpack
```

## Invariants

- Aquarium owns rendering and Faust DSP, not microphone capture or clock correction.
- LocalCastBridge publishes declared AmbiX blocks, not undecoded heterogeneous microphone channels.
- LocalCastBridge publishes source-event geometry separately from AmbiX audio; render effects do not have to infer transient positions from the sound bed.
- The audio frame carries `start_sample` and `audio_time_ns` so visual packets can align against the same bounded-latency timeline.
- OBS may receive a rendered monitor output later, but OBS is not the spatial bus authority.
