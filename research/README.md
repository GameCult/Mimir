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
- Fensalir D3D12 compute/render paths;
- Faust/native DSP;
- OBS-facing program outputs.

Audio timing research entry points:

- `chirplet-sync-decoder/summary.md`: chirplet transform notes, why dense
  matched-filter search was cut from the hybrid hot path, and the current
  dechirp plus FFT/Goertzel decoder shape.
- `live-adaptive-sync/summary.md`: passive program-audio synchronization and
  sample-rate-offset estimation.
- `feedback-calibration/summary.md`: online room/speaker/mic response
  calibration.
- `ambisonic-aquasynth/summary.md`: spatial audio and Ambisonic reference
  material; useful background, not a license to call unsynchronized mics a
  coherent field.

Do not promote mirrored examples, old probes, or external library demos into the
Mimir hot path just because they exist here. If research points at a concept,
rebuild the live version in the Mimir/Fensalir/native stack with a clear owner
and invariant.
