# Implementation Roadmap From Study

## Goal

Turn the study into implementation passes that improve coherence instead of
adding another ring of machinery around the current code.

## Phase 1: Make Active Decode Streaming

Objective:

- Stop rescanning full active windows every analysis tick.

Current mechanism:

- Analyzer extracts mono windows and calls `MimirChirpBinTimeline.DecodeStreamWindow`.
- Decode rebuilds proposals/frames/anchors from the provided span.

Intended mechanism:

- A per-source active decoder owns a rolling PCM window, energy proposal ring,
  candidate-frame cache, anchor trellis tail, and clock fit state.
- Analyzer asks the decoder for the current snapshot.

Invariants:

- Same accepted reports as current analyzer on synthetic and loopback captures.
- No UI/telemetry re-entry.
- Calibration weighting remains optional until physical proof is run.

Cut line:

- If streaming state becomes harder to explain than the current decode, cut it
  back to proposal/frame caching only.

Proof:

- Synthetic chirp-only/hybrid tests still recover known delay.
- Stored loopback ASIO artifact still decodes 0 us.
- Allocation count per analysis tick drops.

## Phase 2: Physical Calibration-Weighted Decode

Objective:

- Make meatspace chirp-bin decode use what calibration already learned.

Current mechanism:

- Calibration model can be loaded.
- Symbol weights, phase weights, group-delay correction, and bin-shift
  hypotheses exist but need physical proof.

Intended mechanism:

- Symbol likelihood is shaped by reliability/confusion/phase before candidate
  pruning.
- Global delay/bin-shift hypotheses score the whole observation window.
- Reduced reliable-bin codebook can be emitted and decoded.

Invariants:

- The codebook remains bijective over the intended horizon.
- Dead bands lose authority.
- Failed timing still emits calibration evidence.

Proof:

- Compare unweighted versus weighted decode on stored hot-mic/physical captures.
- Emit reliable-bin plan and measure anchor rate through the real monitor/mic
  path.

## Phase 3: Actuator Proof

Objective:

- Move from "we know the delay" to "samples are aligned."

Current mechanism:

- `MimirAudioSynchronizationState` estimates smoothed delay and SRO.

Intended mechanism:

- Native/Faust DSP consumes delay/SRO state.
- Fractional delay line corrects sub-sample offset.
- ASRC/PLL corrects drift.

Invariants:

- Runtime estimates; DSP moves samples.
- Actuator state is inspectable but not owned by UI.
- Output remains separately stemmable for OBS.

Proof:

- Offline artifact: apply actuator to delayed candidate and reduce residual.
- Live loopback/mic: show residual delay converges and remains bounded.

## Phase 4: Direct Camera Driver Runtime Path

Objective:

- Replace diagnostic frame-event probe transport for local cameras.

Current mechanism:

- KS/PS3 probes can emit JSON frame events.
- Runtime has `IMimirVideoCaptureDriver` and source adapter seam.

Intended mechanism:

- One native/direct driver worker per camera family.
- Capture worker preserves timestamp and payload handle.
- Runtime indexes sample envelope.
- Fensalir consumes handle/window for GPU feature extraction.

Invariants:

- No local camera pixels through stdout/base64.
- Device/arrival timestamp survives the boundary.
- Capture worker owns device pressure, runtime owns identity and buffer.

Proof:

- Leap or PS3 Eye frames enter runtime buffers with sustained measured cadence.
- Fensalir UI can inspect buffer health without touching pixel hot path.

## Phase 5: Native Reservoir/Fensalir/Faust Integration

Objective:

- Make the native reservoir the shared lower memory/timing authority.

Current mechanism:

- C# buffers own runtime proof state.
- Rust reservoir exists with C ABI and tests.

Intended mechanism:

- Capture workers push handles into native reservoir.
- Runtime and Fensalir read typed views.
- Faust reads audio block handles and alignment state.

Invariants:

- One edge owns retention.
- Typed views do not own separate windows.
- Payload ownership stays with producer/consumer domain.

Proof:

- Runtime status matches C# buffer counts for a mirrored proof.
- Fensalir renders a simple current-window visual packet from reservoir claims.

## Benchmark Harnesses Needed

1. `chirp-bin-decode-bench`: synthetic and artifact decode timing/allocation.
2. `passive-sync-bench`: FFT allocation/reuse comparison.
3. `asio-callback-pressure-bench`: native queue/block pool comparison.
4. `camera-driver-buffer-bench`: direct driver payload handoff cadence.
5. `actuator-residual-bench`: before/after residual delay and drift.

## Decision Gates

- SIMD scorer only after decode timing is the measured bottleneck.
- GPU scorer only after candidate/bin batches exceed CPU SIMD advantage.
- Neural/acoustic-field models only after classical timing/calibration/source
  localization is coherent.
- Dynamic 4DGS only after direct synchronized visual evidence exists in
  Fensalir.

