# Complex Contour Live Scarlett Receipt 2026-05-28 00:23:13

## Setup

- Renderer: `canary-packet-trill`
- Playback artifact: `artifacts/asio/canary-packet-live-20260528-002313-f32.raw`
- Capture artifact: `artifacts/asio/scarlett-canary-packet-live-20260528-002313-f32.raw`
- ASIO driver: `Focusrite USB ASIO`
- Sample rate: `192000`
- Capture seconds: `4.2`
- Inputs: `Input 1`, `Input 2`, `Loopback 1`, `Loopback 2`
- Outputs: `Output 1`, `Output 2`
- Playback gain: `1.0`
- Captured frames: `805888`
- Nonzero samples: `2294204`

## Results

Reference channel is `asio-ch2` / Loopback 1.

| Candidate | Meaning | Estimate | Prediction Error | Confidence | Direct Hits | Notes |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| `asio-ch3` | Loopback 2 | `-0.011837` samples / `-0.062 us` | `-0.062 us` | `0.956` | `257` | good loopback proof |
| `asio-ch1` | Input 2 | no lock | n/a | n/a | `0` | no contour hits |
| `asio-ch0` | Input 1 | no lock | n/a | n/a | `0` | no contour hits |

## Signal Sanity Check

`asio-ch2` and `asio-ch3` contain the rendered packet-song witness. Physical
inputs are present but far below usable contour-lock level in this capture.

| Channel | RMS | Peak | Speech RMS | Ultrasonic RMS |
| --- | ---: | ---: | ---: | ---: |
| `asio-ch0` | `0.000525` | `0.002648` | `0.000063` | `0.000187` |
| `asio-ch1` | `0.000087` | `0.000465` | `0.000010` | `0.000031` |
| `asio-ch2` | `0.006676` | `0.028226` | `0.005157` | `0.002268` |
| `asio-ch3` | `0.006676` | `0.028226` | `0.005157` | `0.002268` |

## Interpretation

The complex-contour receiver and ASIO loopback path still prove sub-microsecond
interface timing on a fresh capture. The microphone step is blocked by current
input signal state, not by the contour decoder: both physical input channels
produced zero contour hits against 1480 reference hits. Next physical proof
needs the mic gain/routing/source placement fixed before changing receiver
logic.
