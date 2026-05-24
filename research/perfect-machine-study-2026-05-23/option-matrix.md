# Implementation Option Matrix

## Purpose

This matrix keeps future cuts from turning into flavor arguments. Each row names
the invariant a subsystem must protect, the conservative implementation, the
aggressive implementation, and the proof that should decide between them.

## Audio Sync Receiver

| Option | Protects | Upside | Failure Mode | Proof |
| --- | --- | --- | --- | --- |
| Current window decoder | Correctness oracle | Easy to inspect; already wired | Repeated full-window work | Keep as reference until streaming matches |
| Streaming scalar decoder | One-pass receiver state | Small, portable, phone-shaped | Weak proposals can miss path-damaged chirps | Synthetic/artifact parity with fewer scans |
| AVX2 scorer | CPU throughput | Fast fixed-bin scoring | Interop and memory traffic erase wins | Batched benchmark beats scalar end-to-end |
| GPU scorer | Large batch throughput | Many channels/candidates at once | Dispatch/readback overhead | Only promote when batches are large and GPU stays resident |
| Full chirplet transform | General signal analysis | Handles unknown chirps | Too broad for controlled codebook | Only if controlled receiver fails after calibration |

## Active Witness Shape

| Option | Protects | Upside | Failure Mode | Proof |
| --- | --- | --- | --- | --- |
| 32 fixed bins | Short triplets | Dense code space | Dead/aliased bands in room | Works only where response is good |
| Adaptive reliable bins + higher order | Physical robustness | Avoids dead bands | Longer lock window | Meatspace anchors increase after calibration |
| Watermarked hybrid | Human tolerance | Less annoying | Masked too well to decode | Passive+active confidence improves without audible abuse |
| Ultrasonic-only | Low distraction | Clean separation from music | Monitors/mics may not pass band | Measured SNR and response beyond hearing |

## Audio Actuator

| Option | Protects | Upside | Failure Mode | Proof |
| --- | --- | --- | --- | --- |
| Integer delay | Coarse alignment | Simple | Not microsecond/subsample | Useful only as baseline |
| Farrow fractional delay | Subsample delay | Cheap, time-varying | Magnitude/phase ripple | Delay residual falls on artifact and live captures |
| Variable ASRC | Long-term SRO | Holds sync over time | More complex buffering | Drift stays bounded over 20+ minutes |
| Frequency-domain phase correction | STFT coherence | Good for analysis frames | Latency/window artifacts | Program material remains clean |

## Audio Field Reconstruction

| Option | Protects | Upside | Failure Mode | Proof |
| --- | --- | --- | --- | --- |
| Aligned stems | OBS usefulness | Immediate production value | Not volumetric | Stems stay synchronized and mixable |
| Source-based spatial bus | Coherent performance mix | Honest with sparse mics | Depends on source metadata | Stable source positions/pans |
| Ambisonic fit | Compact spatial field | Standard ecosystem | Sparse irregular mics unstable | Calibration proves usable order/band |
| Sparse equivalent sources | Physical field estimate | Handles arbitrary geometry | Ill-conditioned inverse problem | Known source reconstructed with residuals |
| Hybrid evidence field | General Mimir shape | Combines audio/visual constraints | Complexity creep | Fensalir reservoir stabilizes claims |

## Camera Ingest

| Option | Protects | Upside | Failure Mode | Proof |
| --- | --- | --- | --- | --- |
| JSON probes | Research observability | Fast to inspect | Not a hot path | Keep diagnostic only |
| Managed wrapper over native reads | App integration | Easier first driver | Copies/GC pressure | One camera sustained cadence |
| Native SPSC frame rings | Throughput | Predictable hot loop | ABI/lifetime complexity | All local cameras sustained |
| GPU shared textures | Copy avoidance | Best final path | Driver/API friction | Fensalir imports and renders frames |

## Visual Fusion

| Option | Protects | Upside | Failure Mode | Proof |
| --- | --- | --- | --- | --- |
| Feature tracks | Sparse evidence | Debuggable | Not visually rich | Tracks align across cameras |
| Point/splat claims | Live volumetric surface | GPU-friendly | Needs calibration | Stable live render from observations |
| Offline 3DGS-style optimization | Visual quality | Mature papers/code | Wrong latency/ownership | Use only for reference or static assets |
| Dynamic/4D splats | Live field | Target shape | Research complexity | Start after direct observations work |

## Fensalir Boundary

| Option | Protects | Upside | Failure Mode | Proof |
| --- | --- | --- | --- | --- |
| Mimir builds render state | Quick local control | Tempting | Duplicates engine | Reject |
| Mimir lowers contract frames | Ownership clarity | Clean boundary | Needs stable contract mapping | Allocation-free lowering benchmark |
| Fensalir owns temporal evidence | Single field authority | Reuses engine work | Requires good candidates | Acoustic/visual claims visible in engine |

