# Aquarium Spatial Audio

Aquarium hosts the Mimir runtime UI and presents the synchronized program
surface. Faust/native DSP owns the hot audio graph behind that surface.

## Contract

- Mimir.Runtime exposes bounded audio buffers and stream health.
- Native audio workers append mic, loopback, and network audio blocks.
- Faust/native DSP consumes aligned blocks and emits stems plus spatial bed.
- Aquarium shows controls and debug state, then routes final outputs toward OBS.

The audio path should be inspectable from the app, but the app UI is not the DSP
engine. Runtime state crosses as typed buffers, descriptors, and controls.
