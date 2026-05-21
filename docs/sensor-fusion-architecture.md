# Sensor Fusion Architecture

Visual fusion belongs in Aquarium over current Mimir runtime buffers.

## Live Flow

```mermaid
flowchart TD
    A["direct camera drivers"] --> B["Mimir.Runtime video buffers"]
    C["Leap stereo IR driver"] --> B
    B --> D["native reservoir handles"]
    D --> E["Aquarium GPU feature extraction"]
    E --> F["cross-view matching + flow"]
    F --> G["surface/material claims"]
    G --> H["brush/splat render budget"]
    H --> I["Spout2/program video"]
```

## Invariants

- Device timestamps beat arrival timestamps when available.
- Leap is the first timing-camera candidate.
- Process capture is a bridge edge, not the local six-camera foundation.
- Aquarium owns GPU extraction, fusion, material fitting, and render budgeting.
- Runtime buffers own retention and stream health, not scene reconstruction.

## Next Cut

Implement one concrete direct capture driver, feed `MimirVideoFrameDescriptor`
samples into `MimirVideoCaptureDriverSource`, and measure sustained cadence over
the default five-second window.
