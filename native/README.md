# Native Runtime

This directory is for the hot path.

LocalCastBridge Python remains useful for calibration, launch, status, contract
tests, and offline analysis. It does not own dense live fusion or DSP.

## Crates

- `reservoir`: the first native five-second spatiotemporal reservoir core. It
  owns shared-edge retention and typed sample rings. Aquarium/Faust integration
  should build on this invariant instead of copying the deadline bridge cache.

