# Native Runtime

This directory is for the hot path.

LocalCastBridge Python remains useful for calibration, launch, status, contract
tests, and offline analysis. It does not own dense live fusion or DSP.

## Crates

- `reservoir`: the first native five-second spatiotemporal reservoir core. It
  owns one shared-edge rolling buffer with typed sample views.
  Aquarium/Faust integration should build on this invariant instead of copying
  the deadline bridge cache.
  It exports `rlib`, `cdylib`, and `staticlib` artifacts plus the C header at
  `reservoir/include/localcast_reservoir.h`.

## Reservoir ABI

The reservoir ABI is intentionally small:

- `LocalcastReservoir` is opaque.
- `LocalcastSampleHandle` is sample metadata plus a `payload_handle`.
- Sample kind ids match the Perfect Machine reservoir views:
  camera frame, camera feature, scene ray, surface claim, material claim, audio
  block, phase claim, event claim, and render packet.
- The reservoir can create/destroy, push, set/query edge, query window start,
  count the whole rolling buffer, count a typed view, read samples by total
  buffer index or typed-view index, and fetch the latest sample for a sensor
  hash inside a typed view.

## Runtime ABI

`LocalcastRuntime` is the live spine that should replace the Python file-cache
runtime path:

- it owns one native reservoir;
- it exposes typed producer functions for camera frames, camera features, scene
  rays, surface claims, material claims, audio blocks, phase claims, event
  claims, and render packets;
- it exposes `LocalcastRuntimeStatus` so Aquarium/Faust can inspect the shared
  edge, window start, total sample count, and typed-view counts without polling JSON or
  MessagePack files.

Payload handles are owned by the caller. Aquarium should treat them as handles
to GPU/native visual memory; Faust/native DSP should treat them as handles to
audio buffers. This crate does not interpret those bytes. It is the clocked
retention authority, not the renderer or DSP engine in a tiny hat.
