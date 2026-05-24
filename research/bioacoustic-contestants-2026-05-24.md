# Bioacoustic Contestants 2026-05-24

## Purpose

This pass moves the harness from "one current song, many decoder knobs" toward
actual contestants: different generated birdcall word shapes fight against the
same indexed cepstral decoder family and the same cepstral warp/blur damage.

The score is intentionally brutal:

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
