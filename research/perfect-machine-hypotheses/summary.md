# Perfect Machine Hypotheses

## What This File Is

This note distills the research-facing hypotheses that fell out of the
2026-05-23 code-map pass. It is not proof. It is a set of cuts worth testing
against real Scarlett, room, Raven, phone, and camera data.

## Sources Checked

- Steve Mann, *The Chirplet Transform: A new signal analysis technique based on
  affine relationships in the time-frequency plane*:
  https://www.media.mit.edu/publications/the-chirplet-transform-a-new-signal-analysis-technique-based-on-affine-relationships-in-the-time-frequency-plane/
- LoRa/CSS synchronization literature, including recent timestamping analysis
  and CSS receiver/tutorial material:
  https://www.mdpi.com/1999-5903/18/2/80
  https://arxiv.org/abs/2310.10503
- GCC-PHAT and improved time-delay estimation literature:
  https://pmc.ncbi.nlm.nih.gov/articles/PMC9571281/
- General/high-resolution chirplet-transform literature:
  https://www.sciencedirect.com/science/article/pii/S0888327015003994
  https://arxiv.org/abs/2108.00572

## Hypothesis 1: The Active Witness Is An Acoustic CSS Receiver

The current chirp-bin path should be treated as a controlled acoustic chirp
spread-spectrum receiver, not as a generic time-frequency detector. We own the
emitter schedule and codebook. That means the efficient shape is:

1. dechirp candidate windows with the known slope;
2. score a small bin bank;
3. preserve nearby time/frequency ambiguity;
4. solve identity through code-valid symbol tuples and clock consistency;
5. refine timing against the scheduled waveform.

This matches the live code better than broad chirplet matching and keeps the
phone/Raven receiver shape small enough to deploy.

## Hypothesis 2: Passive Program Audio Is Evidence, Not Authority

GCC-PHAT-style passive sync is valuable when music/game audio is present. It is
not a canonical timeline. The coherent split is:

- passive path estimates relative delay and drift from broadband program audio;
- active path emits canonical codebook anchors;
- hybrid mode uses passive confidence to decide whether the active watermark is
  necessary.

The passive estimator should feed confidence and drift hints, not codebook
anchors.

## Hypothesis 3: Calibration Is A Response Surface

The same active chirp-bin observations should feed four consumers:

- timing delay and SRO;
- magnitude response normalization;
- phase/group-delay correction;
- adaptive codebook selection.

If a mic path only preserves 10-16 reliable bins, the correct move is to use
those bins with a higher-order code, not to keep spending energy on dead
symbols. This is already the direction of `MimirChirpBinCalibrationModel`; the
next work is to prove it against meatspace captures instead of only clean
loopback.

## Hypothesis 4: The Actuator Belongs Below C#

C# can own orchestration, buffers, config, UI, and timing belief. The hot audio
actuator should not live there. Fractional delay, variable-rate resampling,
phase/group-delay correction, voice separation, and spatial stems belong in
Faust/native DSP, fed by the runtime's delay/SRO/calibration state.

## Hypothesis 5: Receivers Should Know Time Locally

For Raven and smartphone receivers, the desirable shape is:

- ship codebook, schedule epoch, and path calibration;
- decode chirp-bin anchors locally from mic samples;
- fit local sample clock to canonical source time;
- report compact timing/quality/calibration observations back to Starfire.

That keeps network latency out of the timing authority. The network transports
observations; it does not define the clock.

## Next Tests

1. Use the stored Scarlett physical-mic captures to compare calibration-weighted
   chirp-bin decode against unweighted decode.
2. Emit a reduced reliable-bin codebook from the physical calibration model and
   re-run chirp-only through the real monitor/mic path.
3. Add an actuator proof that applies `MimirAudioSynchronizationState` to a
   fractional-delay/resampler test path.
4. Prototype a tiny standalone receiver process that consumes only
   codebook/schedule/model JSON plus live mic Float32 input.

