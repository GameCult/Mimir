# Complex Contour Live Scarlett Receipt 2026-05-28 08:07:27

## Setup

- Renderer: `canary-packet-trill`
- Playback artifact: `artifacts/asio/canary-packet-live-20260528-080727-f32.raw`
- Capture artifact: `artifacts/asio/scarlett-canary-packet-live-20260528-080727-f32.raw`
- ASIO driver: `Focusrite USB ASIO`
- Sample rate: `192000`
- Capture seconds: `4.2`
- Output routing: monitors
- Inputs: `Input 1`, `Input 2`, `Loopback 1`, `Loopback 2`
- Physical mapping:
  - `asio-ch0` / Input 1: shotgun, about 1.5 m from right monitor and 3.0 m from left monitor
  - `asio-ch1` / Input 2: cardioid, about 0.5 m from left monitor and 1.5 m from right monitor
- Loopback reference: `asio-ch2` / Loopback 1
- Playback gain: `1.0`
- Captured frames: `806656`
- Nonzero samples: `2298347`

## Results

| Candidate | Meaning | Estimate | Prediction Error | Confidence | Direct Hits | Notes |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| `asio-ch3` | Loopback 2 | `-0.002628` samples / `-0.014 us` | `-0.014 us` | `0.971` | `216` | interface loopback proof |
| `asio-ch0` | Input 1 | `781.412261` samples / `4069.856 us` | `-0.410 us` | `0.524` | `24` | best physical path this run |
| `asio-ch1` | Input 2 | `543.771315` samples / `2832.142 us` | `-2.467 us` | `0.363` | `14` | weaker physical path this run |

## Signal Sanity Check

Both physical inputs are carrying real monitor-routed acoustic signal. Compared
with the headphone-routed `complex-contour-live-20260528-080604.md` run, both
physical channels have stronger RMS and the path estimates improve.

| Channel | RMS | Peak | Speech RMS | Ultrasonic RMS |
| --- | ---: | ---: | ---: | ---: |
| `asio-ch0` | `0.012653` | `0.072978` | `0.009195` | `0.003837` |
| `asio-ch1` | `0.014106` | `0.093789` | `0.011449` | `0.004182` |
| `asio-ch2` | `0.006673` | `0.028226` | `0.005154` | `0.002267` |
| `asio-ch3` | `0.006673` | `0.028226` | `0.005154` | `0.002267` |

## Interpretation

This is the first credible fresh physical complex-contour proof after arming
both microphones and routing output to monitors. `asio-ch0` is the shotgun and
lands within `0.410 us` of the stored path seed with moderate confidence.
Its measured absolute delay is consistent with a nearest-monitor path on the
order of the right-monitor distance. `asio-ch1` is the cardioid and also locks,
but weaker; its measured absolute delay should be interpreted against the
actual stereo monitor routing and room path, not as a naked source-to-mic
distance yet. The next useful cut is repeated monitor-routed captures to
separate stable direct-path truth from one-run fit luck, then either update the
channel model or add runtime signal-presence gating before actuator authority
consumes physical mic reports.
