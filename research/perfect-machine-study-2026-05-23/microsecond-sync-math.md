# Microsecond Sync Math

## Purpose

The project target is microsecond-class synchronization. This note keeps the
math visible so we do not confuse sample-rate settings, transducer bandwidth,
and actual timing certainty.

## Sample Periods

| Sample Rate | Sample Period |
| --- | --- |
| 48 kHz | 20.833 us |
| 96 kHz | 10.417 us |
| 192 kHz | 5.208 us |

Subsample estimation is mandatory for microsecond delay estimates at every
practical audio sample rate. 192 kHz helps by reducing the interpolation gap and
allowing higher witness frequencies, but it does not magically provide
one-microsecond truth.

## What Enables Subsample Timing

- known emitted waveform;
- high SNR;
- broad enough bandwidth;
- stable phase response;
- clean enough direct path;
- coherent clock model over multiple anchors;
- interpolation or model fitting around correlation/likelihood peaks.

The active witness helps because its shape is known. Passive music can help when
it has enough broadband content, but it is not guaranteed.

## Cramer-Rao Intuition

Delay estimation improves with:

- signal bandwidth;
- SNR;
- observation duration / repeated independent evidence.

It degrades with:

- narrowband signals;
- reverberation;
- unknown filtering;
- low SNR;
- nonlinear processing;
- clock drift;
- false symbol aliases.

This is why the chirp signal has a dual role: it spreads energy across time and
frequency for timing, while also measuring which parts of the path survived.

## 192 kHz Is Useful When

- the interface can capture output and mics in one stable clock domain;
- the monitors/mics preserve enough high-frequency energy;
- phase/group delay can be calibrated;
- the actuator can use fractional delay/SRO information;
- the extra CPU/memory load stays within budget.

## 192 kHz Is Not Useful When

- the acoustic path low-passes the witness below the useful band;
- the mic/driver applies hidden processing;
- the decoder only reports integer samples;
- the system still lacks a resampling/fractional-delay actuator;
- the bottleneck is symbol confusion rather than sample quantization.

## Timing Evidence Hierarchy

1. Electrical loopback: best for emitted timeline and interface clock.
2. Physical mic active witness: best for acoustic path timing/response.
3. Passive program audio: useful continuous drift evidence when content allows.
4. Network timestamps: metadata, not truth.

## Delay To Distance

At roughly 343 m/s:

- 1 us is about 0.343 mm of acoustic path length;
- 10 us is about 3.43 mm;
- 100 us is about 34.3 mm.

That scale is useful and humbling. A room reflection or mic position error can
swamp the number we are trying to measure. The machine must keep uncertainty
visible.

## Practical Acceptance

For the next physical proof, "microsecond sync" should mean:

- loopback pair recovers below printed microsecond precision;
- physical mic reports fractional-sample delay with stable residuals;
- repeated anchors show bounded SRO drift;
- delay uncertainty is reported, not hidden;
- actuator demonstrably reduces residual misalignment.

Without the actuator, the system is measuring microseconds, not delivering them.

