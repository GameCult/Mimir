# Research Claims Digest

## Purpose

This is the claim-level digest from the research pass. It records what the
external literature and open implementations change about Mimir's design.

## Chirplets / CSS / Receiver Design

Claim:

- A generic chirplet transform is the broad signal-analysis tool.
- A controlled chirp communication receiver should usually exploit waveform
  design: dechirp, bin score, then decode.

Mimir consequence:

- The hot receiver should remain constrained by the known codebook and schedule.
- Full chirplet transforms are reference tools, not first-line runtime code.
- Symbol design should serve demodulation.

References:

- Steve Mann chirplet transform.
- Fast chirplet transform / FPGA implementation work.
- LoRa/CSS demodulator literature and OpenLoRa.

## Passive Delay / Distributed Microphones

Claim:

- Delay and sampling-rate offset are different phenomena.
- Distributed microphone arrays require explicit SRO estimation/compensation.
- GCC-PHAT is useful for TDOA/delay evidence, not enough to own canonical time.

Mimir consequence:

- Passive mode is a confidence-bearing witness.
- Active codebook anchors remain the canonical-time path.
- The actuator must include both fractional delay and SRO control.

References:

- Wang/Doclo SRO estimation.
- Schmalenstroeer/Haeb-Umbach clock skew work.
- Didier distributed adaptive node-specific signal estimation.
- GCC-PHAT refinements.

## Room / Sound Field Reconstruction

Claim:

- Sound-field reconstruction from sparse arrays is an inverse problem with
  geometry, bandwidth, regularization, and noise constraints.
- Ambisonics works cleanly when the microphone array and calibration support the
  basis.
- Equivalent-source / sparse reconstruction methods are useful when the source
  model is sparse and measurement model is known.

Mimir consequence:

- Six heterogeneous mics are not automatically a high-order field mic.
- The first honest target is synchronized stems plus source/constraint estimates.
- Field reconstruction should expose uncertainty and avoid pretending unsupported
  spatial detail exists.

References:

- Pyroomacoustics.
- Spatial Audio Framework.
- ManyEars.
- near-field acoustic holography / equivalent source / compressive sensing
  papers.
- ambisonics/arbitrary-array encoding notes.

## Fractional Delay / SRO Actuation

Claim:

- Farrow structures are standard for continuously variable fractional delay.
- Variable-rate resampling or equivalent phase correction is required for drift.

Mimir consequence:

- Microsecond reporting is incomplete until an actuator moves samples.
- A Farrow proof is the right first native/Faust actuator slice.
- Higher-quality polyphase/ASRC work comes after the control loop is proven.

References:

- AMD Vitis Farrow filter docs.
- MATLAB Farrow fractional delay notes.
- joint SFO/Farrow papers.

## Gaussian Splatting / Visual Fusion

Claim:

- Practical 3DGS renderers use packed GPU buffers, projection, tile binning,
  sorting/order strategies, and optimized rasterization kernels.
- Dynamic/4D Gaussian splatting extends splats over time, but production code is
  still research-shaped.
- Online SLAM/sensor fusion variants are more relevant to Mimir than offline
  static scene training alone.

Mimir consequence:

- Fensalir should own the GPU field.
- Mimir should feed synchronized observations and constraints.
- The first proof is live observation-to-claim-to-render, not full 4DGS training.

References:

- GraphDeco 3DGS.
- 4D Gaussian Splatting.
- Spacetime Gaussians.
- gsplat.
- NVIDIA `vk_gaussian_splatting`.
- StopThePop.
- FlashGS.
- Gaussian-SLAM / multi-sensor calibration papers.

## Native Capture / GPU Resource Boundaries

Claim:

- Windows KS/AVStream is the low-level video streaming surface under higher media
  frameworks.
- D3D shared handles are the relevant resource-sharing family once camera
  payloads can become GPU resources.
- ASIO callbacks and sample positions are the correct place to preserve driver
  clock timing.

Mimir consequence:

- Media frameworks may remain diagnostics/fallbacks, but direct workers own
  production capture.
- Fensalir owns D3D12 resource lifetime and imports.
- Mimir should not route six cameras through stdout/JSON or managed pixel
  processing.

References:

- Microsoft Kernel Streaming docs.
- Microsoft DXGI shared handle docs.
- ASIO specification / callback notes.
- Focusrite channel/loopback documentation.

## Strongest Design Conclusion

The research converges on one architecture:

```text
controlled witness + calibrated sensors + rolling buffers
-> deterministic timeline/response models
-> native DSP and GPU field lowering
-> OBS-facing program output
```

The critical missing production step is not another filter. It is connecting
measurement to actuation and field lowering:

- streaming decoder state;
- calibration-weighted likelihood;
- fractional delay/SRO actuator;
- native camera payload handles;
- Fensalir contract lowering.

