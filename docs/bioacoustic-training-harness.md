# Bioacoustic Training Harness

## Purpose

The bioacoustic receiver is tuned by benchmark receipts, not by console optimism.
`Mimir.BufferSmoke --bioacoustic-train` runs a small hypothesis panel over the
current motif language and records both queryable CultCache evidence and audio
artifacts.

This is authoring and calibration machinery. It is allowed to allocate, score,
serialize, and leave receipts. It is not the runtime hot loop.

## Command

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-train --sample-rate 48000 --seconds 0.5 --output artifacts/bioacoustic-training
```

Each run writes:

- `bioacoustic-training.cc`: typed CultCache results with provenance, decoder
  settings, degradation name, scores, timing, and artifact hashes.
- `run-summary.json`: short best-result summary.
- `source-pre-warp.wav`: the emitted source song.
- `<hypothesis>/<degradation>/pre-warp.wav`: source audio for that scored case.
- `<hypothesis>/<degradation>/post-warp.wav`: mel-cepstral warp/blur round-trip.
- `<hypothesis>/<degradation>/reconstructed-from-detections.wav`: song rebuilt
  from detected word features.
- `<hypothesis>/<degradation>/summary.json`: per-case receipt mirror.

## Current Hypotheses

- `baseline-mfcc-index`: current indexed MFCC/log-mel smoke shape.
- `compact-fast-index`: smaller feature and hash surface to test speed against
  identity loss.
- `robust-wide-index`: wider cepstral/augmentation surface to test degraded-path
  survival.
- `highband-room-index`: upper-band-biased receiver to avoid low room junk.

The harness intentionally scores identity, timing, and speed separately:

```text
total = identity * 0.62 + timing * 0.18 + speed * 0.20
```

Aggregate score is a triage knife. Identity tells whether the word language
survived. Timing tells whether detected words landed on the canonical clock.
Speed tells whether the receiver shape can plausibly become a streaming machine.

## Latest Local Receipt

Run:

```text
artifacts/bioacoustic-training/bioacoustic-20260524-150729/
```

This run adds global clock hypothesis scoring. Timing is no longer raw
per-word absolute error; it is the confidence of a coherent clock/path fit over
detected words. Single-anchor fits are deliberately weak, and sample-rate slope
is only allowed when enough anchors exist.

Best aggregate result:

```text
compact-fast-index / clean-roundtrip
total=0.752 identity=0.908 timing=0.905 speed=0.132 realtime=6.6x
```

Useful observations:

- `compact-fast-index` is still the clean-throughput winner when enough anchors
  survive.
- `baseline-mfcc-index` is still the steadier degraded-path receiver.
- `highband-room-index` shows real value under warp, but needs more anchor
  density before it can own timing.
- `compact-fast-index` remains faster, but its warped-path identity and timing
  confidence are weaker.
- `robust-wide-index` still costs too much for the current panel, but it gives
  useful pressure under heavier degradation.
- The global clock hypothesis is the right scoring authority, but short windows
  and weak degradations can still starve it. Future training runs should score
  time-to-lock separately from identity. Anchor coverage is now persisted in the
  CultCache clock snapshot so optimization can target lock density directly.

Previous receipt:

Run:

```text
artifacts/bioacoustic-training/bioacoustic-20260524-103438/
```

Best aggregate result:

```text
compact-fast-index / clean-roundtrip
total=0.700 identity=0.997 timing=0.357 speed=0.085 realtime=4.2x
```

Useful observations:

- `compact-fast-index` is faster on clean/light blur, but collapses under warp.
- `baseline-mfcc-index` keeps better identity under light warp than compact.
- `highband-room-index` wins some blur/warp-blur cases, which supports
  room-path band selection as a real tuning axis.
- `robust-wide-index` is not currently worth its extra CPU except as pressure
  for heavier blur/warp cases.
- Timing mean absolute error is still poor under warped cepstral reconstruction.
  That means the next cut is global delay/clock/path hypothesis over detected
  words, not just “more cepstra.”

## Architecture Pressure

The benchmark now uses CultLib at the BufferSmoke edge, but Mimir itself should
be a CultMesh app. Phones, microcontrollers, Raven, and Starfire need typed
state surfaces for schedule/codebook snapshots, calibration receipts, learned
path response, and work offload. CultCache receipts are the durable proof layer;
the next runtime cut should promote the live state contracts into mesh-owned
documents rather than leaving them as harness-only artifacts.
