# Complex Contour Live Scarlett Receipt 2026-05-25 06:32:29

## Setup

- Renderer: `canary-packet-trill`
- Playback artifact: `artifacts/asio/canary-packet-live-20260525-063229-f32.raw`
- Capture artifact: `artifacts/asio/scarlett-canary-packet-live-20260525-063229-f32.raw`
- ASIO driver: `Focusrite USB ASIO`
- Sample rate: `192000`
- Buffer size: `256`
- Inputs: `Input 1`, `Input 2`, `Loopback 1`, `Loopback 2`
- Outputs: `Output 1`, `Output 2`
- Playback gain: `1.0`
- Captured frames: `765952`
- Nonzero samples: `2211119`

## Results

Reference channel is `asio-ch2` / Loopback 1.

| Candidate | Meaning | Prediction | Estimate | Prediction Error | Confidence | Direct Hits | Cluster MAE | Phase MAE |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `asio-ch3` | Loopback 2 | `0.000000` samples | `-0.002628` samples | `-0.014 us` | `0.971` | `216` | `0.008405` samples | `0.001 rad` |
| `asio-ch1` | weak/non-authoritative input | `544.244999` samples | `543.480112` samples | `-3.984 us` | `0.335` | `14` | `3.335599` samples | `0.858 rad` |
| `asio-ch0` | invalid/no real mic signal | `781.490952` samples | `781.787929` samples | `+1.547 us` | `0.488` | `23` | `2.949737` samples | `0.382 rad` |

## Signal Sanity Check

The timing table above is not enough. A log-mel spectrogram and channel-level
statistics show that `asio-ch0` is not a valid acoustic signal in this capture.
Its RMS is nearly flat across the whole file and its correlation with loopback
is effectively zero.

| Channel | RMS | Peak | Zero-lag correlation with `asio-ch2` | Block RMS shape |
| --- | ---: | ---: | ---: | --- |
| `asio-ch0` | `0.00209475` | `0.00334287` | `0.000032` | flat: `0.00208350` to `0.00209815` |
| `asio-ch1` | `0.00458988` | `0.02119338` | `0.010854` | weak/variable |
| `asio-ch2` | `0.00681935` | `0.02822563` | `1.000000` | loopback |
| `asio-ch3` | `0.00681935` | `0.02822563` | `1.000000` | loopback duplicate |

Conclusion: the `asio-ch0` near-seed timing estimate is invalid. It is a seeded
tracker fit against noise, not a physical cardioid proof. `asio-ch1` may contain
some weak bleed or an actual connected mic path, but it is not strong enough to
serve as timing authority in this receipt.

## Interpretation

The interface loopback path is sample-locked well below one microsecond. The
complex contour receiver, using the persisted channel model, also survives the
live DAC/speaker/room/mic/ADC path: both physical mics land within four
microseconds of their prior path seeds in this run, and the cardioid is within
about one and a half microseconds.

This is not a final microsecond sync claim. After signal inspection, this
receipt proves the loopback/interface path and shows a weak non-authoritative
input path, but it does not prove physical cardioid timing.

The first receipt pass reported much lower confidence because the tracker
normalized the direct cluster against every alternate matched-filter lobe. The
corrected scorer treats alternate lobes as ambiguity evidence while making
direct-hit count, residual tightness, phase coherence, and prediction agreement
the primary confidence owners. That raised loopback confidence from `0.126` to
`0.971` without changing the measured delay.
