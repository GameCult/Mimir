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
| `samples/README.md` | Index of sample implementation sketches. |

## Sample Code

| Sample | Future Production Target | Question It Answers |
| --- | --- | --- |
| `samples/StreamingChirpBinDecoderSketch.cs` | `MimirChirpBinStreamingDecoder` or equivalent runtime class | What state removes full-window rescans? |
| `samples/Avx2DechirpGoertzelSketch.cpp` | Native CPU scorer for chirp-bin candidates | What does SIMD dechirp/bin scoring look like? |
| `samples/ChirpBinScore.compute.hlsl` | Fensalir/D3D12 compute scorer | How would batched candidate/bin scoring map to GPU? |
| `samples/FarrowFractionalDelaySketch.cpp` | Faust/native DSP actuator | What is the smallest fractional delay proof? |
| `samples/SpscAudioBlockRingSketch.cpp` | Native capture worker handoff | What shape should low-jitter audio block transfer take? |

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
- passive sync
- Raven
- response/confusion matrix
- sample-rate offset
- SPSC ring
- Starfire
- streaming decoder
- volumetric audio
- volumetric visual fusion

