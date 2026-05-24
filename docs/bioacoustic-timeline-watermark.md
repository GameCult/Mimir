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
which self-identifying word did that contour encode?
which speaker vocabulary emitted it?
what delay and clock fit make the heard words coherent?
what did this room/mic path do to the spectrum?
```

## Current Machine

`MimirBioacousticTimeline` is the current active runtime watermark.

- 128 self-identifying word positions, each with a left-speaker and
  right-speaker variant.
- Each word contains four short syllables.
- Each syllable carries a log-frequency glide plus formant-like partials.
- Motif rhythm, contour, syllable duration, root, and speaker tint vary by word.
- No de Bruijn grammar is used in the active bioacoustic path. A word identifies
  its canonical event directly inside the current operating horizon.
- The decoder emits motif candidates, direct word anchors, a source clock fit,
  and per-band response evidence.
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

- motif codebook shape;
- deterministic phrase schedule;
- PCM rendering for active emission;
- streaming motif scoring;
- direct word anchor decoding;
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
  and response.

That is the physical path Mimir needs. A phone mic, camera mic, Scarlett input,
or network receiver should be able to recognize canonical time from a damaged
short phrase, not from a perfect isolated tone.

## Proofs

Synthetic checks:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-self-test
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --standalone-bioacoustic-self-test --sample-rate 48000 --delay-samples 1269.5
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --chirp-only-sync-self-test --sample-rate 48000
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

Render a raw Float32 preview:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --render-bioacoustic-f32 --output artifacts/asio/bioacoustic-f32.raw --seconds 8
```

## Next Cut

The current runtime decoder still uses a motif matched-filter proof. The smoke
harness now carries the next receiver shape: indexed MFCC/log-mel word
fingerprints with path-degradation augmentation. The next coherent cut is to
promote that shape into a true streaming log-mel receiver:

```text
audio window
-> log-mel / constant-Q evidence surface
-> motif/formant/contour candidates
-> projection-hash / ANN candidate retrieval
-> direct word identity decode over a tiny candidate set
-> global delay and clock hypothesis
-> path response model
```

Do not add a pile of threshold rules to make one room capture look good. The
right extension is learned path weighting over motif parts: which contours,
partials, and time gaps survive each output/mic path.
