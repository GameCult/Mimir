# Questions And Hypotheses

## Audio

### Q: Is the current physical failure mostly SNR, frequency response, or timing ambiguity?

Hypothesis:

- It is primarily response/confusion plus weak direct-path SNR, not a fundamental
  codebook failure.

Test:

- Compare calibration-weighted likelihood on stored hot mic captures.

### Q: Is 192 kHz worth keeping for active sync?

Hypothesis:

- Yes for fractional timing, loopback, and high-frequency option space; no as a
  magic acoustic fix when speakers/mics do not preserve ultrasonic energy.

Test:

- Run same reliable-bin codebook at 48/96/192 kHz through physical path and
  compare anchor residuals, not just sample granularity.

### Q: Should chirp-only and hybrid share one decoder?

Hypothesis:

- Yes. Mode changes emission policy and confidence gating, not decode
  semantics.

### Q: Can any three chirps always locate timeline position?

Hypothesis:

- Only if they are correctly classified inside the active codebook and the
  receiver keeps enough timing/frequency spacing evidence. In meatspace,
  "any three heard chirps" means "any three reliable decoded symbols after path
  calibration," not raw peaks.

## Visual

### Q: Should Leap or PS3 Eye be first direct runtime driver?

Hypothesis:

- Leap is semantically highest value as timing/depth/near-field witness; PS3 Eye
  may be lower-risk for proving direct high-rate runtime plumbing.

Test:

- Build the smaller driver first if it proves the seam faster, but do not lose
  the Leap timing-camera goal.

### Q: Is realtime Gaussian splatting the first visual fusion target?

Hypothesis:

- No. The first target is GPU feature/claim publication from synchronized
  frames. Splatting is the render/update representation after that.

## Distributed

### Q: Should Raven send raw audio/video or decoded observations?

Hypothesis:

- Both may be needed, but timing authority should come from locally decoded
  codebook/schedule observations where possible. Raw streams are evidence; local
  decoded anchors are stronger over variable network latency.

### Q: Can a phone know canonical time from codebook alone?

Hypothesis:

- Yes if it has schedule epoch, symbol plan, sample-rate estimate, and enough
  reliable decoded anchors. It should report confidence and path model, not just
  "time."

## Implementation

### Q: Is the native reservoir ready to replace C# buffers?

Hypothesis:

- Conceptually yes, operationally not yet. First mirror C# buffer proof through
  reservoir status/views before making it the only path.

### Q: Where does micro-optimization start?

Hypothesis:

- Not with HLSL. First benchmark current active decode; if scalar bin scoring
  dominates, AVX2/FMA scorer is likely the best first optimization. GPU wins
  only when batches are large.

