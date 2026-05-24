# Bioacoustic Contestants 2026-05-24

## Purpose

This pass moves the harness from "one current song, many decoder knobs" toward
actual contestants: different generated birdcall word shapes fight against the
same indexed cepstral decoder family and the same cepstral warp/blur damage.

The original score was intentionally brutal:

```text
performance = sqrt(clamp(realtime / 50x, 0..1) * convergence)
anchor      = sqrt(timing_accuracy * frequency_accuracy)
score       = performance * anchor
```

- `convergence` is clock confidence times anchor coverage.
- `timing_accuracy` falls with clock-fit residual in samples.
- `frequency_accuracy` is current word identity precision; exact word identity
  stands in for the emitted frequency contour until the decoder exposes
  per-formant residuals.
- `realtime` is managed harness speed, not the final native SIMD/DSP shape.

## Contestants

- `current-birdcall`: current four-syllable formant word.
- `redpoll-trill`: fast repeated chips with strong temporal onsets.
- `robin-warble`: softer irregular phrase, wider curved formant motion.
- `thrush-ladder`: repeated interval ladder with a shifted reply.
- `thornbill-zigzag`: high-band fast zig-zag syllables.
- `nightingale-cascade`: dense pretty cascade; not yet in the fast receipt.
- `aquasynth-formant-weaver`: AquaSynth-shaped formant/FM word with payload
  ornaments.
- `canary-packet-trill`: six-syllable packet call with hard timing chips and
  payload-colored formant notes.
- `finch-burst-packet`: shorter five-syllable boundary candidate. Useful as a
  speed/blur failure witness, not the current recommendation.

## Language Score Update

The harness now reports the score the project actually cares about:

```text
language_score = realtime_multiplier
               * timing_accuracy
               * frequency_accuracy
               * payload_bitrate_bps
```

This is raw enough to hurt. It rewards a receiver that is fast, places anchors
accurately in time, preserves frequency/shape identity, and recovers
data-bearing words. `payload_bitrate_bps` is computed from the profile's payload
bits per event, event spacing, and payload accuracy.

Important cut: payload accuracy is now classified from the observed word shape.
For each anchored event, the harness compares the observed feature against that
event's payload alphabet and records the classified payload symbol. This is
slower and meaner than treating event identity as payload recovery, but it is
the first score that deserves to be called bitrate evidence.

The score also stopped using word precision as a fake frequency score. Each
decoded observation now carries a cepstral shape accuracy from the winning
template distance. That is still a compact proxy, but it is pointed at the
frequency/formant surface instead of identity bookkeeping.

Clock fitting now uses a global propagation-delay hypothesis. Candidate anchors
vote for one delay, and only anchors coherent with that delay are allowed to own
the clock fit. Observations outside the expected fully captured window remain
diagnostics; they no longer poison the timing kernel. This fixed a real harness
bug where truncated end-of-window calls were being counted as expected anchors.

The first fast panel intentionally tested only the first four contestants,
two decoders, and clean/blur degradations so the loop could produce receipts
inside one turn.

## Fast Receipt

Run:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.5 --max-songs 4 --max-decoders 2 --max-degradations 2 --output artifacts/bioacoustic-contestants
```

Receipt:

`artifacts/bioacoustic-contestants/contestants-20260524-180639/contestant-summary.json`

Best result:

```text
song=robin-warble
decoder=compact-fast-index
degradation=blur-light
score=0.178
performance=0.178
anchor=1.000
timing=1.000
frequency=1.000
convergence=0.485
realtime=3.3x
correct=2/3
```

Read: `robin-warble` produces fewer anchors in the short window, but the anchors
that survive are clean. The current score rewards that more than many sloppy
anchors. This may be correct for clock acquisition, but the next pass should
separate "lock fast" from "lock beautifully once found" so the harness cannot
overvalue sparse perfection.

## Packet Contest Receipts

### Current Honest Payload Receipt

Run:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.75 --song canary-packet-trill --decoder packet-razor-index --max-songs 1 --max-decoders 1 --max-degradations 5 --output artifacts/bioacoustic-contestants
```

Receipt:

`artifacts/bioacoustic-contestants/contestants-20260524-192238/contestant-summary.json`

Best result with independent payload classification in the decoder sweep:

```text
song=canary-packet-trill
decoder=packet-razor-index
degradation=blur-light
language_score=84.086
realtime=6.0x
timing=1.000
frequency=0.953
payload_bitrate=14.6 bps
payload_accuracy=0.913
correct=5/5
```

Damage panel:

```text
packet-razor clean-roundtrip   score=54.749 payload_bitrate=14.6 bps payload=0.913 timing=1.000 frequency=0.935
packet-razor blur-light        score=84.086 payload_bitrate=14.6 bps payload=0.913 timing=1.000 frequency=0.953
packet-razor warp-light        score=0.189  payload_bitrate=11.7 bps payload=0.730 timing=0.004 frequency=0.683
packet-razor warp-light-blur   score=55.505 payload_bitrate=12.8 bps payload=0.800 timing=0.817 frequency=0.927
packet-razor warp-heavy-blur   score=53.020 payload_bitrate=10.7 bps payload=0.671 timing=1.000 frequency=0.911
```

This is the current honest floor: a 2-bit payload alphabet carried by the
canary packet. Payload classification is now direct waveform/template
correlation over the anchored local payload alphabet, not MFCC identity reuse.
That raised best honest score from `54.119` to `84.086` and made payload survive
most warp/blur cases. A 3-bit variant did not earn its keep; clean was similar,
but degraded payload recovery collapsed. The earlier 8-bit and 4-bit readings
were schedule-entangled and are no longer treated as bitrate evidence.

The leaderboard is now split:

- clean maximum: `canary-packet-trill + packet-razor-index`, `54.749`
- best overall: `canary-packet-trill + packet-razor-index` under blur-light,
  `84.086`
- known failure: razor's warp-light timing fit can still collapse almost to
  zero even while payload classification remains nonzero.
- rejected 3-bit dual-axis packet: best `12.849`, clean `8.084`, heavy warp
  nearly dead. A separate band/rhythm bit did not survive well enough to keep
  in the built-in contestant panel.

### Obsolete Schedule-Entangled Receipt

The best current receipt is:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.75 --song canary-packet-trill --decoder packet-razor-index --max-songs 1 --max-decoders 1 --max-degradations 5 --output artifacts/bioacoustic-contestants
```

Receipt:

`artifacts/bioacoustic-contestants/contestants-20260524-185328/contestant-summary.json`

Best result before independent payload classification:

```text
song=canary-packet-trill
decoder=packet-razor-index
degradation=warp-light-blur
language_score=396.683
realtime=6.7x
timing=1.000
frequency=0.922
payload_bitrate=64.0 bps
payload_accuracy=1.000
correct=5/5
```

This receipt is still useful for timing/frequency/speed pressure, but not as a
bitrate claim. It assumed payload recovery from event identity instead of
decoding the payload alphabet from the observed word.

Worst result in that five-degradation panel:

```text
degradation=warp-light
language_score=199.244
realtime=5.1x
timing=1.000
frequency=0.724
payload_bitrate=54.1 bps
correct=5/5
```

The `packet-razor-index` profile is the current speed ceiling: 512-point FFT,
16 mel bins, 6 cepstral coefficients, one projection table, 8-bit hashes, and a
tight proposal budget. It only became viable after adding the missing
warp-light template augmentation; without that, plain warp-light collapsed to
`0.720`.

Boundary tests:

- `packet-sprint-index` is steadier than razor but slower. Best canary result:
  `255.908`, realtime `4.5x`, timing `1.000`, frequency `0.976`.
- `packet-needle-index` buys more speed and loses some frequency fidelity. Best
  canary result: `258.298`, realtime `5.2x`, frequency `0.845`.
- `finch-burst-packet` proves the current lower bound is too short: its best
  razor result was `280.146`, but blur-light collapsed to `4.358`; with sprint
  it recovered blur but topped out at `218.368`.

Broad panel sanity check:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.75 --max-songs 8 --max-decoders 5 --max-degradations 2 --output artifacts/bioacoustic-contestants
```

Receipt:

`artifacts/bioacoustic-contestants/contestants-20260524-185708/contestant-summary.json`

This clean/blur-only panel still picked `canary-packet-trill +
packet-razor-index` as best, with `language_score=264.923`, `realtime=6.0x`,
`timing=1.000`, `frequency=0.944`, and `payload_bitrate=46.7 bps` on clean
roundtrip. The score is lower than the targeted five-degradation best because
the broad panel did not include the warp-blur cases where canary/razor hit
`396.683`.

## Lessons

- The old decoder recognized words but emitted coarse proposal offsets. Timing
  residuals were thousands of samples even when identity was correct.
- Adding waveform refinement around the winning word fixed some anchor cases
  but was too slow when done as a one-sample brute scan.
- The current refiner is a two-stage phrase search: coarse scan, then fine
  snap near the best basin. It is still managed and expensive, but it turns
  feature identity into a real timing anchor for some contestants.
- `robin-warble` currently gives the best anchor accuracy under light blur.
- `current-birdcall` and `redpoll-trill` still identify words but often anchor
  inside the motif instead of at the canonical motif start.
- The next scoring improvement should expose per-formant/frequency residuals
  directly instead of using word identity as a proxy.
- The 96 ms six-syllable canary packet is the best current tradeoff. Pushing the
  motif down near 78 ms increases nominal bitrate but loses too much
  degradation resistance.
- The global delay hypothesis is not optional. A single smeared anchor can move
  a least-squares fit by hundreds of samples if it is allowed to claim clock
  authority directly.
- AquaSynth's useful pressure is the separation of authoring intent from
  realtime DSP: these packet songs should become precompiled/formally described
  emission surfaces, with runtime controls exposed as stable parameters rather
  than regenerated inside the audio callback.

## Real Bird References

Xeno-canto API v3 now requires an API key, so this pass pulled Wikimedia Commons
mirrors of Xeno-canto recordings instead. Local reference files are under
`artifacts/birdsong-reference/`; they are research inputs and should not be
committed as repo source.

Downloaded references:

- Wattled Guan, `XC250442`, Niels Krabbe, CC BY-SA 3.0, Wikimedia Commons /
  Xeno-canto mirror.
- Lesser Redpoll, `XC482789`, Pascal Christe, CC BY-SA 4.0, Wikimedia Commons /
  Xeno-canto mirror.
- Brown Thornbill, `XC446870`, Wikimedia Commons / Xeno-canto mirror fetch was
  attempted but the downloaded file was only 1990 bytes and should be treated
  as invalid until re-fetched.

FFmpeg is not installed on this workstation, so these MP3/OGG references were
not decoded into mel-cepstral matrices in this pass. The generated contestants
encode observations from the metadata and known birdcall structure first:
redpoll-like trills, robin-like warbles, thrush-like interval ladders,
thornbill-like high zig-zags, and nightingale-like cascades.

## Next Cut

1. Install or vendor a decode path for Commons/Xeno-canto MP3/OGG references.
2. Extract real mel-cepstral contours: syllable onset density, frequency glide
   slope, formant spacing, rhythm variance, and motif repetition.
3. Add per-formant frequency residual to the contestant score.
4. Promote the two-stage refiner into a streaming state model: predict next
   expected word time from the current clock and only search a narrow basin.
5. Run the full contestant panel and keep mutating the top two shapes until
   score improves rather than spreading attention across ornamental variants.
