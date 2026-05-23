# Sample Implementation Sketches

These are not production files. They are indexed sketches for future cuts,
written to make implementation options concrete without sneaking half-built
machinery into the app.

## Files

- `StreamingChirpBinDecoderSketch.cs`: C# state model for an incremental
  chirp-bin decoder that avoids full-window rescans.
- `Avx2DechirpGoertzelSketch.cpp`: AVX2/FMA-shaped CPU scoring kernel sketch.
- `ChirpBinScore.compute.hlsl`: D3D12 compute shader shape for batched
  candidate/bin scoring.
- `FarrowFractionalDelaySketch.cpp`: native fractional delay actuator sketch.
- `SpscAudioBlockRingSketch.cpp`: native single-producer/single-consumer audio
  block ring sketch for capture worker handoff.

## Rule

Promote one of these only after a benchmark or integration test proves it owns a
real invariant. Sample code is a microscope, not a new wall in the house.

