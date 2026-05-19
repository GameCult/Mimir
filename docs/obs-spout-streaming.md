# OBS Spout Streaming

## Current Status

The Python/OpenGL Spout sender has been deleted.

LocalCastBridge no longer carries a runnable OpenGL publication path. The only
surviving pieces are diagnostic packet/brush math in
`localcast.diagnostics.render_math` and typed diagnostic visual documents in
`localcast.diagnostics.visual_cache`.

Production Spout2 publication belongs to Aquarium.

## Target

```text
native rolling reservoir
-> Aquarium typed runtime state
-> GPU feature/fusion/material/brush buffers
-> D3D render target
-> Spout2 sender texture
-> OBS Spout2 Capture
```

## Invariants

- OBS receives a named Spout2 texture from Aquarium, not Python/OpenGL.
- LocalCastBridge may expose diagnostic typed documents and CPU render math, but
  it does not own the production renderer.
- JSON remains a diagnostic/status surface only.
- The neighbor SRT video feed is a timed media artifact and must share the same
  presentation ledger as Aquarium output and Faust audio.
