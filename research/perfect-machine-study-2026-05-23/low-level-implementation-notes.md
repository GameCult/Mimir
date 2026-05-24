# Low-Level Implementation Notes

## Purpose

This file collects the implementation details that are easy to lose when the
conversation stays at the architecture level. None of these notes are promises;
they are candidates to benchmark.

## Audio Memory Layout

### Preferred Analysis Layout

For decoder kernels:

```text
channel0 samples: [s0 s1 s2 ...]
channel1 samples: [s0 s1 s2 ...]
...
```

Separate channel spans make per-channel scoring straightforward and avoid
striding through interleaved audio during analysis. Interleaving is useful for
device I/O and some DSP kernels; it is not automatically the right analysis
shape.

### Preferred Scoring Layout

```text
candidateStartSamples[]
candidateEnergy[]
symbolReliability[]
binFrequencies[]
binSin[]
binCos[]
```

Dense arrays let SIMD/native/GPU kernels process candidates without object
chasing. The codebook trellis can stay managed/CPU because it is small and
branchy.

## SIMD Chirp-Bin Scoring

Likely CPU kernel shape:

1. load candidate window samples in 8-float AVX2 chunks;
2. multiply by dechirp oscillator/window;
3. accumulate bin real/imag sums;
4. compute magnitude or normalized energy;
5. write top K or full dense bin vector.

Important:

- keep oscillator tables aligned;
- keep candidate windows contiguous;
- batch multiple candidates per interop call;
- avoid denormals;
- normalize outside the deepest loop where possible.

## FFT / Goertzel Decision

Use Goertzel/fixed-bin DFT when:

- symbol count is small;
- bins are known;
- candidate count is modest;
- native dispatch overhead matters.

Use FFT/CZT when:

- many bins are needed;
- frequency offset interpolation dominates;
- candidate batches are large enough;
- GPU/native FFT plans can be reused.

Do not choose FFT because it sounds more canonical. Canonical is not a profiler.

## GPU Chirp Scoring

The GPU scoring pass only makes sense if data is already resident or batches are
large enough to pay dispatch overhead.

Good GPU candidates:

- many mic streams;
- many remote receiver windows;
- wide candidate sets during acquisition;
- offline/artifact calibration sweeps.

Bad GPU candidates:

- one or two channels;
- tiny candidate counts;
- paths that require CPU readback before codebook solving every event.

## ASIO Capture Hot Path

Current observed risks:

- per-block `std::vector<float>` resize;
- `std::deque` queue;
- native-to-managed polling copies into `float[]`;
- managed byte array allocation for sample payload;
- `ConcurrentQueue.Count` pressure.

Preferred cut:

- native SPSC ring;
- fixed block pool;
- monotonic frame counters;
- block descriptors with handle/index;
- C# metadata only until DSP needs samples.

## Camera Capture Hot Path

Current probe strengths:

- KS async queueing is already close to the driver;
- PS3 Eye raw path proves high fps;
- JSON frame events make cadence visible.

Current probe risks:

- JSON must not become production payload;
- CPU frame buffers are diagnostic until Fensalir import/upload path exists;
- one-off command-line knobs must become configuration/stateful driver policy.

Preferred cut:

- one native capture worker per camera family;
- fixed frame ring;
- descriptor ABI;
- optional shared GPU texture handle;
- Fensalir owns texture import and GPU staging.

## Fensalir GPU Fusion

Fensalir contracts already distinguish:

- `GpuSensorFrame` for calibrated camera inputs and external textures;
- `AcousticFieldFrame` for timing/room constraints;
- `CalibrationEventFrame` for high-confidence event anchors;
- `GpuFusionField` for point buffers/seeds;
- `TemporalGaussianField` for stable gaussian observations.

Mimir should produce these rows. It should not pack shader structs directly.

## Gaussian Splat Rendering Levers

From modern 3DGS implementations:

- tile size matters;
- sorting strategy matters;
- cull before sort;
- packed buffers matter;
- anti-aliasing and opacity/radius thresholds matter;
- view-consistency fixes cost real time;
- depth/feature render modes are useful for diagnostics.

Mimir-specific cut:

- first update live claims;
- then render simple claims;
- only later optimize dynamic radiance fields.

## Benchmark Counters To Add

Decoder:

- samples consumed;
- proposals created;
- candidates scored;
- anchors accepted/rejected;
- elapsed scoring time;
- allocations per analysis tick.

ASIO:

- callback count;
- block count;
- queue/ring high-water mark;
- dropped blocks;
- conversion time if measurable.

Camera:

- frame interval p50/p95/p99;
- async queue depth;
- dropped/late frames;
- payload copy time;
- upload/import time.

Fensalir:

- contract lowering time;
- upload time;
- GPU pass timings;
- temporal field count;
- acoustic constraint count.

