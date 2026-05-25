# Bioacoustic Timeline Watermark

## Purpose

The active timing signal is no longer supposed to be a row of tidy lab beeps.
That shape proved the math, but it is the wrong animal for a room.

Mimir now treats active acoustic sync as a low-gain bioacoustic language: short
birdsong-like motifs whose identity survives through contour, log-frequency
position, spectral envelope, rhythm, and speaker-specific timbre. The receiver should not
ask only "which bin fired?" It should ask:

```text
which motif contour did I hear?
which self-identifying song word did that contour encode?
which speaker vocabulary emitted it?
which time and frequency anchors did that one call expose?
what delay and clock fit make the heard anchors coherent?
what did this room/mic path do to the spectrum?
```

## Current Machine

`MimirBioacousticTimeline` is the current active runtime watermark.

- 128 self-identifying word positions, each with a left-speaker and
  right-speaker variant.
- Each word contains four short syllables.
- Each syllable carries a log-frequency glide plus formant-like partials.
- Motif rhythm, contour, syllable duration, root, and speaker tint vary by word.
- No de Bruijn grammar is used in the active bioacoustic path. One heard song
  word identifies its canonical event directly inside the current operating
  horizon.
- The decoder emits motif candidates, direct song-word anchors, a source clock
  fit, and per-band response evidence.
- A song word is not one scalar chirp. It is a contour packet: multiple
  syllable onsets, bends, formants, rhythm offsets, payload ornaments, and
  speaker-colored spectral features all become anchors in log-mel space. One
  successful call can pin a cluster of time/frequency constraints at once.
- The first anchor-rich canary packet explicitly shapes timing chips, formant
  pivots, harmonic-envelope notches, and payload ornaments. Calibration receipts
  now persist those intra-call anchors so path learning can discover which
  features survive each output/mic pair.
- `MimirComplexContourMatchedFilterBank` is the first sonar-shaped receiver
  cut: it turns known contour anchors into complex matched-filter responses,
  keeps multiple lobes per anchor, and hands them to `MimirDirectPathTracker`.
  The tracker takes the current path hypothesis as authority, chooses the
  coherent direct-path cluster inside that gate, and records later clusters as
  reflection taps instead of letting them become alternate timelines.
- Runtime emission sends the left vocabulary to the left speaker and the right
  vocabulary to the right speaker, so microphones can learn each side of the
  room separately.
- The runtime active sync evidence kind is `bioacoustic`.

The old `MimirChirpBinTimeline` remains as a calibration/reference artifact
with ASIO capture tooling. It is useful for comparing acoustic paths and
existing calibration captures, but it is no longer the expressive runtime
watermark target.

## Ownership

`MimirBioacousticTimeline` owns:

- song-word codebook shape;
- deterministic phrase schedule;
- PCM rendering for active emission;
- streaming motif scoring;
- direct contour-anchor decoding;
- source clock fitting;
- response-band evidence.

`MimirAudioSynchronizationAnalyzer` owns:

- comparing loopback/reference and candidate receiver decodes;
- choosing passive evidence before active evidence in `hybrid`;
- reporting bioacoustic delay when active evidence is used;
- fractional delay refinement from waveform evidence.

Fensalir audio only emits the rendered watermark. It does not interpret it.
Faust/native DSP will eventually own the fractional-delay/SRO actuator.

## Why This Shape

Birdsong survives ugly channels because the information is redundant:

- contour survives when absolute pitch shifts;
- rhythm survives when frequency response is bad;
- formant envelope survives when one band dies;
- speaker-specific variants expose room asymmetry;
- repeated local structure gives the receiver more than one way to recover time
  response, and local payload identity.

That is the physical path Mimir needs. A phone mic, camera mic, Scarlett input,
or network receiver should be able to recognize canonical time from a damaged
short phrase, not from a perfect isolated tone.

## Proofs

Synthetic checks:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-self-test
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --standalone-bioacoustic-self-test --sample-rate 48000 --delay-samples 1269.5
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --chirp-only-sync-self-test --sample-rate 48000
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --complex-contour-tracker-self-test --sample-rate 192000 --delay-samples 693.5 --reflection-delay-samples 116
```

Expected state:

- `--bioacoustic-self-test` recovers direct word anchors across the canonical
  phrase.
- `--standalone-bioacoustic-self-test` recovers delayed canonical source time
  from codebook/schedule state alone.
- `--chirp-only-sync-self-test` reports `evidence=bioacoustic` and recovers
  fractional delay below printed microsecond precision.
- `--bioacoustic-cepstral-smoke` round-trips the active call through a degraded
  mel-cepstral representation, then decodes word identity with an augmented
  projection-hash MFCC index instead of brute-forcing every word waveform.
- `--bioacoustic-train` runs a hypothesis panel over decoder shapes and writes
  CultCache receipts plus pre-warp, post-warp, and reconstructed-from-detections
  WAV artifacts. See [[bioacoustic-training-harness|Bioacoustic Training
  Harness]].
- `--complex-contour-tracker-self-test` proves the first direct-path tracker
  shape with a louder delayed reflection present. The current 192 kHz synthetic
  smoke reports about `5.249 us` error, so this is a useful tracker surface, not
  the final microsecond claim.

Render a raw Float32 preview:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --render-bioacoustic-f32 --output artifacts/asio/bioacoustic-f32.raw --seconds 8
```

## Next Cut

The current physical receipt path uses the canary packet song as the first
serious contour-anchor witness. The next coherent cut is to promote that shape
into the live streaming log-mel receiver:

```text
audio window
-> log-mel / constant-Q evidence surface
-> contour packet proposals
-> direct song-word identity over a tiny candidate set
-> intra-call time/frequency anchor extraction
-> global delay, clock, and path hypothesis
-> path response model
```

Do not add a pile of threshold rules to make one room capture look good. The
right extension is learned path weighting over contour parts: which syllable
onsets, bends, formants, payload ornaments, partials, and time gaps survive each
output/mic path.

The latest training receipt says identity can survive the current
mel-cepstral degradation panel, but timing collapses under warped domains. That
points the next cut at a global delay/clock/path hypothesis over detected words,
not a larger brute-force dictionary.

That cut has started in the harness. `--bioacoustic-train` persists a global
clock hypothesis per result, and `--calibrate-contestant-asio-f32` now persists
the first physical packet-song response model. The current Scarlett canary
receipt clears 10x realtime and proves that a single song word can own local
identity; the remaining work is to make each syllable/formant contour emit
enough stable anchors that physical mic residuals collapse from tens of
microseconds toward the one-microsecond target. The first anchor-rich receipt
improves loopback but not the physical mics, which means anchor observability is
now present and anchor survivability is the next design problem. The complex
contour tracker now runs against stored Scarlett ASIO captures with seeded path
hypotheses: stored/fresh shotgun estimates land within about `5.860 us` and
`7.247 us` of the existing path fit, while stored/fresh cardioid estimates land
within about `15.817 us` and `8.963 us`. Confidence remains low because the
current matched-filter bank exposes reflection taps but does not yet fit a full
phase/group-delay channel surface.
