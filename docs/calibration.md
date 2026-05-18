# Calibration Harness

This is the first physical-device calibration spine for the expanded spatial rig.

## Objective

Turn connected cameras, microphones, speakers, and near-field sensors into timestamped observations that can be calibrated into one world coordinate system.

## Current Mechanism

The harness uses a repo-local Python environment:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements-calibration.txt
```

Run discovery:

```powershell
.\.venv\Scripts\python.exe .\scripts\calibration_probe.py discover
```

Capture camera snapshots:

```powershell
.\.venv\Scripts\python.exe .\scripts\calibration_probe.py snapshot --max-index 10
```

Probe camera mode behavior:

```powershell
.\.venv\Scripts\python.exe .\scripts\calibration_probe.py mode-probe --api msmf --index 0 --profile 320x240x120
```

Smoke-test input devices:

```powershell
.\.venv\Scripts\python.exe .\scripts\calibration_probe.py audio-smoke --duration 1.0
```

Outputs are written under `calibration/runs/`, which is ignored by git. The manifest files are the disposable lab notebook for the live machine.

## Invariants

- Observations must include device identity, API/backend, timestamp, and confidence or failure reason.
- Calibration files may refer to devices by stable identity where possible, not only by fragile camera index.
- The world model owns truth. Snapshots, audio probes, splat packets, and debug images are evidence.
- Do not assume LeapUVC raw access and Ultraleap tracking coexist.
- Do not assume camera-attached microphones share the camera video clock.

## First Cut

The first useful calibration target is not a full point cloud. It is:

1. identify which video devices can produce frames
2. identify which microphone devices can produce samples
3. capture snapshots from each accessible camera
4. record RMS/peak data from each accessible mic
5. fix driver/backend issues for devices that are attached but inaccessible

## First Probe On This Machine

Run artifacts:

- `calibration/runs/20260518T124230Z-discover/manifest.json`
- `calibration/runs/20260518T124231Z-audio-smoke/manifest.json`
- `calibration/runs/20260518T124257Z-snapshot/manifest.json`

Observed video access:

- `dshow:0` / `msmf:0`: LeapUVC raw stereo IR image, visible as green/magenta packed output.
- `dshow:1` / `msmf:1`: Razer Kiyo-class RGB camera.
- `dshow:2`: second Razer Kiyo-class RGB camera, likely Kiyo Pro.
- PS3 Eye video interfaces are attached but not usable yet. Both `USB\VID_1415&PID_2000&MI_00` devices report Windows problem code `28`, meaning the video-side driver is missing. Their audio interfaces are working through generic USB audio.

Mode probe notes:

- `msmf:0` LeapUVC accepts a `320x240x120` request and reports about 115 fps, with rough OpenCV measured capture around 52 fps in the first short probe. Treat that as "tracking mode is plausible," not calibrated throughput.
- Kiyo RGB devices produce snapshots, but initial isolated mode probes against Kiyo indices timed out. Do not trust FPS configuration for Kiyos until a backend-specific capture path is chosen.
- PS3 Eye tracking modes cannot be tested until the open driver is installed for `MI_00`.

Observed audio access over WASAPI at 48 kHz:

- Scarlett Solo USB microphone
- Razer Kiyo Pro microphone
- Razer Kiyo microphone
- both `USB Camera-B4.09.24.1` microphone devices, presumed PS3 Eye audio interfaces
- Scarlett Solo speaker output

## ChArUco Target

A first calibration target has been generated:

```powershell
.\.venv\Scripts\python.exe .\scripts\charuco_calibration.py board
```

Output:

- `calibration/targets/charuco-8x6-dict4x4-100.png`
- `calibration/targets/charuco-8x6-dict4x4-100.json`

Capture intrinsics frames for a camera:

```powershell
.\.venv\Scripts\python.exe .\scripts\charuco_calibration.py capture --api dshow --index 1 --frames 25
```

Solve intrinsics from a capture run:

```powershell
.\.venv\Scripts\python.exe .\scripts\charuco_calibration.py calibrate --images .\calibration\runs\<run-folder>
```

## PS3 Eye Driver Cut

Use the open-driver path first. The current candidate is the opentrack PS3 Eye open driver route:

1. uninstall any old CL Eye / PS3 Eye video driver
2. use Zadig on PS3 Eye interface `MI_00`
3. install `libusb-win32` first
4. if that fails, try `libusbK`
5. re-run:

```powershell
.\.venv\Scripts\python.exe .\scripts\calibration_probe.py discover --max-index 10
.\.venv\Scripts\python.exe .\scripts\calibration_probe.py snapshot --max-index 10
```

Do not install the driver over `MI_01`; that is the working generic USB audio interface.

CL Eye / Code Laboratories is historical reference material only. Do not make this repo depend on a paid, stale, or redistributed proprietary driver. Reverse engineering should stay inside interoperability research boundaries; the live machine should prefer the open driver.

Current post-install status:

- Both PS3 Eye `MI_00` video interfaces are now bound as `libusb-win32 devices` and show `OK` in PnP.
- `E:\Tools\opentrack\modules\ps3eye-mode-test.exe` sees the cameras well enough to enumerate good modes up to `320x240@205Hz` and `640x480@83Hz`.
- Frame reads still fail with repeated `ps3eye: payload error, data[1]=78` and `can't read any frame`.

That means the next fault is not "missing driver"; it is driver/backend/USB streaming. opentrack's guide recommends `libusb-win32` first and `libusbK` as a fallback. PSMove's PS3 Eye documentation says WinUSB through Zadig has been tested with PS3EYEDriver on Windows. If libusb-win32 continues to enumerate modes but fail frames, try the fallback in this order on `MI_00` only:

1. `libusbK`
2. `WinUSB`

After each driver swap, rerun:

```powershell
& 'E:\Tools\opentrack\modules\ps3eye-mode-test.exe'
& 'E:\Tools\opentrack\modules\ps3eye-frame-test.exe'
```

Fallback results so far:

- `libusbK` with both Eyes direct to motherboard: all opentrack modes report `GOOD`, including `320x240@30`, but frame reads still fail with `payload error, data[1]=78/79` and `bad header`.
- `WinUSB` with both live `MI_00` instances: opentrack mode test still reports all modes `GOOD`, but one camera open reports `Access is denied` and frame streaming still repeats `payload error, data[1]=78/79`.
- `libusb-win32`, `libusbK`, and `WinUSB` have all failed frame reads through opentrack on this machine.

Next escalation is no longer "try the other Zadig driver." It is either:

- test opentrack with exactly one PS3 Eye direct to motherboard and every other high-bandwidth camera unplugged
- test a different PS3 Eye backend/library, such as PS3EYEDriver/PSMoveService-style access
- abandon PS3 Eye video for v1 and use LeapUVC plus Kiyos for initial calibration/tracking while preserving the PS3 Eye driver notes as a rejected path until a cleaner backend is found

Also test with one PS3 Eye unplugged and each camera connected directly to a motherboard USB 2.0/3.x port, not through a hub. A libusb issue report shows PS3 Eye/opentrack access can break when certain USB-C hubs are present, even when the camera itself is not plugged into that hub.

USB topology note from this machine:

- The two PS3 Eyes, Razer Kiyo, Razer Kiyo Pro, and Leap Motion are effectively sharing the same external hub/root path.
- After binding both PS3 Eye `MI_00` interfaces, one PS3 Eye composite device and one PS3 Eye audio interface showed `Unknown`, while both `MI_00` interfaces remained visible as `libusb-win32`.
- Two `Unknown USB Device (Device Descriptor Request Failed)` entries were also present on the same hub path.

That makes the hub/root topology a live suspect. A USB3 hub does not make old USB2 high-bandwidth devices independent; USB2 traffic still shares the hub's USB2 side and transaction scheduling. iPiSoft's multiple-PS Eye guidance says a single USB2 hub is only acceptable for low-resolution capture, and opentrack's guide says to try another USB2/USB3 controller if the camera fails.

Practical next cut:

1. Test one PS3 Eye alone on the current hub.
2. If it still fails, test one PS3 Eye alone on a different motherboard root port/controller.
3. Once one streams, add the second PS3 Eye at low tracking mode first, e.g. `320x240@60`, then increase.
4. Put Kiyos/Leap on a different hub/root path if possible.
5. If no physical split is possible, plan for lower PS3 Eye rates and treat the hub as a scarce shared resource, not a magic blue rectangle.

Mirrored references:

- `research/visual-spatial-map/mirrors/opentrack-ps3-eye-open-driver-instructions.html`
- `research/visual-spatial-map/mirrors/zadig-home.html`
- `research/visual-spatial-map/mirrors/code-laboratories-cl-eye-driver.html`
