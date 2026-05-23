# Optimization Ledger

## What This File Is

This is the low-level implementation ledger for Mimir's most likely hot paths.
It is not a benchmark result. It is a map of what to test, why it might matter,
and where the current code is likely to pay unnecessary cost.

## Hot Path 1: Chirp-Bin Decode

### Current Shape

- Build an energy trace across a window.
- Classify local candidate windows.
- Dechirp and score each bin.
- Run code-valid anchor selection.
- Fit clock and refine offset.

### Bottleneck Risk

The current scalar/classic C# version is correct enough to reason about, but it
still encourages repeated full-window work. Six or more audio streams at 192 kHz
make that expensive quickly.

### Preferred Evolution

1. Streaming energy proposal ring.
2. Precomputed chirp kernels by sample rate and symbol plan.
3. SIMD dechirp/bin score on CPU for small channel counts.
4. Batched GPU compute for many candidate windows or many remote receivers.
5. Keep the codebook trellis on CPU unless profiling proves otherwise; it is
   branchy and small.

### Micro-Optimization Ideas

- Structure-of-arrays for sine/cosine kernels.
- Process bins in groups of 8 floats on AVX2/FMA.
- Keep hot candidate windows contiguous and aligned.
- Avoid allocating candidate arrays inside every analysis tick.
- Cache symbol reliability weights in dense arrays.
- Precompute expected event sample offsets for the active window.
- Use a ring of candidate frames keyed by absolute sample index.

### Sample References

- `samples/StreamingChirpBinDecoderSketch.cs`
- `samples/Avx2DechirpGoertzelSketch.cpp`
- `samples/ChirpBinScore.compute.hlsl`

## Hot Path 2: Passive GCC-PHAT

### Current Shape

- Allocate complex arrays per estimate.
- FFT reference/candidate.
- Normalize cross-spectrum.
- Inverse FFT.
- Search lags.

### Bottleneck Risk

Passive sync is cheaper than dense chirplet matching, but repeated allocations
and full FFTs per source pair can still dominate if called too often.

### Preferred Evolution

- Reuse FFT buffers/plans.
- Keep reference spectrum cached for the current analysis edge.
- Batch candidate transforms.
- Move to native FFTW/KissFFT/MKL/cuFFT only after measured C# FFT cost matters.
- Use passive only as confidence/drift support, not as mandatory every-tick work.

### Micro-Optimization Ideas

- Window into preallocated `Complex` spans.
- Precompute Hann window.
- Preemphasis in one pass with mean removal.
- Limit lag search by physical/network horizon.
- Use parabolic interpolation only around plausible peaks.

## Hot Path 3: Fractional Delay And SRO Actuator

### Current Shape

- Not built.

### Preferred Evolution

- Prototype native/Faust fractional delay using Farrow/Lagrange for small
  sub-sample corrections.
- Add a higher quality polyphase sinc path for program output.
- Drive both from smoothed delay/SRO state.
- Keep per-source state in DSP, not in UI/runtime strings.

### Micro-Optimization Ideas

- Fixed filter order for predictable SIMD.
- Interleave channels only where the DSP kernel wants it.
- Separate control-rate state update from audio-rate sample processing.
- Use denormal guards.

### Sample Reference

- `samples/FarrowFractionalDelaySketch.cpp`

## Hot Path 4: Native Rolling Buffers

### Current Shape

- C# rolling buffers store sample envelopes.
- Rust reservoir stores one shared-edge native rolling buffer with typed views.

### Bottleneck Risk

The C# buffer shape is fine for current proof state, but payload-heavy audio and
video should move to native memory handles. Copies and per-sample allocations
will become visible as source count rises.

### Preferred Evolution

- Native SPSC rings per capture worker feeding a shared reservoir index.
- Payload handles point to native/audio/GPU memory owned by capture/DSP/Fensalir.
- Runtime stores metadata and current belief.

### Micro-Optimization Ideas

- Power-of-two ring capacities.
- Single writer per capture device.
- Cache-line padded head/tail counters.
- Batch publish blocks.
- Avoid sharing mutable payload ownership across subsystems.

### Sample Reference

- `samples/SpscAudioBlockRingSketch.cpp`

## Hot Path 5: Camera Capture And GPU Fusion

### Current Shape

- Native probes prove direct driver access and cadence.
- Runtime direct driver seam exists.
- Fensalir fusion not wired yet.

### Bottleneck Risk

Six cameras make CPU copies and process bridges fail. Leap/PS3/Kiyo sources
need direct capture, stable timestamps, and GPU-friendly payloads.

### Preferred Evolution

- Direct KS/libusb/vendor driver workers.
- Native payload handles into runtime/reservoir.
- Fensalir consumes current window and uploads/processes on GPU.
- Use D3D12 shared resources where possible.

### Micro-Optimization Ideas

- Queue multiple async reads per camera.
- Keep camera buffers pinned/native.
- Avoid decode unless the algorithm needs decoded pixels.
- Do per-camera feature extraction in compute, then fuse compact features.

## External References

- LoRa/CSS receivers repeatedly validate dechirp plus FFT/bin scoring as the
  natural controlled-chirp demodulator shape.
- HLSL Shader Model 6 wave intrinsics are relevant for reductions and FFT-like
  kernels inside D3D12 compute.
- cuFFT callbacks show a general trick: combine preprocessing with transform
  load/store to avoid extra memory bandwidth.
- FFTW wisdom/alignment notes matter if we move passive or chirp-bin batches to
  native FFT plans.

