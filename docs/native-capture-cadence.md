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

## 2026-05-21: LeapUVC Kernel Streaming Probe

The Leap Motion Controller is currently present as `VID_F182&PID_0003` and bound
to `usbvideo.sys`, which means the active mode is LeapUVC/UVC. That mode is
mutually exclusive with the Ultraleap SDK path for this device.

Probe path:

- Windows Kernel Streaming capture interface for `KSCATEGORY_CAPTURE`;
- LeapUVC through `usbvideo.sys`;
- no Media Foundation;
- no DirectShow graph;
- no OpenCV;
- no frame processing;
- five-second measurement window per advertised mode.

Probe source:

- `native/probes/leap_ks_cadence`

Advertised modes and measured queued KS pull rates:

| Mode | Advertised FPS | Frames | Elapsed | Delivered FPS | Bytes / Frame |
| --- | ---: | ---: | ---: | ---: | ---: |
| 752x480 YUY2 | 50.00 | 173 | 5.008s | 34.55 | 721,920 |
| 640x480 YUY2 | 57.50 | 204 | 5.017s | 40.66 | 614,400 |
| 640x240 YUY2 | 115.00 | 415 | 5.004s | 82.93 | 307,200 |
| 640x120 YUY2 | 214.00 | 777 | 5.000s | 155.39 | 153,600 |
| 752x240 YUY2 | 100.00 | 308 | 5.013s | 61.44 | 360,960 |
| 752x120 YUY2 | 190.00 | 589 | 5.008s | 117.62 | 180,480 |

Conclusion: the current direct KS probe can pull real frames from LeapUVC, but
even eight queued asynchronous reads do not reach the device's advertised
cadence. The useful stereo IR mode is `640x240 YUY2`, advertised at 115 fps and
currently measured at about 83 fps. The next cut should either tune LeapUVC
controls that affect exposure/frame interval or deliberately rebind to a
WinUSB/libusb UVC path if we decide the Windows UVC stack is the bottleneck.

Follow-up control sweep:

- the probe now reports known LeapUVC-ish controls directly through KS;
- it discards a two-second warm-up before the five-second measurement window;
- `640x240 YUY2` steady-state cadence lands at about 110.7 fps;
- `640x120 YUY2` steady-state cadence lands at about 205.9 fps.

Steady-state `640x240 YUY2` control sweep:

| Scenario | Result |
| --- | ---: |
| baseline | 554 frames / 5.007s = 110.65 fps |
| exposure 10us | 554 frames / 5.006s = 110.68 fps |
| gamma off | 554 frames / 5.002s = 110.76 fps |
| HDR off | 554 frames / 5.003s = 110.73 fps |
| LEDs on | 554 frames / 5.003s = 110.72 fps |
| LEDs off | 554 frames / 5.006s = 110.68 fps |
| gain minimum | 554 frames / 5.007s = 110.64 fps |
| dark-frame interval 0 | 555 frames / 5.009s = 110.80 fps |
| fast combined | 554 frames / 5.001s = 110.77 fps |

Rejected controls:

- digital gain via `KSPROPERTY_VIDEOPROCAMP_BRIGHTNESS = 0` returned
  `ERROR_INVALID_PARAMETER`;
- the old LeapUVC FPS-ratio selector through `KSPROPERTY_VIDEOPROCAMP_GAIN`
  returned `ERROR_INVALID_PARAMETER`.

Conclusion: the earlier 83 fps number was mostly startup drag inside the short
measurement window. Once the Leap is hot, the useful stereo mode is close to its
advertised cadence through the current KS path. The half-height mode can beat
the PS3 Eyes on raw frame rate, but the PS3 Eyes still win when comparing their
full 320x240 frames against Leap's useful 640x240 stereo stream.
