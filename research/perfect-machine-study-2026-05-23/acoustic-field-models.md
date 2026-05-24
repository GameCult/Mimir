# Acoustic Field Models

## Purpose

Mimir's audio goal is not just "delay-align microphones." Delay alignment is
the entry fee. The larger target is a live volumetric audio field: where sound
is, how the room moves it, which mic/speaker paths are trustworthy, and how to
emit OBS-facing stems or a spatial bed that are better than raw source feeds.

## The Three Layers

### 1. Clock And Path Layer

Questions:

- Where is this sample on the canonical timeline?
- How much sample-rate offset is present?
- What delay, phase, and group-delay shape does this path impose?

Tools:

- active chirp-bin/codebook anchors;
- passive GCC-PHAT on program audio;
- loopback as emitter-side timing authority;
- Farrow/ASRC actuator.

Output:

- delay and SRO state;
- per-band magnitude and phase response;
- confidence and residuals.

This layer must work before any field reconstruction deserves trust.

### 2. Source/Geometry Layer

Questions:

- Which source positions explain the synchronized microphones?
- Which reflections are direct path versus room response?
- What evidence should become a stable field claim?

Tools:

- TDOA;
- SRP-PHAT;
- beamforming;
- known mic geometry;
- visual constraints from cameras/Leap when available.

Output:

- source position candidates;
- source confidence;
- reflection/room residuals;
- constraints for Fensalir's temporal evidence reservoir.

### 3. Field Reconstruction Layer

Questions:

- What pressure/energy field exists in the room?
- Can the field be represented as sources, ambisonics, equivalent sources, or a
  hybrid?
- What should OBS get: isolated stems, spatial bed, diagnostics, or all three?

Tools:

- ambisonics/spherical harmonics for compact field representation where geometry
  fits;
- equivalent source method / near-field acoustic holography when treating sound
  as sparse physical sources;
- compressed sensing or Bayesian sparse recovery when source count is low and
  mic count is limited;
- learned priors only after deterministic models fail cleanly.

Output:

- aligned stems;
- sound-source tracks;
- spatial bed/ambisonic representation;
- visualizable acoustic field constraints.

## Why Six Mics Are Not An Eigenmike

A commercial spherical array might have dozens of capsules in known geometry.
Mimir has an irregular consumer/pro-audio constellation: Scarlett mic, camera
mics, possibly phone/Raven mics, and loopback. That pushes the machine away from
pure textbook ambisonics and toward an evidence model:

- synchronize first;
- calibrate each path;
- infer source constraints;
- reconstruct only what the geometry supports.

If the geometry cannot support a high-order field, the honest output is lower
order or sparse-source estimates with uncertainty. Pretending otherwise would
be the audio equivalent of drawing a ruler on fog and calling it engineering.

## Candidate Models

### Ambisonic Fit

Use when:

- mic geometry can support a stable low-order spherical harmonic fit;
- the goal is a spatial bed, not precise source localization;
- the scene is diffuse or ambient.

Risk:

- irregular sparse mics produce unstable coefficients;
- near-field sources and room boundaries violate simple assumptions.

### Beamforming / SRP-PHAT

Use when:

- seeking source direction/position;
- synchronized mic geometry is known;
- direct-path energy exists.

Risk:

- reflections can produce plausible false peaks;
- small baseline limits low-frequency localization.

### Equivalent Source / Sparse Acoustic Holography

Use when:

- sources can be represented sparsely;
- a grid or source dictionary is acceptable;
- response calibration is available.

Risk:

- inverse problems are ill-conditioned;
- dense grids are expensive;
- sparse recovery can hallucinate when the measurement model is wrong.

### Hybrid Evidence Field

Use when:

- Mimir has mixed-quality sensors;
- visual evidence can constrain likely source regions;
- acoustic output should remain honest about uncertainty.

Shape:

- clock/path layer feeds calibrated observations;
- localization layer creates candidates;
- Fensalir temporal reservoir stabilizes candidates across time;
- DSP/output layer uses stable tracks as weights, not commandments.

This is the likely Mimir path.

## Practical First Milestone

Build one static acoustic source proof:

1. Record loopback and all live mics while emitting calibrated active witness.
2. Recover canonical anchors on loopback and at least one physical mic.
3. Estimate per-mic delay/SRO/response.
4. Use known speaker/mic geometry to form a TDOA/SRP-PHAT position hypothesis.
5. Lower that as an `AquariumAcousticConstraint`.
6. Display/log the constraint without letting it alter program audio yet.

The proof is deliberately modest. It forces the field machine to earn geometry
from timing instead of leaping straight into theatrical volumetric fog.

