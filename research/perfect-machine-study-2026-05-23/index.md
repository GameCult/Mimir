# 2026-05-23 Perfect Machine Study Index

## Purpose

This directory is the deeper study pass that sits beside the source map. Use it
when the next implementation pass needs more than "where is the file?" and
needs the reason a cut exists.

## Documents

| File | Use It For |
| --- | --- |
| `architecture-rumination.md` | System ownership, current machine critique, coherent next cuts. |
| `optimization-ledger.md` | Hot loop risks, low-level implementation options, micro-optimization candidates. |
| `references.md` | Research links for chirplets, CSS/LoRa, GCC-PHAT, D3D12/FFT/GPU compute. |
| `current-hotspot-audit.md` | Static audit of likely runtime allocation/scan/driver-pressure hotspots. |
| `implementation-roadmap.md` | Phase plan, invariants, proofs, and benchmark harnesses. |
| `questions-and-hypotheses.md` | Open questions and falsifiable hypotheses by domain. |
| `reading-guide.md` | Task-oriented route through the study docs and samples. |
| `data-dictionary.md` | Data object definitions for streams, samples, chirp anchors, clock fits, calibration paths, reports, and Fensalir constraints. |
| `volumetric-audio-field.md` | Sound-field reconstruction decomposition and first practical audio target. |
| `acoustic-field-models.md` | Field reconstruction models and the first honest acoustic source proof. |
| `chirplet-transform-deep-dive.md` | Controlled chirp receiver versus generic chirplet transform. |
| `decoder-architecture-options.md` | Active receiver implementation options and promotion criteria. |
| `distributed-receiver-spec.md` | Raven/phone receiver shape for self-locating remote sensors using codebook and schedule state. |
| `visual-fusion-4dgs-study.md` | Sensor fusion and realtime Gaussian splatting architecture study. |
| `open-implementation-catalog.md` | Implementation families to study before inventing local machinery. |
| `prior-research-synthesis.md` | Distillation of the older chirplet, adaptive sync, ambisonic, splatting, calibration, and visual archives. |
| `research-claims-digest.md` | Claim-level digest of external literature and how it steers Mimir. |
| `fensalir-integration-map.md` | Contract-level cut between Mimir runtime truth and Fensalir GPU/render ownership. |
| `native-boundary-map.md` | Native/managed/Fensalir ownership boundaries for ASIO, cameras, reservoir, and GPU payloads. |
| `option-matrix.md` | Implementation alternatives, failure modes, and deciding proofs for the major subsystems. |
| `benchmark-plan.md` | Measurement plan for audio sync, acoustic field, camera capture, Fensalir lowering, and splat fusion. |
| `calibration-session-spec.md` | Command/session shape for learning and validating real output/mic path models. |
| `low-level-implementation-notes.md` | Concrete memory layout, SIMD/GPU, ASIO, camera, and instrumentation notes. |
| `microsecond-sync-math.md` | Timing math for sample period, bandwidth, SNR, subsample delay, and acoustic distance. |
| `state-machine-invariants.md` | Ownership limits for runtime, hub, buffers, decoders, calibration, DSP, capture, Fensalir, network, and OBS. |
| `failure-mode-ledger.md` | Failure modes and coherent fixes across active witness, passive sync, actuator, camera, fusion, and network paths. |
| `hot-path-pseudocode.md` | Code-shaped intended hot paths for streaming decode, calibration weighting, actuator, native rings, camera rings, and Fensalir lowering. |
| `glossary.md` | Short definitions for the synchronization, acoustic, visual, and reservoir terms used across the study. |
| `sample-code-index.json` | Machine-readable index for sample sketches. |
| `samples/README.md` | Index of sample implementation sketches. |

## Sample Code

| Sample | Future Production Target | Question It Answers |
| --- | --- | --- |
| `samples/StreamingChirpBinDecoderSketch.cs` | `MimirChirpBinStreamingDecoder` or equivalent runtime class | What state removes full-window rescans? |
| `samples/Avx2DechirpGoertzelSketch.cpp` | Native CPU scorer for chirp-bin candidates | What does SIMD dechirp/bin scoring look like? |
| `samples/BatchedChirpScoreAbiSketch.h` | Native scorer ABI | What batch boundary avoids per-candidate managed/native overhead? |
| `samples/ChirpBinScore.compute.hlsl` | Fensalir/D3D12 compute scorer | How would batched candidate/bin scoring map to GPU? |
| `samples/FarrowFractionalDelaySketch.cpp` | Faust/native DSP actuator | What is the smallest fractional delay proof? |
| `samples/SpscAudioBlockRingSketch.cpp` | Native capture worker handoff | What shape should low-jitter audio block transfer take? |
| `samples/CalibrationWeightedChirpLikelihoodSketch.cs` | Physical-path active decoder | Where does calibration enter symbol likelihood? |
| `samples/SroPllAsrcControllerSketch.cpp` | DSP actuator control | How does delay/SRO state become a resampler command? |
| `samples/GaussianSplatClaimUpdate.compute.hlsl` | Fensalir visual fusion | What is the smallest live splat-claim compute shape? |
| `samples/AquariumGpuSensorFrameBridgeSketch.cs` | Mimir-to-Fensalir visual lowering | How should runtime video observations become Fensalir sensor frames? |
| `samples/AcousticConstraintLoweringSketch.cs` | Mimir-to-Fensalir acoustic lowering | How should decoded anchors/geometry become acoustic constraints? |
| `samples/TemporalEvidenceCandidateSketch.cs` | Shared temporal evidence candidates | How can Mimir lower candidates without owning Fensalir tracks? |
| `samples/SrpPhatGridSketch.cpp` | Acoustic source localization | What is the smallest TDOA/SRP-PHAT-style grid scoring proof? |

## Best Next Implementation Sequence

1. Build a streaming chirp-bin decoder state object beside the current analyzer,
   but keep the old analyzer as proof oracle until outputs match.
2. Load a real physical mic calibration model and compare weighted versus
   unweighted symbol likelihood on stored Scarlett captures.
3. Emit reduced reliable-bin codebook from the physical model and measure
   meatspace anchor rate.
4. Add native/Faust actuator proof using logged `MimirAudioSynchronizationState`
   before touching live program output.
5. Port one direct camera path into `IMimirVideoCaptureDriverSource`; Leap first
   if timing/depth value wins, PS3 Eye first if raw driver throughput is the
   faster proof.

## Index Terms

- active sync
- ASIO
- AVX2
- chirp-bin
- chirplet
- codebook adaptation
- dechirp
- D3D12 compute
- Farrow fractional delay
- Faust actuator
- GCC-PHAT
- group delay
- LoRa CSS
- native reservoir
- Fensalir
- Gaussian splatting
- passive sync
- Raven
- response/confusion matrix
- sample-rate offset
- SPSC ring
- Starfire
- streaming decoder
- volumetric audio
- volumetric visual fusion
