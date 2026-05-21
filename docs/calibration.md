# Calibration

Calibration data is measured truth for the live runtime. It is not the runtime.

## Current Policy

- Keep calibration artifacts under `calibration/` when they record real device
  identity, timing, geometry, or audio response.
- Prefer vendor SDKs, native driver APIs, Media Foundation, DirectShow, libusb,
  LeapC, and GPU interop for new capture work.
- Treat old measurements as evidence only when they still change a live
  decision.

## Needed Measurements

- Leap stereo IR cadence and device timestamp behavior.
- Per-camera intrinsics and fixed rig extrinsics.
- Mic/speaker positions and polarity.
- Loopback delay for local and co-streamer playback paths.
- OBS presentation delay for bridge endpoints.

Calibration tooling should be native or vendor-backed from here. If a measurement
cannot be reproduced without the deleted scripting stack, record the gap and
rebuild the measurement path in the live language stack.
