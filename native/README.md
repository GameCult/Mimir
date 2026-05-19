# Native Runtime

This directory is for the hot path.

LocalCastBridge Python remains useful for calibration, launch, status, contract
tests, and offline analysis. It does not own dense live fusion or DSP.

## Crates

- `reservoir`: the first native five-second spatiotemporal reservoir core. It
  owns shared-edge retention and typed sample rings. Aquarium/Faust integration
  should build on this invariant instead of copying the deadline bridge cache.
  It exports `rlib`, `cdylib`, and `staticlib` artifacts plus the C header at
  `reservoir/include/localcast_reservoir.h`.

## Reservoir ABI

The ABI is intentionally small:

- `LocalcastReservoir` is opaque.
- `LocalcastSampleHandle` is sample metadata plus a `payload_handle`.
- Ring ids match the Perfect Machine reservoir rings:
  camera frame, camera feature, scene ray, surface claim, material claim, audio
  block, phase claim, event claim, and render packet.
- The reservoir can create/destroy, push, set/query edge, query window start,
  count a ring, and fetch the latest sample for a sensor hash.

Payload handles are owned by the caller. Aquarium should treat them as handles
to GPU/native visual memory; Faust/native DSP should treat them as handles to
audio buffers. This crate does not interpret those bytes. It is the clocked
retention authority, not the renderer or DSP engine in a tiny hat.
