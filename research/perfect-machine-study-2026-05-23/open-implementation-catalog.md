# Open Implementation Catalog

This catalog lists implementation families worth stealing shape from. Do not
vendor code casually. The point is to understand mature cuts.

## Chirp / CSS / LoRa

What to look for:

- dechirp implementation;
- symbol timing synchronization;
- FFT/bin selection;
- CFO/bin-shift correction;
- preamble/sync word handling;
- soft decision metrics.

Candidate references:

- OpenLoRa demodulator work.
- SDR LoRa implementations.
- GNU Radio LoRa/CSS blocks.
- Audio "data over sound" projects for acoustic channel constraints.

Mimir translation:

- Preamble becomes deterministic timeline/codebook schedule.
- CFO becomes acoustic bin shift / sample-rate mismatch.
- Channel response becomes output/mic path calibration.

## Audio Delay / Beamforming

What to look for:

- GCC-PHAT implementations;
- SRP-PHAT acceleration;
- microphone array calibration;
- fractional delay filters;
- asynchronous sample-rate conversion.

Candidate references:

- Pyroomacoustics for algorithm shape.
- WebRTC audio processing for pragmatic realtime constraints.
- JACK/PipeWire/ASIO style callback boundaries.
- Faust examples for DSP graph ownership.

Mimir translation:

- Keep algorithm shape, not necessarily library dependency.
- Faust/native DSP is the actuator home.

## Gaussian Splatting

What to look for:

- 3D Gaussian Splatting CUDA rasterizer.
- Dynamic/4D Gaussian splatting repos.
- Differentiable Gaussian rasterization data layouts.
- Real-time viewer/update pipelines.

Mimir translation:

- Fensalir owns render/update.
- Native reservoir owns time-indexed claims.
- Direct camera drivers feed evidence; do not train/reconstruct from disk
  batches in the live path.

## Native Capture

What to look for:

- ASIO callback isolation.
- Kernel Streaming async read queues.
- libusb/WinUSB transfer queues.
- D3D12 shared texture/resource handoff.
- lock-free SPSC rings.

Mimir translation:

- One capture worker owns one device.
- Payload handles cross subsystem boundaries.
- Runtime indexes metadata and timing.

## SIMD/GPU Compute

What to look for:

- AVX2/FMA complex dot products.
- batched FFT plan reuse.
- HLSL wave reductions.
- CUDA/cuFFT callback fusion.
- cache-aware ring buffers.

Mimir translation:

- SIMD first for fixed small bin banks.
- GPU when candidate/bin/channel batches are large enough to amortize dispatch
  and memory movement.

