# Research Archive

This directory preserves implementation research and hard-won calibration
lessons. It is allowed to mention external tools, papers, vendor docs, and
failed paths because forgetting those days would be an expensive little act of
self-sabotage.

Research is not runtime authority.

Use it to shape native implementation decisions:

- direct camera and audio drivers;
- pinned/native buffers;
- GPU feature extraction, flow, matching, and fusion;
- Aquarium D3D12 compute/render paths;
- Faust/native DSP;
- OBS-facing program outputs.

Do not promote mirrored examples, old probes, or external library demos into the
Mimir hot path just because they exist here. If research points at a concept,
rebuild the live version in the Mimir/Aquarium/native stack with a clear owner
and invariant.
