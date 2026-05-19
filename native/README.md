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
- `LocalcastSampleHandle` is sample metadata plus a `payload_handle` and
  provenance flags. Live samples must have `flags == 0`; diagnostic or unknown
  flagged samples are rejected by the live reservoir.
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
  MessagePack files;
- it exposes total and typed-view sample reads so Aquarium/Faust can consume the
  same rolling window producers append to.

`LocalcastProducer` is the native ingress helper for capture workers:

- it owns sample kind, source hash, and monotonically increasing sequence;
- it creates live sample handles with `flags == 0`;
- it appends into `LocalcastRuntime` without letting hardware adapters invent
  reservoir metadata.

`LocalcastAudioBlockDescriptor` is the first typed payload descriptor:

- it describes the caller-owned audio block handle, frame count, channel count,
  sample rate, sample format, start sample, and channel-layout hash;
- `localcast_producer_push_audio_block` only accepts `AudioBlock` producers and
  stores the descriptor pointer as the sample payload handle;
- the reservoir still does not copy or own audio memory.

`LocalcastRenderPacketDescriptor` is the visual equivalent:

- it describes the caller-owned point buffer handle, point count/stride, target
  dimensions, source-time span, presentation time, audio-alignment time, and an
  optional metadata handle;
- `localcast_producer_push_render_packet` only accepts `RenderPacket`
  producers and stores the descriptor pointer as the sample payload handle;
- Aquarium owns decoding and GPU upload. The reservoir only indexes when this
  render packet exists in the shared rolling window.

Payload handles are owned by the caller. Aquarium should treat them as handles
to GPU/native visual memory; Faust/native DSP should treat them as handles to
audio buffers. This crate does not interpret those bytes. It is the clocked
retention authority, not the renderer or DSP engine in a tiny hat.
