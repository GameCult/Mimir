# Sonar DSP Notes For Recursive Bioacoustic Refinement

## Question

Can Mimir use iterative refinement in the decoder by transforming a sampled song
contour back toward the emitted chirp, resampling, and repeating until phase is
locked?

Short answer: yes, but not as plain waveform correlation. The failed local
experiment rejected the naive objective function, not recursive refinement.

## Sources Checked

- liquid-dsp: open-source C DSP library for SDR-style filters, transforms,
  synchronizers, modulators, demodulators, and frame processing.
  <https://github.com/jgaeddert/liquid-dsp>
- liquid-dsp release notes: `qdsync` detects frames and estimates/corrects
  carrier frequency, carrier phase, and timing offset before callback delivery.
  <https://github.com/jgaeddert/liquid-dsp/releases>
- Underwater acoustic modem paper using liquid-dsp: packet starts are found
  with chirp matched filters; later flexframe receivers use PN synchronization,
  equalization, PLL, AGC, and acquisition/tracking loop bandwidth changes.
  <https://signet.dei.unipd.it/wp-content/uploads/2022/02/Oceans2022_ac_modem-2.pdf>
- pyAPRiL passive radar: explicit reference/surveillance processing,
  clutter-cancellation families, frequency-domain/batched cross-correlation,
  CFAR, and range-Doppler processing.
  <https://github.com/pyapril/pyapril>
- GNU Radio correlate access code / stream tags: synchronization emits tags at
  precise stream positions so downstream blocks process the same sample truth.
  <https://wiki.gnuradio.org/index.php/Correlate_Access_Code_-_Tag_Stream>
  <https://wiki.gnuradio.org/index.php/Stream_Tags>
- OpenTDS / time-delay spectrometry: swept source plus tracking band-pass can
  isolate direct sound from lower-frequency delayed reflections, then derive
  magnitude and phase response.
  <https://syarusinsky.github.io/OpenTDS/>
- Signalsmith DSP: high-quality fractional delay/resampling primitives include
  Lagrange, polyphase, and Kaiser-sinc interpolation with measured delay error.
  <https://signalsmith-audio.co.uk/code/dsp/>
- Open Echo: open-source sonar stack captures raw echo data and emphasizes
  transducer/receiver filtering as part of the measurement system.
  <https://github.com/Neumi/open_echo>
- ahoi acoustic modem docs: receiver front-end has preamp plus band-pass stage
  adapted to the used frequency band before DSP sees samples.
  <https://www3.tuhh.de/acps/projects/ahoi/hardware_overview/>

## What The Failed Mimir Pass Got Wrong

The reverted recursive pass treated the best waveform correlation after
fractional resampling as the truth. In a reflective room, that is not a timing
estimator. It is a reflection selector.

Observed failure:

- stored cardioid improved from 7.576 us to 5.141 us;
- stored shotgun worsened from 6.578 us to 10.236 us;
- fresh cardioid locked to a later reflection lobe at 65.666 us instead of the
  prior 18.930 us.

This means the correct recursion variable is not "delay that maximizes raw
correlation." It is a channel hypothesis:

```text
direct-path delay
+ sample-rate offset / clock drift
+ frequency-dependent phase/group delay
+ complex path gain per contour/formant band
+ sparse reflection taps with later arrival and lower authority
```

The direct path owns sync. Reflections are channel evidence, not alternate
timelines.

## Better Shape

```text
rolling ASIO window
-> analytic / complex bandbank or short chirp matched-filter bank
-> per-anchor complex response: magnitude, phase, local slope, confidence
-> ambiguity candidates over delay and small frequency/clock offsets
-> direct-path hypothesis tracker
-> sparse multipath residual model
-> recursive refinement:
   1. predict direct-path anchor phases from current hypothesis
   2. dechirp / derotate each contour into baseband
   3. score phase slope and group-delay residuals, not only magnitude
   4. update delay/SRO/group-delay with bounded loop bandwidth
   5. explain later energy as reflection taps
   6. repeat until the direct-path innovation is small
-> output timing anchor, response model, confidence, and residual reflection map
```

This is closer to sonar/acoustic modem practice:

- matched filters acquire the preamble or contour;
- PLL/timing loops refine phase and sample timing;
- equalizers/channel estimators explain multipath;
- CFAR/thresholding prevents noise-floor peaks from becoming detections;
- stream tags carry exact sample-position decisions forward.

## Concrete Mimir Implementation Cut

### 1. Replace Raw Waveform Scoring With Complex Matched Filters

For each shaped contour anchor, build a complex reference template:

```text
template[n] = envelope[n] * exp(j * phase[n])
```

Maintain streaming I/Q accumulators per anchor family:

```text
z = sum window[n] * conj(template[n])
energy = sum abs(window[n])^2
score = abs(z)^2 / energy
phase = atan2(imag(z), real(z))
```

The magnitude says "this contour exists." The phase and phase slope say "the
direct-path hypothesis is early/late."

### 2. Estimate Delay From Phase Slope

Across frequency-separated anchors:

```text
phase(f) ~= phase0 - 2*pi*f*delay + group_delay_error(f)
```

Fit delay from the wrapped phase slope after unwrapping over the usable
anchor set. Weight by learned path response. This is the missing bridge between
"subsample interpolation" and physically meaningful phase lock.

### 3. Track, Do Not Reacquire Every Window

Use two loop bandwidths:

- acquisition: broader ambiguity search, more tolerance, accepts less precise
  anchors;
- tracking: narrow predicted window around the existing path, rejects peaks
  that require impossible acceleration, clock drift, or propagation jumps.

This is how recursion becomes stable: each pass refines a live channel state
instead of discovering a new room every frame.

### 4. Model Multipath Explicitly

After subtracting the predicted direct-path complex template, keep a small
sparse list of later residual taps:

```text
tap = delay_after_direct, complex_gain_by_band, decay/confidence
```

Those taps are useful for room mapping and frequency-response normalization, but
they cannot vote the direct-path delay backward or forward unless the direct
path is absent.

### 5. Use Real Fractional Delay Tools

Linear interpolation is not good enough for the last mile. The actuator and
decoder should use at least Lagrange or polyphase/Kaiser-sinc fractional delay
when comparing/resampling high-rate anchors. Signalsmith's public DSP library is
a useful reference for delay-line quality gates.

## What To Build Next

1. A `MimirComplexContourMatchedFilter` over the canary packet anchors.
2. A per-mic `MimirAcousticPathTracker` with:
   - direct delay;
   - SRO ppm;
   - phase intercept;
   - per-band complex gain;
   - sparse reflection taps.
3. A calibration replay command that runs both stored ASIO receipts and reports:
   - direct delay MAE;
   - phase-slope residual;
   - reflection-tap energy;
   - usable anchor bands;
   - convergence time;
   - realtime factor.
4. Only then reintroduce recursive refinement as a bounded tracker update.

## Current Belief

Iterative refinement is absolutely worth pursuing. The correct version is a
complex matched-filter / phase-slope / channel-tracker machine, not a raw
waveform-correlation machine. "Nanosecond" remains a dangerous word in air with
consumer-ish acoustic paths, but the route toward lower microseconds is real:
make phase/group delay first-class and stop letting reflections masquerade as
time.
