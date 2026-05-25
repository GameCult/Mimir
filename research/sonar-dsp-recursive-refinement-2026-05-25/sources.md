# Source Ledger: Recursive Sonar DSP Refinement

This ledger preserves the source trail behind
`../sonar-dsp-recursive-refinement-2026-05-25.md`. It is not a literature review
for sport. Each source is here because it changes the next Mimir decoder cut.

Local mirrors live under `mirrors/` so the next implementation pass has stable
context even if a page moves, throttles, or gets cute.

## Source Index

| ID | Source | Local mirror | Primary lesson for Mimir |
| --- | --- | --- | --- |
| S1 | liquid-dsp README | `mirrors/liquid-dsp-readme.rst` | Use lightweight complex DSP primitives, synchronizers, filters, NCOs, and modem-style framing as the reference shape for a portable hot path. |
| S2 | liquid-dsp release notes | `mirrors/liquid-dsp-releases.html` | `qdsync`-style synchronization estimates and corrects timing, carrier frequency, and phase as one acquisition/tracking problem. |
| S3 | Underwater acoustic modem using liquid-dsp | `mirrors/oceans2022-acoustic-modem-liquid-dsp.pdf` | Acoustic packet receivers use chirp matched filters for packet starts, then PN synchronization, equalization, PLL, AGC, and changing loop bandwidths for acquisition versus tracking. |
| S4 | pyAPRiL passive radar | `mirrors/pyapril-readme.md` | Reference/surveillance processing, clutter cancellation, overlap-save correlation, CFAR, and range-Doppler thinking map cleanly onto loopback/mic acoustic paths. |
| S5 | GNU Radio correlate access code | `mirrors/gnuradio-correlate-access-code-tag-stream.html` | Synchronization should emit stream-position facts/tags, not just side-channel estimates. |
| S6 | GNU Radio stream tags | `mirrors/gnuradio-stream-tags.html` | Time decisions must travel with sample streams as isosynchronous metadata. This matches Mimir's rolling-buffer anchor model. |
| S7 | OpenTDS / time-delay spectrometry | `mirrors/opentds.html` | Swept tracking filters can isolate direct sound from later reflections and then derive amplitude/phase response. |
| S8 | Signalsmith DSP | `mirrors/signalsmith-dsp.html` | Fractional delay quality is measurable; use Lagrange/polyphase/Kaiser-sinc references instead of linear interpolation for final fitting/actuation. |
| S9 | Open Echo | `mirrors/open-echo-readme.md` | Sonar systems treat transducer drive, receive filtering, raw echo capture, and visualization as one measurement stack. |
| S10 | ahoi acoustic modem hardware overview | `mirrors/ahoi-hardware-overview.html` | Front-end analog filtering and gain are part of the receiver; usable acoustic bands must be measured per mic/path before DSP pretends all bins are equal. |

## Distilled Notes

### S1: liquid-dsp README

Upstream: <https://github.com/jgaeddert/liquid-dsp>

liquid-dsp is a C library meant for SDR-style DSP on embedded platforms. It
provides filters, filter design, oscillators/NCOs, modems, synchronizers, FFTs,
complex math, and framing pieces without forcing a large framework. It also
ships extensive tests and benchmarks.

Mimir implication:

- The hot decoder should be structured as small streaming DSP objects, not as
  whole-window analysis scripts.
- Complex baseband is the natural internal representation for phase-aware
  contour matching.
- The decoder should expose benchmarkable primitives: filterbank, matched
  filter, synchronizer, path tracker, actuator.

### S2: liquid-dsp Release Notes

Upstream: <https://github.com/jgaeddert/liquid-dsp/releases>

The relevant signal is `qdsync`: the release notes describe a synchronizer that
detects frames and estimates/corrects carrier frequency, carrier phase, and
timing offset before returning clean data to the user. That is the missing shape
in Mimir's failed recursion pass.

Mimir implication:

- Do not split detection, phase, delay, and drift into unrelated little patches.
- The recursive state should be a synchronizer object with acquisition and
  tracking modes.
- Timing offset, SRO, phase intercept, and group delay are one coupled state
  surface.

### S3: Underwater Acoustic Modem With liquid-dsp

Upstream:
<https://signet.dei.unipd.it/wp-content/uploads/2022/02/Oceans2022_ac_modem-2.pdf>

The modem paper uses chirp preambles, matched filtering to find packet starts,
and later liquid-dsp flexible framing with PN synchronization, equalization,
PLL, AGC, and acquisition-mode loop bandwidths. It also explicitly notes that
chirps can support Doppler and channel impulse-response estimation.

Mimir implication:

- Canary/birdcall packets should expose a preamble-like matched-filter surface,
  even if they sound musical.
- The contour word is not only an identifier; it is a channel probe.
- Recursive refinement should update channel impulse-response/state, not only
  delay.

### S4: pyAPRiL Passive Radar

Upstream: <https://github.com/pyapril/pyapril>

pyAPRiL separates reference and surveillance channels, then provides clutter
cancellation, time-domain and frequency-domain cross-correlation, batched
overlap-save correlation, Doppler windowing, CFAR, beamforming, and performance
metrics.

Mimir implication:

- Scarlett loopback is the reference channel; mics are surveillance channels.
- Reflections and music leakage are clutter, not random noise.
- The path tracker should use clutter/residual modeling and CFAR-like detection
  thresholds so strong reflection lobes stop impersonating direct-path timing.

### S5: GNU Radio Correlate Access Code

Upstream:
<https://wiki.gnuradio.org/index.php/Correlate_Access_Code_-_Tag_Stream>

The block scans a stream for a chosen access code and emits a tagged stream when
the access code is found. Its documentation also warns that cyclic access codes
produce false detections and bad alignment.

Mimir implication:

- Song contours need low-autocorrelation / non-cyclic identity features.
- Decoder hits should become timestamped anchors in the stream, not global
  mutable estimates.
- False-lock behavior is an architecture problem, not a threshold annoyance.

### S6: GNU Radio Stream Tags

Upstream: <https://wiki.gnuradio.org/index.php/Stream_Tags>

GNU Radio stream tags are metadata attached to exact sample positions and
propagated along the same stream as the samples.

Mimir implication:

- Mimir anchors should be sample-position metadata inside the rolling buffer.
- UI/debug state reads these tags; it should not recalculate timing.
- Networked Raven/phone decoders should emit the same typed anchor facts so mesh
  state preserves timing authority.

### S7: OpenTDS / Time-Delay Spectrometry

Upstream: <https://syarusinsky.github.io/OpenTDS/>

Time-delay spectrometry uses a swept source and tracking band-pass behavior to
separate direct sound from delayed reflections, then measures magnitude and
phase response.

Mimir implication:

- The room-path problem is directly analogous: direct arrival must be isolated
  before response normalization is trusted.
- Frequency response normalization needs phase/group delay, not only magnitude.
- Our birdcall contours should be shaped so direct-path time/frequency evidence
  survives before later reflections dominate.

### S8: Signalsmith DSP

Upstream: <https://signalsmith-audio.co.uk/code/dsp/>

Signalsmith's open DSP library includes delay tools, Lagrange/polyphase/Kaiser-
sinc interpolators, envelopes, FFTs, and multi-channel STFT. Its docs discuss
measured delay-line error versus bandwidth.

Mimir implication:

- Linear interpolation is not credible for the last microseconds.
- Decoder refinement and Faust/native actuation need measured fractional-delay
  quality gates.
- Oversampled or band-limited interpolation should be part of the timing kernel,
  not an afterthought.

### S9: Open Echo

Upstream: <https://github.com/Neumi/open_echo>

Open Echo is an open sonar controller stack. It drives ultrasonic transducers,
captures raw echo data, filters signals, and surfaces raw waterfalls / TCP data
for tooling.

Mimir implication:

- Treat emission, analog/acoustic path, capture, raw receipts, and decoder as
  one calibration system.
- Persist raw captures and derived receipts together.
- The UI should expose raw evidence and path state, not only final delay.

### S10: ahoi Acoustic Modem Hardware Overview

Upstream: <https://www3.tuhh.de/acps/projects/ahoi/hardware_overview/>

The ahoi modem receiver chain includes hydrophone matching, pre-amplification,
software-switchable gain, and band-pass filtering adapted to the intended
frequency band.

Mimir implication:

- Per-path usable frequency bands are physical facts, not decoder preferences.
- Calibration must learn what each speaker/mic path can actually hear.
- The matched-filter bank should be weighted by measured response and confusion
  surfaces.

## Implementation Synthesis

The next recursive decoder should be:

```text
loopback reference + mic window
-> complex contour matched-filter bank
-> candidate access/word anchors with stream positions
-> direct-path channel tracker
   - delay
   - SRO
   - phase intercept
   - phase/group-delay curve
   - per-band complex gain
-> sparse later reflection taps
-> bounded recursive update
-> typed rolling-buffer anchors + calibration receipt
```

The important correction is ownership: recursion refines the direct-path channel
state. It does not choose whichever delayed waveform lobe has the biggest
correlation this frame.
