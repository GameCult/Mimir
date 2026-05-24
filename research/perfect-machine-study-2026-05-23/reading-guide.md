# Reading Guide

## Purpose

This directory is now large enough to need its own map. Use this guide when
entering a future implementation pass cold.

## If You Are Building The Active Decoder

Read in order:

1. [[chirplet-transform-deep-dive|Chirplet Transform Deep Dive]]
2. [[decoder-architecture-options|Decoder Architecture Options]]
3. [[microsecond-sync-math|Microsecond Sync Math]]
4. [[current-hotspot-audit|Current Hotspot Audit]]
5. [[optimization-ledger|Optimization Ledger]]
6. [[low-level-implementation-notes|Low-Level Implementation Notes]]
7. [[benchmark-plan|Benchmark Plan]]
6. sample sketches:
   - `samples/StreamingChirpBinDecoderSketch.cs`
   - `samples/BatchedChirpScoreAbiSketch.h`
   - `samples/CalibrationWeightedChirpLikelihoodSketch.cs`
   - `samples/Avx2DechirpGoertzelSketch.cpp`
   - `samples/ChirpBinScore.compute.hlsl`

Decision:

- implement streaming state first;
- preserve the existing window decoder as oracle;
- promote native/GPU scoring only after benchmarks.

## If You Are Building Meatspace Calibration

Read in order:

1. [[volumetric-audio-field|Volumetric Audio Field]]
2. [[acoustic-field-models|Acoustic Field Models]]
3. [[calibration-session-spec|Calibration Session Spec]]
4. [[prior-research-synthesis|Prior Research Synthesis]]
5. [[benchmark-plan|Benchmark Plan]]
6. [[references|References]]

Decision:

- learn usable bands per path;
- shape symbol likelihood before codebook solving;
- correct magnitude and phase/group delay;
- adapt the emitted alphabet rather than filtering failure afterward.

## If You Are Building The Audio Actuator

Read in order:

1. [[prior-research-synthesis|Prior Research Synthesis]]
2. [[optimization-ledger|Optimization Ledger]]
3. [[benchmark-plan|Benchmark Plan]]
4. sample sketches:
   - `samples/FarrowFractionalDelaySketch.cpp`
   - `samples/SroPllAsrcControllerSketch.cpp`

Decision:

- delay and SRO are separate loops;
- the runtime estimates state;
- Faust/native DSP moves samples.

## If You Are Building Camera Ingest

Read in order:

1. [[native-boundary-map|Native Boundary Map]]
2. [[visual-fusion-4dgs-study|Visual Fusion 4DGS Study]]
3. [[low-level-implementation-notes|Low-Level Implementation Notes]]
4. [[current-hotspot-audit|Current Hotspot Audit]]
5. [[benchmark-plan|Benchmark Plan]]
6. [[docs/native-capture-cadence|Native Capture Cadence]]
7. [[docs/native-rebuild-plan|Native Rebuild Plan]]

Decision:

- JSON probes are diagnostics;
- production capture is native workers plus payload handles;
- Fensalir owns GPU import and resource lifetime.

## If You Are Building Fensalir Integration

Read in order:

1. [[fensalir-integration-map|Fensalir Integration Map]]
2. [[native-boundary-map|Native Boundary Map]]
3. [[visual-fusion-4dgs-study|Visual Fusion 4DGS Study]]
4. sample sketches:
   - `samples/AquariumGpuSensorFrameBridgeSketch.cs`
   - `samples/AcousticConstraintLoweringSketch.cs`
   - `samples/TemporalEvidenceCandidateSketch.cs`

Decision:

- Mimir lowers observations/constraints;
- Fensalir owns temporal evidence and render state;
- the render tick must not run capture or analysis.

## If You Are Looking For The Next Cut

Read in order:

1. [[implementation-roadmap|Implementation Roadmap]]
2. [[option-matrix|Option Matrix]]
3. [[research-claims-digest|Research Claims Digest]]
4. [[questions-and-hypotheses|Questions And Hypotheses]]
5. [[distributed-receiver-spec|Distributed Receiver Spec]]
6. [[current-hotspot-audit|Current Hotspot Audit]]

Recommended next code cut:

- streaming active decoder state with calibration-weighted likelihood;
- benchmark it against the current window decoder;
- then wire the actuator.

## If You Are Updating Public Docs

Read:

1. [[docs/perfect-machine-domain-index|Perfect Machine Domain Index]]
2. [[docs/code-algorithm-map|Code Algorithm Map]]
3. [[notes/current-system-map|Current System Map]]
4. this directory's [[index|Study Index]]

Rule:

- public docs describe the live machine and current constraints;
- research docs may hold scars, rejected paths, and implementation options.
