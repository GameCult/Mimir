# Reading Guide

## Purpose

This directory is now large enough to need its own map. Use this guide when
entering a future implementation pass cold.

## If You Are Building The Active Decoder

Read in order:

1. `chirplet-transform-deep-dive.md`
2. `decoder-architecture-options.md`
3. `microsecond-sync-math.md`
4. `current-hotspot-audit.md`
5. `optimization-ledger.md`
6. `low-level-implementation-notes.md`
7. `benchmark-plan.md`
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

1. `volumetric-audio-field.md`
2. `acoustic-field-models.md`
3. `calibration-session-spec.md`
4. `prior-research-synthesis.md`
5. `benchmark-plan.md`
6. `references.md`

Decision:

- learn usable bands per path;
- shape symbol likelihood before codebook solving;
- correct magnitude and phase/group delay;
- adapt the emitted alphabet rather than filtering failure afterward.

## If You Are Building The Audio Actuator

Read in order:

1. `prior-research-synthesis.md`
2. `optimization-ledger.md`
3. `benchmark-plan.md`
4. sample sketches:
   - `samples/FarrowFractionalDelaySketch.cpp`
   - `samples/SroPllAsrcControllerSketch.cpp`

Decision:

- delay and SRO are separate loops;
- the runtime estimates state;
- Faust/native DSP moves samples.

## If You Are Building Camera Ingest

Read in order:

1. `native-boundary-map.md`
2. `visual-fusion-4dgs-study.md`
3. `low-level-implementation-notes.md`
4. `current-hotspot-audit.md`
5. `benchmark-plan.md`
6. `docs/native-capture-cadence.md`
7. `docs/native-rebuild-plan.md`

Decision:

- JSON probes are diagnostics;
- production capture is native workers plus payload handles;
- Fensalir owns GPU import and resource lifetime.

## If You Are Building Fensalir Integration

Read in order:

1. `fensalir-integration-map.md`
2. `native-boundary-map.md`
3. `visual-fusion-4dgs-study.md`
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

1. `implementation-roadmap.md`
2. `option-matrix.md`
3. `research-claims-digest.md`
4. `questions-and-hypotheses.md`
5. `distributed-receiver-spec.md`
6. `current-hotspot-audit.md`

Recommended next code cut:

- streaming active decoder state with calibration-weighted likelihood;
- benchmark it against the current window decoder;
- then wire the actuator.

## If You Are Updating Public Docs

Read:

1. `docs/perfect-machine-domain-index.md`
2. `docs/code-algorithm-map.md`
3. `notes/current-system-map.md`
4. this directory's `index.md`

Rule:

- public docs describe the live machine and current constraints;
- research docs may hold scars, rejected paths, and implementation options.
