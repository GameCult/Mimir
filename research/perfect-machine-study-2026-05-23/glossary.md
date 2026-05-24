# Glossary

This glossary is deliberately blunt. It is here so the next implementation pass
does not burn half an hour rediscovering which word owns which invariant.

## Active Witness

A signal Mimir deliberately emits so receivers can recover canonical time and
path response. In this repo the active witness is the chirp-bin/chirplet
timeline. It is not only a sync beep; it is also a calibration probe.

## ASIO

Low-latency pro-audio driver API. In Mimir, ASIO is the production path for
Scarlett loopback and mic capture because it can put output loopback and inputs
in the same driver clock domain at 192 kHz.

## Canonical Timeline

The emitter-owned time axis. A receiver that knows the codebook and schedule
should infer where its captured samples sit on this timeline.

## Chirp Spread Spectrum

A communications family that encodes symbols in chirps. LoRa/CSS demodulators
matter to Mimir because they show a mature version of dechirp, FFT/bin scoring,
timing/frequency-offset handling, and low-power receiver design.

## Chirplet

A localized chirp atom: time-limited, frequency-localized, and slope-bearing.
The generic chirplet transform searches a broad chirplet parameter space. Mimir
uses a constrained chirplet receiver because the emitter controls the codebook.

## Codebook

The set of allowed emitted symbols and their shapes. In Mimir this includes
frequency bin, glide direction/slope, duration, and rhythmic spacing. It should
adapt to the measured physical path by avoiding dead or ambiguous bins.

## Confusion Matrix

For each expected symbol, the observed energy distribution across possible
symbols/bins. This is how Mimir learns that a mic hears symbol 12 as symbol 10,
or does not hear a band at all.

## De Bruijn Sequence

A sequence where every length-N symbol tuple appears once in a cycle. Mimir uses
the idea so a small number of consecutive chirp events can identify a unique
timeline position.

## Dechirp

Mix a received chirp against the conjugate/reference chirp so its energy
collapses toward a tone/bin. This turns chirp classification into a cheap
frequency-bin measurement.

## Farrow Filter

A fractional-delay filter structure where coefficients are polynomial functions
of the desired fractional sample delay. Useful for continuous delay actuation
without rebuilding FIR kernels at audio rate.

## Fensalir

The engine repo. It owns windowing, rendering, D3D12, render graph execution,
GPU fusion packets, temporal Gaussian/evidence fields, debug UI, and eventual
program surface publication.

## Frequency Response

How a speaker/room/mic path changes signal magnitude by frequency. Mimir should
learn it from the active witness and use it to normalize both calibration and
program audio.

## GCC-PHAT

Generalized cross-correlation with phase transform weighting. A common robust
delay estimator. It is useful for passive program-audio alignment and TDOA, but
it is not a substitute for controlled codebook anchors.

## Gaussian Splat

A 3D/4D ellipsoidal radiance primitive. For Mimir, splats are a plausible
runtime field representation for fused camera evidence, but Fensalir should own
the GPU field and renderer.

## Group Delay

Frequency-dependent delay through a path. If high bands arrive shifted relative
to low bands, symbol timing and response normalization both suffer. The active
witness should estimate and correct this.

## Hybrid Mode

Use passive program-audio timing by default, emit active witness only when
confidence falls. In Mimir this should eventually be a shaped watermark, not a
burst of rude test tones.

## Loopback

Audio captured from the output path. Scarlett loopback is the current timing
authority because it observes what Mimir attempted to emit before acoustic room
damage.

## Near-Field Acoustic Holography

A family of techniques for reconstructing sound fields from microphone array
measurements. It matters because Mimir is not only aligning mics; it is trying
to infer a room-scale sound field from sparse sensors.

## Passive Mode

No active emitted witness. Mimir estimates delay from program audio such as
music/game sound. Useful when the content is rich; fragile during silence or
band-limited material.

## PLL

Phase-locked loop. In Mimir, a control-rate PLL can turn repeated delay
observations into delay and sample-rate-offset corrections for the audio
actuator.

## Reservoir

A bounded evidence store. The Mimir rolling buffer owns recent raw stream
history; Fensalir's temporal evidence reservoir owns stable resolved visual and
acoustic field claims after lowering.

## Sample-Rate Offset

Clock drift between devices, usually described in ppm. Delay alone aligns one
moment; SRO correction keeps streams aligned over time.

## SPSC Ring

Single-producer/single-consumer ring buffer. The natural shape for hot native
capture callbacks handing blocks to one runtime consumer without locks.

## SRP-PHAT

Steered response power with PHAT weighting. It searches source positions by
testing which spatial hypothesis best explains inter-mic phase/delay evidence.

## TDOA

Time difference of arrival. With known microphone positions, TDOA constraints
help localize sound sources.

## Temporal Gaussian Field

Fensalir's GPU-side field representation for temporally coherent splat-like
evidence. Mimir should feed it observations and constraints, not own its
internal rendering state.

## Watermark

An active witness shaped to be unobtrusive under program audio. The best Mimir
watermark still carries deterministic canonical-time identity and response
calibration energy.

