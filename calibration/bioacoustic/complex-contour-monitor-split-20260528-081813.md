# Complex Contour Monitor Split Receipt 2026-05-28 08:18:13

## Setup

- Renderer: `canary-packet-trill`
- Playback artifact: `artifacts/asio/canary-packet-monitor-split-20260528-081813-f32.raw`
- Output 0 capture: `artifacts/asio/scarlett-canary-packet-monitor-output0-20260528-081813-f32.raw`
- Output 1 capture: `artifacts/asio/scarlett-canary-packet-monitor-output1-20260528-081813-f32.raw`
- ASIO driver: `Focusrite USB ASIO`
- Sample rate: `192000`
- Capture seconds: `4.2`
- Probe change: `--play-output-channel` selects one ASIO output buffer; `-1`
  remains the default all-output behavior.
- Physical mapping:
  - `asio-ch0` / Input 1: shotgun, about 1.5 m from right monitor and 3.0 m from left monitor
  - `asio-ch1` / Input 2: cardioid, about 0.5 m from left monitor and 1.5 m from right monitor

## Signal Sanity

Output 0 energized loopback channel `asio-ch2`; `asio-ch3` was silent.
Output 1 energized loopback channel `asio-ch3`; `asio-ch2` was silent.

Physical input RMS stayed high in both captures:

| Capture | `asio-ch0` RMS | `asio-ch1` RMS | Reference loopback |
| --- | ---: | ---: | --- |
| output 0 | `0.014660` | `0.050159` | `asio-ch2` |
| output 1 | `0.019050` | `0.040859` | `asio-ch3` |

## Results

| Output | Reference | Mic | Estimate | Confidence | Direct Hits |
| --- | --- | --- | ---: | ---: | ---: |
| output 0 | `asio-ch2` | shotgun / `asio-ch0` | `783.629083` samples / `4081.401 us` | `0.392` | `19` |
| output 1 | `asio-ch3` | shotgun / `asio-ch0` | `779.619141` samples / `4060.516 us` | `0.376` | `18` |
| output 0 | `asio-ch2` | cardioid / `asio-ch1` | `547.779779` samples / `2853.020 us` | `0.285` | `14` |
| output 1 | `asio-ch3` | cardioid / `asio-ch1` | `543.020433` samples / `2828.231 us` | `0.383` | `17` |

## Interpretation

The ASIO probe now isolates output buffers, and the loopback channels confirm
that output 0 and output 1 are distinct at the interface. The physical mic
delays, however, are nearly identical across output channels:

- shotgun differs by about `20.885 us`;
- cardioid differs by about `24.789 us`.

That is not compatible with the declared monitor geometry if output 0 and
output 1 correspond to isolated left/right monitors in the room. The current
evidence says the downstream monitor path is probably summing, mirroring, or
otherwise not isolating the physical speakers as assumed. Do not use this
receipt to learn left/right acoustic geometry until physical speaker isolation
is verified.
