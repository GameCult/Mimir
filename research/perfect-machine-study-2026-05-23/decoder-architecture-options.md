# Decoder Architecture Options

## Purpose

The active decoder must eventually be small enough to run on a phone, robust
enough for meatspace microphones, and cheap enough not to eat a CPU core per
stream. This file separates the receiver shapes so the next cut can be made on
invariants instead of frustration.

## Current Runtime Shape

`MimirChirpBinTimeline.DecodeStreamWindow` currently does the whole job for a
bounded PCM window:

1. build an energy trace;
2. find candidate event windows;
3. classify each window with chirp-bin scoring;
4. preserve ambiguity candidates;
5. build code-valid anchors from the de Bruijn schedule;
6. search delay/bin-shift hypotheses;
7. fit an affine source clock;
8. emit calibration response/confusion evidence.

This is coherent as a proof machine. It is not yet the final streaming receiver.

## Option A: Streaming Scalar Receiver

### Shape

- Maintain a rolling PCM ring.
- Maintain an energy/proposal ring keyed by absolute sample index.
- When a proposal crosses threshold, score only the local candidate window.
- Keep the top K symbol hypotheses per proposal.
- Run a small codebook trellis over recent candidates.
- Update clock fit incrementally.

### Why It Fits

The emitter controls the symbol vocabulary and schedule. The receiver does not
need to search an unconstrained chirplet dictionary. It needs to notice likely
events and map them through a codebook.

### Risks

- Scalar scoring may still be too expensive at 192 kHz across many mics.
- Proposal thresholds can hide weak physical events if calibration is not used
  before rejection.
- Trellis state must preserve ambiguity, not collapse to one wrong symbol.

### Promote When

It matches the current window decoder on synthetic and artifact captures while
reducing repeated work by at least an order of magnitude.

## Option B: SIMD Dechirp + Goertzel Receiver

### Shape

- Precompute symbol reference chirps or dechirp oscillators.
- Process candidate windows in AVX2/FMA blocks.
- Accumulate real/imag Goertzel-style bin responses.
- Produce dense scores for the codebook trellis.

### Why It Fits

The hot math is mostly multiply-add and reduction over short windows. That is
exactly where SIMD is boring and useful. Boring and useful beats clever and
fragile here.

### Risks

- Small windows and few bins may be memory-bound or dispatch-bound rather than
  arithmetic-bound.
- Managed/native transition cost can erase wins if called per candidate instead
  of in batches.
- Alignment and denormal behavior need explicit treatment.

### Promote When

Profiling shows scalar C# scoring dominates, and a batched native scorer is
faster after interop overhead is included.

## Option C: Dechirp + FFT/CZT Receiver

### Shape

- Dechirp candidate windows against known slope.
- Use FFT or chirp-Z transform to estimate symbol bin/frequency offset.
- Feed bin likelihoods into codebook path selection.

### Why It Fits

CSS/LoRa receivers validate this family. It also handles frequency offset and
bin interpolation naturally when the symbol set is dense.

### Risks

- FFT setup and memory traffic can dominate for 32 bins and small windows.
- A full FFT is wasteful if only a small fixed bin bank is needed.
- CZT is attractive for narrowband zooming, but more complex than necessary
  until measured bin interpolation is the problem.

### Promote When

The codebook expands, frequency offset becomes first-order, or batched GPU/native
FFT plans beat SIMD Goertzel on actual candidate batches.

## Option D: Full Chirplet Transform / Fast Chirplet Transform

### Shape

- Search a chirplet parameter space over time, center frequency, duration,
  chirp rate, and scale.
- Use fast chirplet transform variants or maximum chirplet decomposition to
  locate arbitrary chirp-like atoms.

### Why It Fits

It is the canonical general tool when the emitted atoms are unknown or when we
are analyzing natural signals.

### Why It Does Not Fit The Hot Path Yet

Mimir controls the emitter. Searching a large affine dictionary to rediscover a
known alphabet is buying a forklift to move a teacup. Good forklift, wrong
room.

### Promote When

Only if the controlled-codebook receiver cannot survive physical paths even
after response weighting, group-delay correction, and codebook adaptation.

## Option E: Learned / Adaptive Receiver

### Shape

- Train or fit a compact classifier on calibration captures.
- Use learned symbol likelihood surfaces and delay priors.
- Keep codebook trellis deterministic after likelihood generation.

### Why It Fits

Physical paths produce systematic distortion: dead bands, aliases, reflections,
group delay, and mic-specific response. A learned likelihood surface may capture
what hand weighting misses.

### Risks

- A learned classifier can become an uninspectable compensator.
- Training data may overfit one room/gain setting.
- It must not replace the deterministic schedule map.

### Promote When

Calibration-weighted deterministic decoding fails in repeatable, explainable
ways that a compact trained likelihood model can resolve on held-out captures.

## Recommended Cut

Build Option A first, but design the scoring call as a replaceable batched
kernel so Option B or C can occupy it without changing ownership:

```text
PCM ring -> proposal ring -> batched score kernel -> ambiguity trellis -> clock fit
```

Only the score kernel should vary. The codebook, schedule, calibration model,
and clock fit remain the receiver's authority.

## Phone Receiver Shape

A phone should need:

- source codebook id;
- current schedule seed/epoch;
- sample rate estimate;
- rolling PCM buffer;
- compact path calibration if known.

It should not need:

- Starfire round trips for each event;
- Raven transport timing;
- Fensalir GPU state;
- OBS state.

The phone receiver emits:

- decoded anchor observations;
- fitted local clock to canonical time;
- confidence/residuals;
- response/confusion profile.

That profile can come home later. The phone does not need to be trusted as a
clock authority; it is a self-locating sensor.

