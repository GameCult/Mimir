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

List DirectShow devices and options through FFmpeg:

```powershell
$ff = .\.venv\Scripts\python.exe -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())"
& $ff -hide_banner -list_devices true -f dshow -i dummy
& $ff -hide_banner -list_options true -f dshow -i video="PS3 Eye Universal"
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
- `dshow:2` / `msmf:2`: second Razer Kiyo-class RGB camera, likely Kiyo Pro.
- `dshow:3`: first PS3 Eye through PS3 Eye Universal Driver / PS3EyeDirectShow.
- `dshow:4`: second PS3 Eye through PS3 Eye Universal Driver / PS3EyeDirectShow.

Mode probe notes:

- Historical diagnostic only: `msmf:0` LeapUVC accepted a `320x240x120` request and reported about 115 fps, while the old OpenCV probe measured around 52 fps. Treat that as evidence that the sensor mode is plausible and that the diagnostic path is too soft for live timing. Mimir live ingest should talk directly to the driver path.
- Kiyo RGB devices produce snapshots, but initial isolated mode probes against Kiyo indices timed out. Do not trust FPS configuration for Kiyos until a backend-specific capture path is chosen.
- PS3 Eye default DirectShow capture works at `640x480` near 30 fps. High-FPS tracking modes are not proven yet; mode probes against the DirectShow filter can hang or collide with another open handle, so test them sequentially.

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

opentrack/libusb status:

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

That path is currently rejected for frame capture on this machine. Keep it as a mode-enumeration reference, not the live capture path.

Working DirectShow path:

- Installed AllanCat `PS3 Eye Universal Driver` / `PS3EyeDirectShow` from `PS3EyeInstaller1.1.msi`.
- Installed source filters:
  - `C:\Program Files\PS3 Eye Universal Driver\PS3EyeSourceFilter64.dll`
  - `C:\Program Files (x86)\PS3 Eye Universal Driver\PS3EyeSourceFilter.dll`
- The filter's camera count is controlled by `ChangeCameraNumber.bat`, which unregisters/registers the source filter with `/i:<count>`.
- With two cameras connected, register both filters for two cameras from an elevated shell:

```powershell
$dll32 = 'C:\Program Files (x86)\PS3 Eye Universal Driver\PS3EyeSourceFilter.dll'
$dll64 = 'C:\Program Files\PS3 Eye Universal Driver\PS3EyeSourceFilter64.dll'
Start-Process regsvr32.exe -ArgumentList "/n /i:2 `"$dll32`"" -Verb RunAs -Wait
Start-Process regsvr32.exe -ArgumentList "/n /i:2 `"$dll64`"" -Verb RunAs -Wait
```

After registration, this machine exposes:

- `dshow:3`: PS3 Eye, default `640x480`, about 30 fps.
- `dshow:4`: PS3 Eye, default `640x480`, about 30 fps.
- Simultaneous three-second capture from indices 3 and 4 produced 90 frames each, about 29.8-30.0 fps.
- DirectShow device names are `PS3 Eye Universal` and `PS3 Eye Universal2`; use these names when forcing high-rate modes through FFmpeg.

Known caveats:

- Historical diagnostic only: OpenCV `mode-probe` calls that set width/height/fps may time out against the PS3EyeDirectShow filter.
- Parallel probes can race the same libusb/backend state and produce `Access is denied`; test PS3 Eye mode changes one handle at a time.
- Historical diagnostic only: OpenCV by index did not negotiate high-rate PS3 Eye modes correctly. It can report a requested FPS while delivering the wrong shape or frame rate.
- FFmpeg by DirectShow device name does negotiate the filter pins correctly. Verified modes:
  - single `PS3 Eye Universal` at `640x480@30/40/50/60`; `640x480@60` delivered about 56 fps over three seconds
  - single `PS3 Eye Universal` at `320x240@30/40/50/60`; `320x240@60` delivered 180 frames over three seconds
  - single `PS3 Eye Universal2` at `640x480@60` and `320x240@60`; both delivered about 59-60 fps
  - dual `640x480@60` delivered about 54-56 fps per Eye over five seconds
  - dual `320x240@60` delivered 300 frames per Eye over five seconds
- Requests above 60 fps through the installed DirectShow filter do not increase delivered frame rate. `320x240@70` still delivered about 60 fps; `640x480@70` delivered about 51-52 fps.

Current tracking capture command shape:

```powershell
$ff = .\.venv\Scripts\python.exe -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())"
& $ff -hide_banner -f dshow -video_size 320x240 -framerate 60 -pixel_format bgr24 -i video="PS3 Eye Universal" -an -f rawvideo -
& $ff -hide_banner -f dshow -video_size 320x240 -framerate 60 -pixel_format bgr24 -i video="PS3 Eye Universal2" -an -f rawvideo -
```

That command emits raw BGR frames to stdout. A real tracker should launch each FFmpeg capture as a child process, read fixed-size `320 * 240 * 3` frame packets, and timestamp packet arrival at the process boundary.

Next escalation is no longer "find any video path." It is:

- capture ChArUco intrinsics for `dshow:3` and `dshow:4` at the working default mode
- use FFmpeg DirectShow capture for PS3 Eye tracking at `320x240@60`
- patch/rebuild PS3EyeDirectShow if more than 60 fps is required; the installed filter advertises a practical 60 fps ceiling
- keep Leap/Kiyos/PS3 Eyes split across USB root paths where possible

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
