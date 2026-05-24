# Data Dictionary

## Purpose

This dictionary names the data objects that keep recurring across code, docs,
samples, and planned commands.

## Stream Descriptor

Identity for one source stream.

Fields:

- source id;
- kind (`audio`, `video`, etc.);
- origin (`local`, `network`, `process`, `native`);
- human label;
- optional device/config metadata.

Invariant:

- one descriptor maps to one rolling buffer.

## Stream Sample

One runtime sample envelope.

Fields:

- descriptor/source id;
- timestamp;
- sequence;
- audio block descriptor or video frame descriptor;
- optional payload bytes/handle.

Invariant:

- samples are indexed by runtime buffers; they are not long-term history.

## Audio Block Descriptor

Metadata for a block of audio.

Fields:

- sample rate;
- channel count;
- sample format;
- frame count;
- timestamp;
- optional native handle.

Invariant:

- production payload should move toward native handles, not managed byte arrays.

## Video Frame Descriptor

Metadata for one camera frame.

Fields:

- width;
- height;
- pixel format;
- stride;
- device timestamp;
- byte length;
- optional native/GPU handle.

Invariant:

- frame descriptor must preserve enough information for Fensalir to import or
  upload without guessing.

## Chirp Symbol

One emitted active-witness event.

Fields:

- symbol id;
- frequency bin;
- chirp slope/window/duration;
- expected event time;
- optional rhythmic/gap evidence.

Invariant:

- symbol shape is controlled by the emitter and known by the receiver.

## Chirp Anchor

A decoded observation that maps a local sample offset to a canonical event.

Fields:

- event index;
- symbol id;
- local sample offset;
- canonical time;
- confidence;
- residual/error;
- bin shift hypothesis.

Invariant:

- anchors are the bridge between local sample time and canonical time.

## Clock Fit

Affine map between local samples and canonical time.

Fields:

- source offset samples;
- effective sample rate;
- confidence;
- anchor count;
- mean absolute error.

Invariant:

- clock fit is evidence state, not a sample-moving actuator.

## Calibration Path

Per-output/mic model.

Fields:

- output path id;
- source/mic id;
- sample rate;
- usable bands;
- symbol calibrations;
- confusion observations;
- delay hypotheses;
- phase/group-delay summaries;
- codebook plan.

Invariant:

- calibration alters likelihood and emission planning before decode failure
  happens.

## Sync Report

Analyzer output for one source relative to reference.

Fields:

- source id;
- delay samples;
- delay milliseconds/microseconds;
- fractional delay;
- confidence;
- band response;
- decode trace.

Invariant:

- report describes measurement; DSP decides how to move audio.

## Sync State

Smoothed runtime belief.

Fields:

- latest delay;
- smoothed delay;
- confidence;
- delay slope / SRO ppm;
- per-band evidence.

Invariant:

- UI reads this; analyzers write it; render tick does not recompute it.

## Acoustic Constraint

Fensalir-facing spatial/timing evidence.

Fields:

- constraint id;
- kind;
- position/velocity/radius;
- confidence;
- timestamp;
- timing uncertainty.

Invariant:

- raw PCM stays out of Fensalir contract frames.

## Visual Observation

Fensalir-facing camera evidence.

Fields:

- source id;
- native/shared texture handle or payload handle;
- pixel format;
- dimensions;
- calibration id;
- canonical timestamp.

Invariant:

- Fensalir owns GPU lifetime after import/lowering.

