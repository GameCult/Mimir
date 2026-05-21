import argparse
import json
import subprocess
import sys
import multiprocessing as mp
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RUNS = ROOT / "calibration" / "runs"


def utc_stamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")


def run_dir(kind: str) -> Path:
    for suffix in ["", *[f"-{i}" for i in range(1, 100)]]:
        path = RUNS / f"{utc_stamp()}-{kind}{suffix}"
        try:
            path.mkdir(parents=True, exist_ok=False)
            return path
        except FileExistsError:
            continue
    raise RuntimeError(f"Could not create unique calibration run directory for {kind}")


def write_json(path: Path, data) -> None:
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def powershell_json(script: str):
    completed = subprocess.run(
        ["powershell", "-NoProfile", "-Command", script],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if completed.returncode != 0:
        return {
            "error": completed.stderr.strip(),
            "stdout": completed.stdout.strip(),
            "returncode": completed.returncode,
        }
    text = completed.stdout.strip()
    if not text:
        return []
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return {"raw": text}


def pnp_devices():
    script = r"""
$devices = Get-CimInstance Win32_PnPEntity |
  Where-Object { $_.Name -match 'Kiyo|Leap|Camera|Scarlett|USB Audio|Microphone|Speakers' } |
  Select-Object Name,PNPClass,Status,DeviceID
$devices | ConvertTo-Json -Depth 4
"""
    return powershell_json(script)


def audio_devices():
    try:
        import sounddevice as sd
    except Exception as exc:
        return {"error": f"sounddevice unavailable: {exc!r}"}

    return {
        "hostapis": sd.query_hostapis(),
        "devices": sd.query_devices(),
    }


def camera_probe(max_index: int, apis: list[str], read_frames: int = 1):
    try:
        import cv2
    except Exception as exc:
        return [{"error": f"cv2 unavailable: {exc!r}"}]

    api_map = cv2_api_map()
    results = []
    for api_name in apis:
        api = api_map[api_name]
        for index in range(max_index + 1):
            cap = cv2.VideoCapture(index, api)
            entry = {
                "api": api_name,
                "index": index,
                "opened": bool(cap.isOpened()),
            }
            if cap.isOpened():
                cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
                frames = []
                for _ in range(read_frames):
                    ok, frame = cap.read()
                    frames.append(bool(ok and frame is not None))
                entry["read_ok"] = any(frames)
                entry["read_attempts"] = frames
                entry["backend"] = cap.getBackendName()
                entry["width"] = cap.get(cv2.CAP_PROP_FRAME_WIDTH)
                entry["height"] = cap.get(cv2.CAP_PROP_FRAME_HEIGHT)
                entry["fps"] = cap.get(cv2.CAP_PROP_FPS)
                entry["fourcc"] = int(cap.get(cv2.CAP_PROP_FOURCC))
            cap.release()
            results.append(entry)
    return results


def cv2_api_map():
    import cv2

    return {
        "any": 0,
        "dshow": cv2.CAP_DSHOW,
        "msmf": cv2.CAP_MSMF,
    }


def api_value(name: str):
    return cv2_api_map()[name]


def discover(args):
    out = run_dir("discover")
    manifest = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "python": sys.version,
        "pnp_devices": pnp_devices(),
        "audio": audio_devices(),
        "cameras": camera_probe(args.max_index, args.api, read_frames=1),
    }
    write_json(out / "manifest.json", manifest)
    print(out)
    print(json.dumps(manifest["cameras"], indent=2))


def snapshot(args):
    import cv2

    out = run_dir("snapshot")
    manifest = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "snapshots": [],
    }
    for probe in camera_probe(args.max_index, args.api, read_frames=1):
        if not probe.get("opened") or not probe.get("read_ok"):
            manifest["snapshots"].append({**probe, "snapshot": None})
            continue
        api_map = {"any": 0, "dshow": cv2.CAP_DSHOW, "msmf": cv2.CAP_MSMF}
        cap = cv2.VideoCapture(probe["index"], api_map[probe["api"]])
        ok, frame = cap.read()
        cap.release()
        snap_name = None
        if ok and frame is not None:
            snap_name = f"{probe['api']}-index{probe['index']}.png"
            cv2.imwrite(str(out / snap_name), frame)
        manifest["snapshots"].append({**probe, "snapshot": snap_name})
    write_json(out / "manifest.json", manifest)
    print(out)
    for item in manifest["snapshots"]:
        if item.get("snapshot"):
            print(f"{item['api']}:{item['index']} -> {item['snapshot']}")


def mode_probe(args):
    out = run_dir(f"mode-probe-{args.api}{args.index}")
    result = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "camera": {"api": args.api, "index": args.index},
        "profiles": [],
    }

    profiles = []
    requested_profiles = args.profile or [
        "640x480x30",
        "640x480x60",
        "320x240x60",
        "320x240x120",
    ]
    for item in requested_profiles:
        parts = item.lower().split("x")
        if len(parts) != 3:
            raise SystemExit(f"Profile must be WIDTHxHEIGHTxFPS, got {item}")
        profiles.append(tuple(int(part) for part in parts))

    for width, height, fps in profiles:
        entry = run_profile_probe(args.api, args.index, width, height, fps, args.duration, args.profile_timeout)
        result["profiles"].append(entry)

    write_json(out / "manifest.json", result)
    print(out)
    print(json.dumps(result["profiles"], indent=2))


def _profile_probe_worker(queue, api, index, width, height, fps, duration):
    import cv2
    import time

    cap = cv2.VideoCapture(index, api_value(api))
    if not cap.isOpened():
        queue.put(
            {
                "requested": {"width": width, "height": height, "fps": fps},
                "opened": False,
            }
        )
        return
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
    cap.set(cv2.CAP_PROP_FPS, fps)
    cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
    start = time.perf_counter()
    count = 0
    shape = None
    first_ok = False
    while (time.perf_counter() - start) < duration:
        ok, frame = cap.read()
        if ok and frame is not None:
            count += 1
            first_ok = True
            shape = list(frame.shape)
    elapsed = time.perf_counter() - start
    entry = {
        "requested": {"width": width, "height": height, "fps": fps},
        "opened": True,
        "read_ok": first_ok,
        "frames": count,
        "measured_fps": count / elapsed if elapsed else 0.0,
        "reported_width": cap.get(cv2.CAP_PROP_FRAME_WIDTH),
        "reported_height": cap.get(cv2.CAP_PROP_FRAME_HEIGHT),
        "reported_fps": cap.get(cv2.CAP_PROP_FPS),
        "fourcc": int(cap.get(cv2.CAP_PROP_FOURCC)),
        "shape": shape,
        "backend": cap.getBackendName(),
    }
    cap.release()
    queue.put(entry)


def run_profile_probe(api, index, width, height, fps, duration, timeout):
    queue = mp.Queue()
    proc = mp.Process(target=_profile_probe_worker, args=(queue, api, index, width, height, fps, duration))
    proc.start()
    proc.join(timeout)
    if proc.is_alive():
        proc.terminate()
        proc.join(2)
        return {
            "requested": {"width": width, "height": height, "fps": fps},
            "opened": None,
            "timeout": True,
        }
    if not queue.empty():
        return queue.get()
    return {
        "requested": {"width": width, "height": height, "fps": fps},
        "opened": None,
        "error": f"worker exited with code {proc.exitcode}",
    }


def audio_smoke(args):
    import numpy as np
    import sounddevice as sd

    out = run_dir("audio-smoke")
    devices = sd.query_devices()
    hostapis = sd.query_hostapis()
    targets = []
    for dev in devices:
        if dev["max_input_channels"] <= 0:
            continue
        hostapi = hostapis[dev["hostapi"]]["name"]
        if args.hostapi and hostapi.lower() != args.hostapi.lower():
            continue
        if args.name and args.name.lower() not in dev["name"].lower():
            continue
        targets.append(dev)

    results = []
    for dev in targets:
        samplerate = int(args.samplerate or dev["default_samplerate"] or 48000)
        channels = min(int(dev["max_input_channels"]), args.channels)
        entry = {
            "index": dev["index"],
            "name": dev["name"],
            "hostapi": hostapis[dev["hostapi"]]["name"],
            "samplerate": samplerate,
            "channels": channels,
        }
        try:
            rec = sd.rec(
                int(args.duration * samplerate),
                samplerate=samplerate,
                channels=channels,
                dtype="float32",
                device=dev["index"],
                blocking=True,
            )
            entry["frames"] = int(rec.shape[0])
            entry["rms"] = [float(x) for x in np.sqrt(np.mean(rec * rec, axis=0))]
            entry["peak"] = [float(x) for x in np.max(np.abs(rec), axis=0)]
            entry["ok"] = True
        except Exception as exc:
            entry["ok"] = False
            entry["error"] = repr(exc)
        results.append(entry)

    manifest = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "duration": args.duration,
        "results": results,
    }
    write_json(out / "manifest.json", manifest)
    print(out)
    print(json.dumps(results, indent=2))


def main():
    parser = argparse.ArgumentParser(description="Probe Mimir calibration devices.")
    sub = parser.add_subparsers(required=True)

    p = sub.add_parser("discover")
    p.add_argument("--max-index", type=int, default=10)
    p.add_argument("--api", action="append", choices=["any", "dshow", "msmf"], default=["dshow", "msmf"])
    p.set_defaults(func=discover)

    p = sub.add_parser("snapshot")
    p.add_argument("--max-index", type=int, default=10)
    p.add_argument("--api", action="append", choices=["any", "dshow", "msmf"], default=["dshow", "msmf"])
    p.set_defaults(func=snapshot)

    p = sub.add_parser("mode-probe")
    p.add_argument("--api", choices=["any", "dshow", "msmf"], default="dshow")
    p.add_argument("--index", type=int, required=True)
    p.add_argument("--duration", type=float, default=1.0)
    p.add_argument("--profile-timeout", type=float, default=6.0)
    p.add_argument(
        "--profile",
        action="append",
        default=None,
        help="WIDTHxHEIGHTxFPS, repeatable. Example: --profile 320x240x120",
    )
    p.set_defaults(func=mode_probe)

    p = sub.add_parser("audio-smoke")
    p.add_argument("--duration", type=float, default=1.0)
    p.add_argument("--samplerate", type=int)
    p.add_argument("--channels", type=int, default=2)
    p.add_argument("--hostapi")
    p.add_argument("--name")
    p.set_defaults(func=audio_smoke)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
