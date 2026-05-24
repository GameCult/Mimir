# Mimir Perfect Machine Domain Index

## What This File Is

This is the domain index for future implementation passes. It organizes the
whole Mimir problem space by authority, evidence type, algorithm, hot path, and
open architectural cut.

Read this with:

- `docs/code-algorithm-map.md` for source-level ownership.
- `docs/audio-field.md` for current audio sync narrative.
- `docs/perfect-machine-full-field.md` for the article-form full-field vision:
  direct/network cameras, microphones, tracked phones, glowing markers,
  chirplet room mapping, motion capture, and the spatiotemporal reservoir.
- `docs/native-capture-cadence.md` for measured camera/USB evidence.
- `research/perfect-machine-study-2026-05-23/architecture-rumination.md` for
  the long-form design argument.
- `research/perfect-machine-study-2026-05-23/index.md` for the study directory
  and sample-code index.
- `research/perfect-machine-study-2026-05-23/reading-guide.md` for task-oriented
  navigation through the study.
- `research/perfect-machine-study-2026-05-23/optimization-ledger.md` for hot
  loop candidates.
- `research/perfect-machine-study-2026-05-23/fensalir-integration-map.md` for
  the Mimir/Fensalir ownership cut.
- `research/perfect-machine-study-2026-05-23/calibration-session-spec.md` for
  the planned physical-path calibration command.
- `research/perfect-machine-study-2026-05-23/samples/README.md` for sample
  implementation sketches.

## One-Sentence Machine

Mimir is a bounded-latency realtime field computer: it ingests many clocks,
places their evidence inside one rolling temporal window, extracts audio/video
geometry from that window, and emits an OBS-usable program surface without
letting OBS become the synchronization authority.

## Authority Index

| Authority | Owns | Must Not Own |
| --- | --- | --- |
| `Mimir.Runtime` | stream identity, buffers, sync reports, calibration model loading, source polling, telemetry state | hot DSP actuator, dense visual fusion, broadcast composition |
| Native capture | direct driver callbacks, device timestamps, payload handles, low-jitter capture loops | runtime policy, UI state, OBS composition |
| `native/reservoir` | shared-edge retention and typed views | payload memory interpretation, rendering, DSP |
| Fensalir | window, UI, D3D12 bridge, GPU fusion, program video publication | clock authority, audio actuator |
| Faust/native DSP | fractional delay, resampling, separation, spatialization, stems | stream discovery, UI |
| OBS | final broadcast composition | synchronization, capture timing |
| Raven/phones | local decode of codebook/schedule observations | canonical clock authority |

## Evidence Taxonomy

### Audio Timing Evidence

- ASIO loopback: strongest local reference; same interface clock as Scarlett mic
  channels on Starfire.
- Physical mic chirp-bin decode: acoustic timing plus response surface; current
  failure mode is sparse symbols and room/path coloration.
- Passive program audio: relative delay/drift hint from real content; useful
  when confidence is earned, not a canonical code timeline.
- Network audio: delayed observations from Raven/phones; must carry decoded
  local timing quality, not become a remote clock.

### Audio Calibration Evidence

- Magnitude response: which emitted bins survive per output/mic path.
- Confusion matrix: expected symbol versus observed bin energy/symbol.
- Phase coherence: whether a path has stable phase enough for group-delay
  correction.
- Delay hypotheses: global delay/bin-shift candidates for whole-window decode.
- SRO: delay slope over time, feeding resampler control.

### Visual Evidence

- Leap stereo IR: timing/depth candidate, topology-sensitive but high-value.
- PS3 Eyes: high-rate tracking witnesses.
- Kiyo/Kiyo Pro: RGB context and texture, with Kiyo Pro currently cadence/USB
  limited.
- Future Fensalir GPU state: features, rays, surface/material claims, splats,
  confidence, and render packets.

### Network Evidence

- SRT/FFmpeg bridge: useful v1 transport witness, not synchronized program
  authority.
- Future Raven/phone agents: source-local decode/calibration reporters.

## Algorithm Families

### Controlled Chirp-Bin Active Decode

Purpose:

- Place each mic stream on canonical time.
- Learn response/confusion/phase/group-delay.

Current live shape:

1. render deterministic fixed-slope chirp-bin codebook;
2. detect candidate event windows via cheap energy trace;
3. dechirp each candidate;
4. score a small bin bank;
5. preserve ambiguous symbol/time candidates;
6. select code-valid de Bruijn triplets;
7. fit a coherent source clock;
8. refine source offset against scheduled waveform;
9. emit calibration model evidence.

Pressure:

- The hot loop must move from scalar per-window scoring toward SIMD/GPU batched
  dechirp/bin evaluation once stream counts rise.

### Passive GCC-PHAT Program-Audio Sync

Purpose:

- Use existing music/game audio when it is informative.

Current live shape:

1. window reference/candidate audio;
2. mean remove, preemphasize, Hann window;
3. FFT both;
4. phase-normalized cross-spectrum;
5. inverse FFT to lag correlation;
6. peak/refinement/confidence.

Pressure:

- Passive confidence should be multi-window and stateful before it can suppress
  active watermark emission for long periods.

### Fractional Delay And SRO Actuation

Purpose:

- Turn measurement into aligned samples.

Likely shape:

- Per-source delay state drives a fractional delay line.
- SRO state drives an asynchronous sample-rate converter.
- Fast path belongs in Faust/native DSP, not C#.
- Control loop consumes `MimirAudioSynchronizationState`.

Candidate filters:

- Farrow/Lagrange fractional delay for tiny offset corrections.
- Polyphase windowed-sinc resampler for higher quality.
- PLL-like state on delay slope to keep drift stable.

### Visual Fusion

Purpose:

- Turn synchronized frames into a usable volumetric scene.

Likely shape:

- Native capture pushes typed frame handles and timestamps.
- Fensalir extracts features/depth/optical flow on GPU.
- Native reservoir preserves rolling evidence.
- Fusion emits surface/material/render claims.
- Program video publishes via Spout2.

Pressure:

- Do not build a CPU image pipeline because it is easy to inspect. Six cameras
  make that path collapse into a pile of excuses.

## Hot Loop Budget

### Audio

Given 192 kHz, four local Scarlett channels, and future Raven/phone sources:

- 2 seconds = 384,000 samples per channel.
- Four channels = 1.5 million samples per analysis window.
- Analysis every 100 ms can easily become a full CPU core if windows are
  rescanned naively.

Rules:

- Incremental streaming state beats full-window rescans.
- Precompute chirp kernels.
- Dechirp/bin score in batches.
- Reuse aligned buffers.
- Separate proposal, classification, and global code solving.

### Video

Six optical sensors with high-rate Eyes and Leap IR mean:

- CPU copying/compression/debug JSON is poison in the hot path.
- Capture should produce native handles or stable byte spans with timestamps.
- GPU work should happen once inside Fensalir, not through throwaway staging
  formats.

## Implementation Index

### Current Code To Read First

- `src/Mimir.Runtime/MimirRuntime.cs`: runtime loop and witness emission.
- `src/Mimir.Runtime/Synchronization/MimirSynchronizationHub.cs`: source/buffer
  and report authority.
- `src/Mimir.Runtime/Synchronization/MimirAudioSynchronizationAnalyzer.cs`:
  buffer-to-report analyzer.
- `src/Mimir.Runtime/Synchronization/MimirChirpBinTimeline.cs`: active decoder.
- `src/Mimir.Runtime/Synchronization/MimirChirpBinCalibrationModel.cs`:
  response/confusion/delay model.
- `native/asio_capture/mimir_asio_capture.cpp`: runtime ASIO bridge.
- `native/reservoir/src/lib.rs`: native shared-edge reservoir.

### Current Code To Treat As Temporary

- `MimirFrameEventProcessStreamSource`: diagnostic JSON-line adapter.
- `MimirProcessStreamSource`: bridge/network raw stdout adapter.
- WASAPI probe sample transport.
- FFmpeg/SRT sender scripts.
- Legacy dense chirplet path.

### Current Code To Preserve Conceptually

- Descriptor/sample/buffer identity model.
- Hub report/state cache authority.
- ASIO in-process source shape.
- Chirp-bin codebook/calibration separation.
- Native reservoir shared-edge invariant.

## Cut Lines

- Cut JSON/stdout local camera hot path when concrete direct drivers land.
- Cut legacy chirplet path once chirp-bin decoder has equivalent docs/tests and
  physical path proof.
- Cut bridge scripts only after OBS-facing direct output is real.
- Cut ad hoc response weighting if a formal path model replaces it.

## Future Work Index

1. Streaming chirp-bin decoder state.
2. Calibration-weighted physical decode proof.
3. Reduced alphabet/high-order codebook physical proof.
4. Fractional delay/SRO actuator proof.
5. Direct Leap/PS3/Kiyo runtime drivers.
6. Native reservoir integration with Fensalir/Faust.
7. Raven/phone receiver codebook/schedule local decode.
8. GPU visual fusion and Spout2 program video.
