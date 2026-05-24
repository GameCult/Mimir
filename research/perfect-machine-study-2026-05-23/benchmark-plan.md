# Benchmark Plan

## Purpose

This is the measurement spine for future implementation passes. It exists to
keep the machine honest: no hot-path change earns production status because it
sounds clever. It earns it by moving measured latency, jitter, throughput,
allocation, cache pressure, confidence, or reconstruction quality in the right
direction.

## Global Rules

- Measure wall-clock throughput and canonical timing error separately.
- Keep synthetic, artifact, and live-device runs distinct.
- Always record sample rate, buffer size, device clock, channel mapping, and
  mode (`passive`, `chirp-only`, `hybrid`).
- Allocation-free claims require runtime allocation counters, not code vibes.
- A passing self-test is a unit proof, not a physical proof.
- A physical proof without artifact retention is only a campfire story.

## Audio Synchronization Benchmarks

### Synthetic Chirp-Bin Decoder

Goal: prove canonical-time recovery without acoustic path damage.

Inputs:

- generated chirp-bin timeline;
- known integer delay;
- known fractional delay;
- optional sample-rate offset;
- optional colored noise and band dropouts.

Metrics:

- decoded anchors per second;
- false anchors per second;
- delay error in microseconds;
- fractional sample residual;
- SRO estimate error in ppm;
- allocations per second;
- CPU cycles or elapsed time per decoded second.

Current command family:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --standalone-chirp-bin-self-test --sample-rate 192000 --delay-samples 96000
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --chirp-only-sync-self-test --sample-rate 192000
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --hybrid-sync-self-test --sample-rate 192000
```

Missing benchmark:

- a loop over SNR, reliable-bin count, fractional delay, and SRO;
- output JSON/CSV artifact;
- CI-friendly threshold for the synthetic path.

### Scarlett Loopback Artifact

Goal: prove the analyzer survives the real ASIO clock and converter path when
the signal remains electrical/loopback-clean.

Inputs:

- rendered mono float chirp-bin witness;
- ASIO output through Scarlett;
- ASIO capture of loopback channels;
- optional persisted calibration profile.

Metrics:

- loopback channel-to-channel delay;
- confidence;
- anchor count;
- response matrix diagonal strength;
- per-band phase/group-delay residual;
- callback block jitter;
- dropped/late ASIO blocks.

Current command family:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --render-chirp-bin-f32 --sample-rate 192000 --seconds 6 --output .\artifacts\chirp-bin.f32
.\native\probes\asio_audio_cadence\build\Release\asio_audio_cadence.exe --play-f32-mono .\artifacts\chirp-bin.f32 --record-f32-interleaved .\artifacts\scarlett.f32 --sample-rate 192000 --seconds 6
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --analyze-asio-f32 .\artifacts\scarlett.f32 --sample-rate 192000
```

Missing benchmark:

- stable artifact directory naming;
- automatic device/channel manifest capture;
- repeated run variance.

### Meatspace Mic Path

Goal: prove acoustic propagation, monitor response, mic response, room
reflections, and gain staging still permit deterministic code-valid anchors.

Inputs:

- Scarlett output through monitors;
- Scarlett mic and camera mics;
- calibration profile generated from the same room/output/mic path;
- optional passive music bed.

Metrics:

- usable bands per mic;
- confusion matrix entropy;
- strongest alias bins;
- group-delay slope by bin;
- decoded anchors per second;
- rejected anchors and why;
- global delay/bin-shift hypothesis likelihood;
- time-to-lock and lock-hold duration;
- SRO drift estimate stability.

Acceptance:

- a calibrated physical mic should produce stable canonical anchors within the
  five-second rolling buffer;
- delay should be reported in microseconds with residuals low enough for a
  fractional-delay actuator to matter;
- dead bins must be excluded or downweighted by the emitted codebook, not merely
  filtered after failure.

## Audio Field Benchmarks

### Passive Program-Audio Alignment

Goal: use loopback music as continuous timing evidence when active chirps are
unnecessary.

Metrics:

- PHAT peak sharpness;
- delay confidence over time;
- failure rate on low-frequency-only material;
- behavior during silence;
- agreement with active chirp anchors.

Cut line:

- passive alignment is advisory unless it agrees with active/codebook anchors or
  repeated state earns confidence.

### Acoustic Source Localization

Goal: turn synchronized mics into spatial estimates rather than just aligned
waveforms.

Inputs:

- known source positions if available;
- synthetic room model for baseline;
- live mic geometry.

Metrics:

- TDOA residual;
- SRP-PHAT peak width;
- source position error;
- update rate;
- ambiguity count.

First proof:

- one static speaker position recovered from loopback/chirp evidence and mic
  geometry.

## Video Capture Benchmarks

### Native Camera Cadence

Goal: prove each camera's raw driver path maintains sustained cadence without
framework buffering lies.

Metrics:

- delivered fps after warm-up;
- frame interval p50/p95/p99;
- missing sequence count;
- device timestamp monotonicity;
- CPU time in callback;
- transfer size and USB topology.

Known baselines:

- Leap full-height stereo IR: roughly 110 fps when the USB neighborhood is not
  polluted by the Focusrite.
- PS3 Eyes: roughly 187 fps at 320x240 and roughly 58-60 fps at 640x480.
- Kiyo Pro: currently about 25 fps despite 60 fps advertisement and HighSpeed
  negotiation.
- Kiyo basic: about 30 fps in current usable modes.

### Multi-Camera Contention

Goal: discover whether frame loss is USB topology, driver scheduling, CPU copy,
or runtime ingest pressure.

Metrics:

- per-device fps with all sources open;
- callback jitter correlation across devices;
- host controller/root hub mapping;
- frame-size bandwidth estimate versus observed cadence;
- runtime buffer enqueue cost.

First implementation proof:

- all local cameras feed `Mimir.Runtime` descriptors in one app run with no
  JSON process bridge.

## Fensalir Lowering Benchmarks

Goal: prove Mimir can create Fensalir contract frames every render tick without
turning the app into a garbage factory.

Metrics:

- lowering time per frame;
- managed allocations per frame;
- number of sensor descriptors lowered;
- D3D12 upload/import wait time once connected;
- stale observation count.

Acceptance:

- lowering is pure mapping over current buffer snapshots;
- analysis/capture does not run from the render tick;
- UI reads cached state only.

## Gaussian Splat / Sensor Fusion Benchmarks

Goal: decide which visual fusion representation deserves production attention.

Metrics:

- observations accepted per second;
- splat claims updated per second;
- reprojection error;
- track lifetime;
- sort/tile/render cost;
- memory footprint per claim;
- drift under camera motion.

Candidate approaches:

- sparse feature claims lowered into Fensalir temporal evidence reservoir;
- live Gaussian claims updated from synchronized camera features;
- hybrid point/splat field where Mimir supplies timing and Fensalir owns
  rendering/reuse.

## Reporting Format

Every serious benchmark should write one manifest next to artifacts:

```json
{
  "machine": "Starfire",
  "timestampUtc": "2026-05-24T00:00:00Z",
  "mode": "chirp-only",
  "sampleRate": 192000,
  "bufferFrames": 192,
  "sources": [],
  "artifacts": [],
  "metrics": {},
  "notes": []
}
```

The manifest is not the hot path. It is the lab notebook.

