# Native Capture Cadence

This file records measured driver-path camera cadence. It is evidence, not a
promise.

## 2026-05-21: PS3 Eye Raw USB Probe

Probe location: temp workspace only, outside this repo.

Probe path:

- raw PS3 Eye userspace driver reference;
- libusb/WinUSB interface 0;
- Bayer frame output;
- no DirectShow;
- no Media Foundation;
- no frame processing;
- five-second measurement window.

Installed device state:

- two PS3 Eyes present as `VID_1415&PID_2000`;
- interface 0 is available through the WinUSB/libusb-style path;
- interface 1 remains the media/audio side and is not the capture path measured
  here.

Sequential results:

| Camera | Requested Mode | Frames | Elapsed | Delivered FPS | Avg Delta |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0 | 320x240 @ 187 | 934 | 5.002s | 186.72 | 5.355 ms |
| 1 | 320x240 @ 187 | 917 | 5.001s | 183.35 | 5.454 ms |
| 0 | 640x480 @ 60 | 292 | 5.006s | 58.33 | 17.118 ms |
| 1 | 640x480 @ 60 | 290 | 5.010s | 57.89 | 17.237 ms |

Dual-camera results in one process:

| Camera | Requested Mode | Frames | Elapsed | Delivered FPS | Avg Delta |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0 | 320x240 @ 187 | 915 | 5.005s | 182.81 | 5.476 ms |
| 1 | 320x240 @ 187 | 916 | 5.005s | 183.01 | 5.460 ms |
| 0 | 640x480 @ 60 | 294 | 5.009s | 58.69 | 17.068 ms |
| 1 | 640x480 @ 60 | 286 | 5.009s | 57.09 | 17.478 ms |

Conclusion: the two PS3 Eyes can be treated as roughly 183 fps timing/marker
witnesses at 320x240 on the current USB topology when using the raw USB path.
At 640x480 they are roughly 57-59 fps.

The probe reported one unrelated invalid USB descriptor from
`USB\VID_0000&PID_0002`; it did not prevent PS3 Eye capture.

## Leap Status

The Leap Motion Controller is currently present as `VID_F182&PID_0003` and bound
to `usbvideo.sys`, which means the active mode is LeapUVC/UVC. That mode is
mutually exclusive with the Ultraleap SDK path for this device.

No close-to-metal Leap frame number has been measured in Mimir yet. The next
probe should use UVC/KS or a deliberately chosen LeapUVC route and should record
delivered stereo IR frames, device/embedded timestamps when available, and
arrival jitter over the same five-second window.
