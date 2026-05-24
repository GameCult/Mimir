# Architecture Rumination: Building Mimir Without Jenga

## Clock

- User correction received after the first documentation pass: spend the full
  dedicated pass mapping, researching, distilling, ruminating, and writing
  sample options.
- Active pass start recorded locally: `2026-05-23T23:41:19+01:00`.
- Corrected no-shortcut pass start recorded locally after the final instruction:
  `2026-05-23T23:54:42.9041588+01:00`. Do not treat this directory as complete
  until at least `2026-05-24T01:54:42.9041588+01:00`.

## Prime Cut

Mimir is not an app that "captures devices." That framing is too small and it
drags the architecture toward adapters, UI panels, and pile-of-feeds thinking.
Mimir is a realtime field reconstruction machine. Devices are evidence
producers. The rolling buffer is the measurement window. Synchronization is the
act that makes independent evidence comparable. Output is a negotiated product,
not a raw feed.

The architecture should be judged by whether each subsystem makes field
evidence more coherent:

- Can this block preserve timing?
- Can this block preserve identity?
- Can this block preserve enough raw information for a later better algorithm?
- Can this block be explained as "X owns Y so Z remains true"?

If not, it is probably scaffolding or residue.

## Current Machine

### Runtime

`MimirRuntime` is a Fensalir runtime with an intentionally empty scene. It owns
application cadence, audio witness emission, telemetry, and UI readouts. It
does not own driver details. It does not own DSP. It should eventually own less
of the calibration renderer too: witness generation can stay controlled by the
runtime, but sample production and hot-path synthesis should be native/DSP once
the shape is settled.

### Hub

`MimirSynchronizationHub` is the correct authority for current runtime belief.
It owns source registration, buffer append, analyzer scheduling, report cache,
and state smoothing. The strongest invariant here is that UI/telemetry must not
invoke analysis. That separation prevents accidental workload explosions.

### Buffers

`MimirRollingStreamBuffer` is simple and right: one stream identity, one bounded
queue, one edge. It is not the final native memory layout, but the ownership
model is sound. The native reservoir should be the lower version of the same
idea: one edge, typed views, no per-kind retention.

### Active Audio Decoder

`MimirChirpBinTimeline` is the live active sync machine. It is conceptually
right because the emitter and receiver share the same codebook and schedule.
The current code still has scalar and full-window pressure, but the shape is no
longer the wrong dense chirplet wall. It is a controlled receiver:

- proposal;
- dechirp;
- bin score;
- ambiguity preservation;
- code-constrained path selection;
- clock fit;
- calibration evidence.

The next coherent cut is not "add more rejection." It is streaming state and
calibration-weighted decode that changes the likelihood surface before path
selection.

### Passive Audio Decoder

`MimirPassiveAudioSynchronizationEstimator` is useful but should be demoted in
the mental model. It is a passive delay/drift witness, not canonical time. It
can suppress active watermark when confidence is stable. It should not invent
timeline anchors.

### Native ASIO

`native/asio_capture` is a correct direction: COM and ASIO callbacks stay in
native code, C# reads bounded channel blocks. This pattern should repeat for
camera drivers.

### Native Probes

The probes are research instruments. They should keep telling us what hardware
does, but their stdout/JSON shape must not leak into production.

## The Perfect Machine Shape

### Audio Spine

1. Native ASIO capture writes input and loopback channels into native audio
   blocks with device sample counters.
2. Mimir runtime indexes those blocks as stream samples.
3. Active/passive analyzers update per-source delay/SRO/response state.
4. Faust/native DSP consumes the same blocks plus state and emits aligned stems.
5. OBS receives stems/spatial bed; it never sees unsynchronized raw mics as the
   truth.

### Visual Spine

1. Native camera drivers capture frames with device or immediate receipt time.
2. Runtime indexes handles into rolling buffers/reservoir.
3. Fensalir GPU extracts features/flow/depth/surface/material claims.
4. Reservoir preserves rolling visual evidence by time.
5. Fensalir emits program video via Spout2.

### Distributed Spine

Raven and phones should decode local time from codebook/schedule state. They
should report:

- local anchor observations;
- confidence;
- response surface;
- fitted clock;
- transport timestamp only as metadata.

They should not ask Starfire to bless every sample over the network. Network
latency is exactly what the codebook is meant to escape.

## Things The Current Code Gets Right

- The app/runtime split is clean.
- The UI is not the analyzer.
- Stream identity is explicit.
- Direct ASIO is in process and sample-bearing.
- Calibration model includes magnitude, confusion, phase, delay, and adaptive
  codebook planning.
- Runtime emission can consume the calibration plan.
- Native reservoir already encodes the single-edge invariant.

## Things Still Too Soft

- Active decoder still rescans windows rather than owning a streaming state.
- Physical acoustic decode is not yet robust enough.
- The actuator does not exist.
- Direct camera runtime drivers do not exist.
- Native reservoir is not wired into the C#/Fensalir/Faust flow.
- Calibration model is loaded, but not yet proven as the decisive fix for
  meatspace.
- Raven/phone receiver shape is only conceptual.

## Candidate Cuts

### Cut 1: Streaming Chirp-Bin Decoder

Owns:

- rolling PCM window;
- energy proposal ring;
- candidate frame cache;
- anchor trellis;
- clock fit state.

Reason:

- Avoid full-window rescans every analysis tick.

### Cut 2: Calibration-Weighted Likelihood

Owns:

- path-specific symbol likelihood from response model;
- phase/group-delay correction before code selection;
- global delay/bin-shift hypothesis scoring.

Reason:

- Meatspace failure is not merely noisy timing. It is path-shaped symbol
  distortion.

### Cut 3: Native DSP Actuator

Owns:

- fractional delay;
- resampler;
- SRO control;
- phase/group-delay correction;
- stem alignment.

Reason:

- The runtime estimates state. DSP moves samples.

### Cut 4: Direct Camera Driver Worker

Owns:

- device open/configure;
- async read queue;
- timestamp preservation;
- native payload handle.

Reason:

- JSON/stdout cannot be allowed near six-camera hot loops.

## Rejected Temptations

- More mode flags in analyzer: the problem is evidence ownership, not a switch
  shortage.
- More outlier rejection before calibration weighting: that hides the response
  surface instead of learning it.
- Routing all sources through process adapters because it is easy to debug:
  that is how bandwidth dies in a formal suit.
- Treating 192 kHz as magic: it improves timing granularity and ultrasonic
  options only where transducers and path response actually preserve signal.
