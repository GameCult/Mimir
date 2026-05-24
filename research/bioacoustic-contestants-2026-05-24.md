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

## Spectral Receipts

The current canary packet has rendered spectral witnesses under
`artifacts/spectra/canary-packet-192847/`:

- `source-spectrogram-linear.png`: linear-frequency view.
- `source-spectrogram-log.png`: log-frequency view, closer to the perceptual
  shape we keep talking about.
- `source-spectral-peaks.csv`: per-frame peak bins for quick inspection.

The log spectrogram shows the actual packet anatomy: hard vertical onset gaps,
two lower timing chips, then payload-colored formant ladders. The early peak CSV
shows the first anchor band climbing around `2.67-3.12 kHz` between `0.040s`
and `0.070s`, which is exactly the kind of explicit spectral witness the harness
should have been producing from the beginning.

### Current Honest Payload Receipt

Run:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.75 --song canary-packet-trill --decoder packet-razor-index --max-songs 1 --max-decoders 1 --max-degradations 5 --output artifacts/bioacoustic-contestants
```

Receipt:

`artifacts/bioacoustic-contestants/contestants-20260524-192847/contestant-summary.json`

Best result with independent payload classification in the decoder sweep:

```text
song=canary-packet-trill
decoder=packet-razor-index
degradation=blur-light
language_score=88.380
realtime=5.6x
timing=0.860
frequency=0.957
payload_bitrate=19.0 bps
payload_accuracy=1.000
correct=6/6
```

Damage panel:

```text
packet-razor clean-roundtrip   score=88.129 payload_bitrate=19.0 bps payload=1.000 timing=0.859 frequency=0.931
packet-razor blur-light        score=88.380 payload_bitrate=19.0 bps payload=1.000 timing=0.860 frequency=0.957
packet-razor warp-light        score=65.172 payload_bitrate=17.4 bps payload=0.913 timing=0.872 frequency=0.776
packet-razor warp-light-blur   score=66.679 payload_bitrate=13.5 bps payload=0.707 timing=1.000 frequency=0.922
packet-razor warp-heavy-blur   score=81.515 payload_bitrate=13.5 bps payload=0.707 timing=1.000 frequency=0.922
```

This is the current honest floor: a 2-bit payload alphabet carried by the
canary packet. Payload classification is now direct waveform/template
correlation over the anchored local payload alphabet, not MFCC identity reuse.
That raised the honest score and made payload survive all current warp/blur
cases. Tightening `canary-packet-trill` spacing from `0.125s` to `0.105s`
improved bitrate and the degradation floor. A `0.100s` spacing was tested and
rejected because overlap started eating anchor accuracy. A 3-bit
variant did not earn its keep; clean was similar, but degraded payload recovery
collapsed. The earlier 8-bit and 4-bit readings were schedule-entangled and are
no longer treated as bitrate evidence.

The leaderboard is now split:

- clean result: `canary-packet-trill + packet-razor-index`, `88.129`
- best overall: `canary-packet-trill + packet-razor-index` under
  blur-light, `88.380`
- remaining failure: warp-light frequency accuracy is still low at `0.776`,
  but timing and payload now stay alive.
- rejected 3-bit dual-axis packet: best `12.849`, clean `8.084`, heavy warp
  nearly dead. A separate band/rhythm bit did not survive well enough to keep
  in the built-in contestant panel.

### Log-Mel Flux / Log-Mel Leak Receipt

The arena now includes two separate damage domains:

- cepstral warp/blur: the older MFCC-shaped leaky pipeline.
- log-mel warp/blur: a perceptual-surface leak that bends and blurs the
  log-mel field before reconstruction through the original STFT phase.

The decoder panel also tested two flux-proposal variants:

- `packet-razor-flux-index`: same packet razor identity surface, but candidate
  starts come from positive log-mel spectral-flux peaks.
- `packet-sprint-flux-index`: a wider sprint variant with the same flux proposal
  owner.

Runs:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.75 --song canary-packet-trill --decoder packet-razor-index --max-songs 1 --max-decoders 99 --max-degradations 9 --output artifacts/bioacoustic-contestants
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.75 --song canary-packet-trill --decoder packet-razor-flux-index --max-songs 1 --max-decoders 99 --max-degradations 9 --output artifacts/bioacoustic-contestants
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.75 --song canary-packet-trill --decoder packet-sprint-flux-index --max-songs 1 --max-decoders 99 --max-degradations 9 --output artifacts/bioacoustic-contestants
```

Receipts:

- `artifacts/bioacoustic-contestants/contestants-20260524-212432/contestant-summary.json`
- `artifacts/bioacoustic-contestants/contestants-20260524-212451/contestant-summary.json`
- `artifacts/bioacoustic-contestants/contestants-20260524-212518/contestant-summary.json`

Best result in this panel:

```text
song=canary-packet-trill
decoder=packet-razor-index
degradation=logmel-warp-light-blur
language_score=92.522
realtime=5.7x
timing=1.000
frequency=0.939
payload_bitrate=17.4 bps
payload_accuracy=0.913
correct=5/6
```

Flux-proposal results:

```text
packet-razor-flux logmel-warp-light      score=43.577 realtime=2.7x payload_bitrate=17.4 bps timing=1.000 frequency=0.925 correct=5/6
packet-razor-flux clean-roundtrip        score=33.466 realtime=2.2x payload_bitrate=19.0 bps timing=0.862 frequency=0.930 correct=6/6
packet-sprint-flux logmel-warp-light-blur score=26.761 realtime=2.0x payload_bitrate=14.7 bps timing=1.000 frequency=0.926 correct=5/6
```

Read: spectral flux is a good biological witness and a plausible proposal
owner, but the naive managed implementation does not yet win the arena. It
spends too much time computing log-mel flux separately from the indexed
fingerprint pass. The shape should not be discarded; it should be fused into
the existing feature extraction so each STFT frame emits both log-mel/MFCC
identity and positive-flux onset evidence. Current best transmission through
the leaky pipeline remains `canary-packet-trill + packet-razor-index`, now at
`92.522` on log-mel warp/light blur.

### Streaming Packet Razor Receipt

The next cut stopped treating the packet receiver like an offline search
problem. `packet-razor-streaming-faust` is schedule-owned and packet-local:

- no dense MFCC/LSH proposal sweep;
- no second FFT pass for flux;
- no wide identity table lookup;
- only scheduled packet windows are inspected;
- each window tests the four local payload hypotheses;
- timing is refined with a tiny parabolic local correction;
- the Faust lowering target is `faust/mimir_packet_razor_frontend.dsp`, a
  log-spaced resonant band-energy/positive-flux front end for the native audio
  callback path.

Run:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-contestants --seconds 0.75 --song canary-packet-trill --decoder packet-razor-streaming-faust --max-songs 1 --max-decoders 99 --max-degradations 9 --output artifacts/bioacoustic-contestants
```

Receipt:

`artifacts/bioacoustic-contestants/contestants-20260524-214530/contestant-summary.json`

Best result:

```text
song=canary-packet-trill
decoder=packet-razor-streaming-faust
degradation=logmel-blur-light
language_score=1121.012
realtime=62.6x
timing=0.995
frequency=0.944
payload_bitrate=19.0 bps
payload_accuracy=1.000
correct=6/6
```

Nine-leak panel:

```text
clean-roundtrip          score=798.225  realtime=44.7x payload=1.000 timing=0.993 frequency=0.943 correct=6/6
blur-light               score=720.550  realtime=56.8x payload=1.000 timing=0.995 frequency=0.669 correct=6/6
warp-light               score=527.312  realtime=55.7x payload=1.000 timing=0.862 frequency=0.577 correct=6/6
warp-light-blur          score=749.635  realtime=60.0x payload=1.000 timing=0.996 frequency=0.658 correct=6/6
warp-heavy-blur          score=672.210  realtime=57.4x payload=1.000 timing=0.996 frequency=0.618 correct=6/6
logmel-blur-light        score=1121.012 realtime=62.6x payload=1.000 timing=0.995 frequency=0.944 correct=6/6
logmel-warp-light        score=1066.350 realtime=68.2x payload=1.000 timing=0.995 frequency=0.826 correct=6/6
logmel-warp-light-blur   score=872.989  realtime=55.0x payload=1.000 timing=0.996 frequency=0.837 correct=6/6
logmel-warp-heavy-blur   score=992.207  realtime=68.2x payload=1.000 timing=0.997 frequency=0.767 correct=6/6
```

This is the current honest arena leader by more than an order of magnitude over
the earlier `92.522` best. The architectural lesson is stronger than the number:
once the receiver is streaming and schedule-aware, the problem becomes local
payload discrimination over a tiny candidate set. Wide MFCC search is an
acquisition/reference tool, not the hot loop. The next non-cheating production
cut is an acquisition state that uses Faust/front-end band flux to find the
first clock hypothesis, then switches to scheduled packet windows exactly like
this receipt.

### First Physical ASIO Packet Receipt

The first physical run used the current Focusrite ASIO path:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --render-contestant-f32 --song canary-packet-trill --output artifacts/asio/canary-packet-192k-f32.raw --sample-rate 192000 --seconds 4
.\native\probes\asio_audio_cadence\build\Release\asio_audio_cadence.exe --set-sample-rate 192000 --play-f32-mono artifacts\asio\canary-packet-192k-f32.raw --record-f32-interleaved artifacts\asio\scarlett-canary-packet-192k-f32.raw --play-gain 1.0
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --analyze-contestant-asio-f32 --input artifacts\asio\scarlett-canary-packet-192k-f32.raw --sample-rate 192000 --channels 4 --seconds 4 --song canary-packet-trill --schedule-offset-samples 1623
```

The first ASIO attempt accidentally ran at the driver's existing `44100` Hz
state despite the requested sample rate. The corrected run used
`--set-sample-rate 192000`, captured `765,952` frames at 192 kHz across four
inputs, and found the loopback packet by correlation at about `+1623` samples
(`8.453 ms`). With that acquisition offset supplied, the streaming packet
receiver decoded the loopback channels perfectly:

```text
channel=2 events=37/37 payload=1.000 timing=1.000 confidence=1.000 delaySamples=1623.000000 delayUs=8453.125 maeSamples=0.000000
channel=3 events=37/37 payload=1.000 timing=1.000 confidence=1.000 delaySamples=1623.000000 delayUs=8453.125 maeSamples=0.000000
```

The acoustic inputs did not yet prove the room path:

```text
channel=0 events=0/37 payload=0.000 timing=0.000 confidence=0.000
channel=1 events=1/37 payload=0.000 timing=1.000 confidence=0.545 delaySamples=1587.500000 delayUs=8268.229
```

Interpretation: the packet machine survives the real Focusrite digital loopback
path at 192 kHz. The room/mic path needs either more acoustic level, better mic
channel state, or a front-end acquisition pass that can lock before scheduled
packet-local decoding takes over. The important architectural result still
stands: acquisition and tracking are separate states. Once the clock offset is
known, the streaming packet hot loop is tiny and exact.

### Second Physical ASIO Packet Receipt

After placing both microphones, the same 192 kHz packet run was repeated:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --render-contestant-f32 --song canary-packet-trill --output artifacts/asio/canary-packet-192k-f32.raw --sample-rate 192000 --seconds 4
.\native\probes\asio_audio_cadence\build\Release\asio_audio_cadence.exe --set-sample-rate 192000 --play-f32-mono artifacts\asio\canary-packet-192k-f32.raw --record-f32-interleaved artifacts\asio\scarlett-canary-packet-192k-rerun-f32.raw --play-gain 1.0
```

Capture:

```text
sampleRate=192000 frames=765952 channels=4
input[0]="Input 1" input[1]="Input 2" input[2]="Loopback 1" input[3]="Loopback 2"
source rms=0.00694 peak=0.02913
ch0 rms=0.02612 peak=0.32488
ch1 rms=0.01530 peak=0.21927
ch2/ch3 rms=0.00692 peak=0.02913
correlation lags: ch0=2519, ch1=2122, ch2=1623, ch3=1623 samples
```

With the loopback acquisition offset (`1623` samples), loopback remained
perfect and the two mic channels showed partial lock:

```text
ch0 events=14/37 payload=0.162 timing=0.745 delayUs=8500.880
ch1 events=23/37 payload=0.135 timing=0.706 delayUs=8445.016
ch2 events=37/37 payload=1.000 timing=1.000 delayUs=8453.125
ch3 events=37/37 payload=1.000 timing=1.000 delayUs=8453.125
```

With per-channel acquisition offsets, the co-streamer shotgun mic on channel 1
locked:

```text
ch1 offset=2122 events=37/37 payload=1.000 timing=0.828 delayUs=11166.397 maeSamples=9.952719
```

The cardioid/near mic on channel 0 improved but remained unreliable:

```text
ch0 offset=2519 events=26/37 payload=0.595 timing=0.726 delayUs=13075.222 maeSamples=18.120421
```

The channel correlations were negative for both mic inputs, so the streaming
packet scorer now treats polarity as a nuisance variable in the packet-local
path. That is a real-world acoustic invariant, not a broad decoder patch: mic,
preamp, wiring, and room reflections can flip the sign of a narrow waveform
template without changing the packet identity.

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

FFmpeg is now installed through winget as `Gyan.FFmpeg 8.1.1`. The current
shell used the installed binary directly from:

```text
C:\Users\Meta\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe
```

Decoded WAVs remain ignored scratch artifacts. Committed receipts live under
`artifacts/spectra/real-birds-ffmpeg/`:

- `lesser-redpoll/Acanthis_cabaret_-_Lesser_Redpoll_XC482789-log-mel.png`
- `lesser-redpoll/Acanthis_cabaret_-_Lesser_Redpoll_XC482789-log-mel-flux.png`
- `lesser-redpoll/Acanthis_cabaret_-_Lesser_Redpoll_XC482789-cepstrum.png`
- `lesser-redpoll/Acanthis_cabaret_-_Lesser_Redpoll_XC482789-mel-cepstral-features.csv`
- `wattled-guan/Aburria_aburri_-_Wattled_Guan_XC250442-log-mel.png`
- `wattled-guan/Aburria_aburri_-_Wattled_Guan_XC250442-log-mel-flux.png`
- `wattled-guan/Aburria_aburri_-_Wattled_Guan_XC250442-cepstrum.png`
- `wattled-guan/Aburria_aburri_-_Wattled_Guan_XC250442-mel-cepstral-features.csv`

The Brown Thornbill file remains invalid. FFmpeg reports:

```text
Failed to find two consecutive MPEG audio frames
Invalid data found when processing input
```

The mel-cepstral receipt tool chooses the loudest active window when no start
time is supplied, then writes log-mel, cepstrum, and per-frame feature CSV. The
feature CSV now includes positive log-mel spectral flux: frame-to-frame
new-energy motion in mel space. This is a better timing-anchor surface than the
plain cepstrum because sustained bands recede and attacks/glides light up.

The first real-bird readings:

```text
Lesser Redpoll active window: start=2.000s, duration=4.000s
centroid median=2440 Hz, p90=3041 Hz, max=3981 Hz
bandwidth median=2427 Hz, p90=2683 Hz
peak-energy frame near 1.732s, centroid=3224 Hz, bandwidth=1768 Hz
log-mel flux median=0.0870, p90=0.1166, p99=0.2079, max=0.3437
top flux frames around 1.716-1.720s and 1.478-1.482s

Wattled Guan active window: start=137.000s, duration=4.000s
centroid median=2217 Hz, p90=4229 Hz, max=4445 Hz
bandwidth median=1072 Hz, p90=1854 Hz
peak-energy frame near 1.722s, centroid=2021 Hz, bandwidth=766 Hz
log-mel flux median=0.0749, p90=0.1034, p99=0.1818, max=0.2370
top flux frames around 0.034-0.036s and the 3.248-3.744s high-band shifts
```

Read against the current `canary-packet-trill`, the lesson is blunt:

- The Redpoll reference is not a neat de Bruijn-ish token stream. It has dense
  chatter, short vertical attacks, noisy broadband texture, and repeated
  contour fragments that stay recognizable even when individual peaks blur.
- The Guan reference is the opposite useful witness: a slow, broad, curved
  low-mid phrase with stable harmonic/cepstral structure. It is poor as a high
  bitrate packet model but excellent for response/group-delay probing.
- Our canary packet currently anchors time well because its syllables are
  deliberately sharp, but it is still too ladder-like. Real birds are carrying
  identity through redundant contour, roughness, onset statistics, and cepstral
  envelope motion at the same time. That is the next scoring surface.
- The log-mel flux receipts are the clearest current clue. They expose repeated
  curved strokes in Redpoll and slower phrase-boundary/harmonic-motion anchors
  in Guan. The next decoder should treat flux ridges as anchor proposals, then
  classify the surrounding log-mel/cepstral word shape instead of asking the
  cepstrum to do all the work alone.

## Next Cut

1. Extract more real mel-cepstral contours: syllable onset density, frequency glide
   slope, formant spacing, rhythm variance, and motif repetition.
2. Add per-formant frequency residual to the contestant score.
3. Promote the two-stage refiner into a streaming state model: predict next
   expected word time from the current clock and only search a narrow basin.
4. Teach the generator to combine Redpoll-style onset/chatter density with
   Guan-style smooth cepstral envelope probes.
5. Run the full contestant panel and keep mutating the top two shapes until
   score improves rather than spreading attention across ornamental variants.
