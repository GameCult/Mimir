# Sample Implementation Sketches

These are not production files. They are indexed sketches for future cuts,
written to make implementation options concrete without sneaking half-built
machinery into the app.

## Files

- `StreamingChirpBinDecoderSketch.cs`: C# state model for an incremental
  chirp-bin decoder that avoids full-window rescans.
- `Avx2DechirpGoertzelSketch.cpp`: AVX2/FMA-shaped CPU scoring kernel sketch.
- `BatchedChirpScoreAbiSketch.h`: native scoring ABI shape for batching
  candidate/bin work across the managed/native boundary.
- `ChirpBinScore.compute.hlsl`: D3D12 compute shader shape for batched
  candidate/bin scoring.
- `FarrowFractionalDelaySketch.cpp`: native fractional delay actuator sketch.
- `SpscAudioBlockRingSketch.cpp`: native single-producer/single-consumer audio
  block ring sketch for capture worker handoff.
- `CalibrationWeightedChirpLikelihoodSketch.cs`: shows how response/confusion/
  phase/group-delay calibration should shape symbol likelihood before codebook
  path selection.
- `SroPllAsrcControllerSketch.cpp`: control-rate delay/SRO loop for driving an
  ASRC or fractional-delay actuator.
- `GaussianSplatClaimUpdate.compute.hlsl`: minimal Fensalir compute shape for
  turning synchronized visual observations into live splat claims.
- `AquariumGpuSensorFrameBridgeSketch.cs`: contract sketch for lowering runtime
  video observations into Fensalir GPU sensor frames.
- `AcousticConstraintLoweringSketch.cs`: contract sketch for lowering decoded
  chirp/passive anchors into Fensalir acoustic constraints.
- `TemporalEvidenceCandidateSketch.cs`: candidate lowering sketch that keeps
  stable track ownership in Fensalir.
- `SrpPhatGridSketch.cpp`: minimal TDOA/SRP-PHAT-style grid scoring proof for a
  synchronized mic constellation.

## Rule

Promote one of these only after a benchmark or integration test proves it owns a
real invariant. Sample code is a microscope, not a new wall in the house.
