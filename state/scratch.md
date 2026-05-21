# Scratch

## Current Subgoal

No active scratch subgoal.

## Notes

- Viable stream app contract promoted to docs/map/evidence: Aquarium hosts one
  five-second in-memory runtime, local/networked feeds append as producers, and
  OBS receives synchronized program outputs.
- Mimir app scaffold now lives in `Mimir.slnx` with `src/Mimir.App` and
  `src/Mimir.Runtime`; Aquarium Engine remains an external project reference.
- Synchronization ownership started moving into `Mimir.Runtime`: hub, stream
  descriptors, per-stream rolling buffers, settings, and source adapter seam are
  in C# now; concrete capture adapters are next.
- Local six-camera ingest should stay close to the metal: native push sources
  carry payload handles into buffers; process sources are network/diagnostic
  compatibility, not the main local camera path.
- Keep scratch short and disposable.
- Promote only durable lessons into `state/evidence.jsonl` or the map.
