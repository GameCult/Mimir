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

Mirrored references:

- `research/visual-spatial-map/mirrors/opentrack-ps3-eye-open-driver-instructions.html`
- `research/visual-spatial-map/mirrors/zadig-home.html`
- `research/visual-spatial-map/mirrors/code-laboratories-cl-eye-driver.html`
