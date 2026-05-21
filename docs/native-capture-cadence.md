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

- `native/probes/ks_camera_cadence`

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

## 2026-05-21: Razer Kiyo Kernel Streaming Probe

Installed device state:

- `Razer Kiyo` camera: `VID_1532&PID_0E03&MI_00`;
- `Razer Kiyo Pro` camera: `VID_1532&PID_0E05&MI_00`;
- both expose UVC camera interfaces through the Windows Kernel Streaming
  capture category.

Probe path:

- Windows Kernel Streaming capture interface for `KSCATEGORY_CAPTURE`;
- no Media Foundation;
- no DirectShow graph;
- no OpenCV;
- eight queued asynchronous reads;
- two-second warm-up discarded before each five-second measurement.

Kiyo Pro advertised `MJPG` modes and measured cadence:

| Device | Mode | Advertised FPS | Best Measured FPS | Note |
| --- | --- | ---: | ---: | --- |
| Kiyo Pro | 1280x720 MJPG | 60.00 | 25.20 | manual exposure did not improve cadence |
| Kiyo Pro | 1920x1080 MJPG | 60.00 | 25.18 | manual exposure did not improve cadence |

Plain Kiyo advertised `MJPG` modes and measured cadence:

| Device | Mode | Advertised FPS | Best Measured FPS | Best Setting |
| --- | --- | ---: | ---: | --- |
| Kiyo | 1920x1080 MJPG | 30.00 | 30.27 | manual exposure `-9` or `-10` |
| Kiyo | 1280x720 MJPG | 60.00 | 51.51 | gain minimum after manual exposure sweep |

Conclusion: plain Kiyo is exposure-limited by default and becomes usable once
manual exposure is forced shorter. Kiyo Pro advertises 60 fps modes but the
current KS path delivers about 25 fps regardless of manual exposure, so it needs
a separate driver/control investigation before it should be trusted for high
cadence capture.

Online follow-up:

- Razer's support page for the Kiyo Pro confirms `1080p @ 60/30/24FPS`,
  `720p @ 60FPS`, USB 3.0 connectivity, and HDR support, but also says HDR must
  be toggled off to keep output aligned with the selected frame rate.
- Razer's firmware updater page lists `v1.5.0.1_r1` as the current RZ19-0364
  updater lineage and includes a factory-default reset command in that firmware.
- Razer's flicker/banding guidance says capture frame rate and powerline
  frequency can interact, and points users to the anti-flicker/powerline
  setting.
- Microsoft documents `KSPROPERTY_CAMERACONTROL_AUTO_EXPOSURE_PRIORITY` as the
  UVC low-light compensation control, and says it allows the device to vary
  frame rate under lighting conditions.
- Razer community reports show other users seeing Kiyo Pro firmware or control
  states stuck below 60 fps, including one report that Linux and Windows both
  failed to reach 60 fps after a firmware update.

Local follow-up after that research:

| Device | Mode | Scenario | Result |
| --- | --- | --- | ---: |
| Kiyo Pro | 1280x720 MJPG | baseline, low-light priority value `1`, powerline `50Hz` | 127 frames / 5.009s = 25.35 fps |
| Kiyo Pro | 1280x720 MJPG | low-light compensation off | 125 frames / 5.039s = 24.81 fps |
| Kiyo Pro | 1280x720 MJPG | powerline `60Hz` | 145 frames / 5.008s = 28.95 fps |
| Kiyo Pro | 1920x1080 MJPG | baseline, low-light priority value `1`, powerline `50Hz` | 126 frames / 5.006s = 25.17 fps |
| Kiyo Pro | 1920x1080 MJPG | low-light compensation off | 126 frames / 5.006s = 25.17 fps |
| Kiyo Pro | 1920x1080 MJPG | powerline `60Hz` | 145 frames / 5.020s = 28.88 fps |

Conclusion: the Kiyo Pro's 25 fps result matches the kind of low-light /
anti-flicker / firmware trouble reported elsewhere, but the standard KS
properties are not enough to recover 60 fps locally. Powerline `60Hz` moves the
needle only to about 29 fps. The next investigation should compare this direct
KS path with Synapse/OBS and inspect whether the firmware exposes a vendor or
extension control that selects the real 60 fps profile.

HDR follow-up:

- the probe now tries `KSPROPERTY_CAMERACONTROL_EXTENDED_VIDEOHDR` with both
  filter-scope and pin-0 extended-property headers;
- the Kiyo Pro reports `ERROR_FILE_NOT_FOUND` / `Element not found`;
- setting extended Video HDR off also reports `Element not found`;
- measured cadence is unchanged at about 25 fps for both `720p MJPG` and
  `1080p MJPG`.

Conclusion: HDR may still be part of the Razer/Synapse control story, but it is
not exposed through the standard Windows extended Video HDR KS property on this
device path. The next useful cut is vendor-extension enumeration or an
OBS/Synapse comparison to see whether Razer flips a private UVC extension before
opening the real 60 fps stream.

Vendor/USB descriptor follow-up:

- Synapse is not installed and was not used.
- The probe now has a controls-only mode:
  `ks_camera_cadence.exe vid_1532&pid_0e05&mi_00 --controls-only`.
- The Kiyo Pro exposes two `KSNODETYPE_DEV_SPECIFIC` topology nodes.
- USB VideoControl descriptor parsing found two UVC extension units:
  `{2c49d16a-32b8-4485-3ea8-643a152362f2}` with unit id `2`, six controls,
  and `bmControls=3f00`; `{23e49ed0-1178-4f31-ae52-d2fb8a8d3b48}` with unit id
  `6`, five declared controls, and `bmControls=ff6f`.
- The second extension GUID answers KS selector probes on node `2`. Selectors
  `1`, `3`, `4`, `6`, `11`, `12`, `14`, and `15` report writable support;
  selectors `2`, `5`, `7`, `8`, `9`, `10`, and `15` return current payloads.
  Selector `10` returns ASCII `PYTHON_V2B`, which looks like camera/firmware
  identity rather than an FPS switch.
- The first extension GUID is present in the descriptor but did not answer the
  current KS selector sweep.
- The local Kiyo Pro is currently behind a Genesys Logic `VID_05E3&PID_0610`
  hub and reports USB speed `2` with `bcdUSB=0x0210`, i.e. high-speed USB 2.x,
  not SuperSpeed USB 3.x.

Conclusion: the strongest current explanation for the Pro's 25-29 fps ceiling
is bus topology, not HDR or low-light mode. Razer documents the Kiyo Pro's
60 fps modes as USB 3.0 operation; locally the device is sitting on a USB 2.x
hub path. Move it to a verified SuperSpeed root/hub path before spending more
time mutating unknown vendor selectors.

Motherboard USB3 port follow-up:

- After moving the Kiyo Pro off the external hub and directly onto the
  motherboard USB3 port, the device moved to
  `\\?\usb#root_hub30#...` port `7` and reports `bcdUSB=0x0320`.
- Windows still reports the negotiated connection speed enum as `2`
  (`UsbHighSpeed`, not `UsbSuperSpeed`). The probe now prints the enum name so
  this is harder to misread.
- The advertised mode surface improved: `1280x720` and `1920x1080` now expose
  `YUY2`, `MJPG`, `H264`, and `NV12` at 60 fps.
- Baseline measured cadence stayed around 25 fps for every 720p/1080p format:

| Mode | Advertised FPS | Delivered FPS |
| --- | ---: | ---: |
| 1280x720 YUY2 | 60.00 | 24.96 |
| 1920x1080 YUY2 | 60.00 | 24.98 |
| 1280x720 MJPG | 60.00 | 25.20 |
| 1920x1080 MJPG | 60.00 | 25.03 |
| 1280x720 H264 | 60.00 | 25.19 |
| 1920x1080 H264 | 60.00 | 25.01 |
| 1280x720 NV12 | 60.00 | 25.07 |
| 1920x1080 NV12 | 60.00 | 25.16 |

Conclusion: the direct motherboard port improved enumeration but did not fix
cadence. Because Windows still reports `UsbHighSpeed`, verify the cable and
port path before assuming the private firmware selectors are guilty. If the
same 25 fps lock remains after `UsbSuperSpeed` is confirmed, the next cut is
explicit frame-interval negotiation or cautious vendor-selector tracing.
