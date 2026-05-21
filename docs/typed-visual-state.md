# Typed Visual State

Typed visual state now means native/runtime descriptors and Aquarium-readable
payload handles, not file-polled render documents.

## Boundary

- Capture drivers append frame descriptors.
- The runtime owns stream buffering and health.
- Native reservoir handles preserve shared timing and typed lookup.
- Aquarium decodes visual payloads, runs GPU fusion, and publishes the program
  surface.

Any remaining file export should be treated as a diagnostic artifact, not as a
live data path.
