# Prior Research Synthesis

## Purpose

Mimir already carries useful research archives. This note distills the pieces
that still steer the live machine so the next pass does not reread every mirror
before touching code.

## Chirplet Sync Decoder Archive

Location: `research/chirplet-sync-decoder/`

Durable lesson:

- Generic chirplet matching is valid signal analysis, but the controlled Mimir
  beacon should be receiver-cheap by construction.
- Dechirp plus FFT/Goertzel is the important communications trick.
- Symbol identity should be built from common chirp duration/slope/window plus
  decodable frequency bin; rhythm and gaps are additional evidence.

Impact on current work:

- `MimirChirpBinTimeline` is directionally right.
- The next work is streaming state and calibrated likelihood, not resurrecting
  dense matched-filter banks.

## Live Adaptive Sync Archive

Location: `research/live-adaptive-sync/`

Durable lesson:

- Initial delay and sample-rate offset are separate loops.
- SRO is inevitable across independent clocks and must be estimated/compensated.
- Resampling/fractional delay is the actuator when hardware clocks cannot be
  controlled.
- GCC-PHAT/TDOA is evidence for relative arrival and localization, not a
  canonical timeline by itself.

Impact on current work:

- `MimirAudioSynchronizationState` needs to drive a real actuator.
- Delay/SRO estimates should be control inputs to Faust/native DSP.
- Passive mode remains valuable, but it does not replace active codebook time.

## Ambisonic / AquaSynth Archive

Location: `research/ambisonic-aquasynth/`

Durable lesson:

- Arbitrary microphones are not automatically an ambisonic mic.
- Ambisonic output needs declared order, channel ordering, normalization, sample
  rate, and target.
- Source-based spatialization is the honest first path; arbitrary-array field
  reconstruction requires calibration and regularization.

Impact on current work:

- Mimir should not promise high-order field truth until mic geometry and path
  calibration prove it.
- Faust `hoa.lib`, IEM, SPARTA, and SAF are verification/reference tools.
- The initial output can be aligned stems plus a lower-order spatial bed.

## GPU Fusion / Splatting Archive

Location: `research/gpu-fusion-splatting/`

Durable lesson:

- Gaussian splatting is useful for packed GPU representations, tile/bin/sort
  renderers, anisotropic radiance primitives, and dynamic field ideas.
- Mimir's live problem is not offline static-scene 3DGS training.
- CPU readback of dense visual state is poison for the hot path.

Impact on current work:

- Fensalir owns temporal Gaussian/evidence fields.
- Mimir should feed synchronized observations and constraints.
- The first visual fusion proof should be a small live observation-to-splat
  update, not full 4D training.

## Playback / Feedback Calibration Archives

Locations:

- `research/playback-calibration/`
- `research/feedback-calibration/`

Durable lesson:

- Room/speaker/mic paths can be measured as impulse or transfer functions.
- Online acoustic system identification and adaptive echo cancellation literature
  is relevant because Mimir is emitting and listening in the same room.
- Magnitude-only response is insufficient; phase/group delay are part of the
  timing machine.

Impact on current work:

- The chirp-bin calibration model should keep becoming a path transfer model.
- Active witness design should support both timeline decoding and transfer
  function estimation.
- Hybrid watermarking should learn from echo-cancellation and system-identification
  practice rather than just hiding chirps under music.

## Visual Spatial Map Archive

Location: `research/visual-spatial-map/`

Durable lesson:

- Direct device paths and camera-specific quirks matter. Framework convenience
  layers hide timing, buffering, exposure, and bandwidth behavior.
- Leap, Kiyos, and PS3 Eyes have different roles: timing/IR, RGB ground truth,
  and high-rate tracking.
- Multi-camera calibration is a geometry problem before it is a rendering
  problem.

Impact on current work:

- `IMimirVideoCaptureDriver` is the correct production seam.
- Native probes should be promoted into workers only after they preserve
  timestamps, payload handles, and cadence.
- Fensalir should consume calibrated camera observations, not raw app-specific
  UI experiments.

## Repository-Level Conclusion

The archives agree on one architecture:

```text
calibrated sensors -> rolling evidence window -> canonical time -> native/GPU/DSP lowering -> program outputs
```

They do not support:

- framework-mediated capture in the six-camera hot loop;
- generic chirplet transforms as the default controlled-beacon receiver;
- arbitrary ambisonic claims from unsynchronized heterogeneous mics;
- CPU-owned dense visual fusion;
- OBS as synchronization authority.

