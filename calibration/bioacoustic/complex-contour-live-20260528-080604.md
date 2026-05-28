# Complex Contour Live Scarlett Receipt 2026-05-28 08:06:04

## Setup

- Renderer: `canary-packet-trill`
- Playback artifact: `artifacts/asio/canary-packet-live-20260528-080604-f32.raw`
- Capture artifact: `artifacts/asio/scarlett-canary-packet-live-20260528-080604-f32.raw`
- ASIO driver: `Focusrite USB ASIO`
- Sample rate: `192000`
- Capture seconds: `4.2`
- Inputs: armed cardioid and shotgun on Scarlett input channels
- Output routing: headphones by mistake
- Loopback reference: `asio-ch2` / Loopback 1
- Playback gain: `1.0`
- Captured frames: `806656`
- Nonzero samples: `2296810`

## Results

| Candidate | Estimate | Prediction Error | Confidence | Direct Hits | Notes |
| --- | ---: | ---: | ---: | ---: | --- |
| `asio-ch3` | `-0.002628` samples / `-0.014 us` | `-0.014 us` | `0.971` | `216` | loopback proof |
| `asio-ch1` | `544.957302` samples / `2838.319 us` | `+3.710 us` | `0.358` | `13` | better physical path this run |
| `asio-ch0` | `784.256427` samples / `4084.669 us` | `+14.404 us` | `0.276` | `9` | weaker physical path this run |

## Signal Sanity Check

Both physical inputs are now armed and carrying real signal. They remain lower
confidence than loopback, but this is no longer the zero-contour-hit failure
from `complex-contour-live-20260528-002313.md`.

| Channel | RMS | Peak | Speech RMS | Ultrasonic RMS |
| --- | ---: | ---: | ---: | ---: |
| `asio-ch0` | `0.006902` | `0.037563` | `0.002519` | `0.002437` |
| `asio-ch1` | `0.002714` | `0.033561` | `0.002128` | `0.000431` |
| `asio-ch2` | `0.006673` | `0.028226` | `0.005154` | `0.002267` |
| `asio-ch3` | `0.006673` | `0.028226` | `0.005154` | `0.002267` |

## Interpretation

The contour receiver sees both physical inputs, but this run was accidentally
routed to headphones rather than the monitors. Treat it as a misroute witness,
not a room/mic proof. It is useful mainly because it explains the weak physical
confidence and proves the harness did not silently lose the armed inputs.
