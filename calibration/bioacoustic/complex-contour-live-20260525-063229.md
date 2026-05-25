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
| `asio-ch3` | Loopback 2 | `0.000000` samples | `-0.002628` samples | `-0.014 us` | `0.126` | `216` | `0.008405` samples | `0.001 rad` |
| `asio-ch1` | shotgun/co-streamer mic path | `544.244999` samples | `543.480112` samples | `-3.984 us` | `0.002` | `14` | `3.335599` samples | `0.858 rad` |
| `asio-ch0` | cardioid/local mic path | `781.490952` samples | `781.787929` samples | `+1.547 us` | `0.011` | `23` | `2.949737` samples | `0.382 rad` |

## Interpretation

The interface loopback path is sample-locked well below one microsecond. The
complex contour receiver, using the persisted channel model, also survives the
live DAC/speaker/room/mic/ADC path: both physical mics land within four
microseconds of their prior path seeds in this run, and the cardioid is within
about one and a half microseconds.

This is not a final microsecond sync claim because the physical mic confidence
is still low and the estimates are judged against previous path seeds rather
than an independently measured ground-truth distance. It is, however, the first
fresh meatspace proof that the complex contour/channel-model path can hear the
current witness and return usable direct-path timing instead of only replaying
old artifacts.
