#!/usr/bin/env python3
"""Capture a chirp/PS Move pulse sweep and score every available witness.

This is a receipt harness, not runtime calibration authority. It owns one
event schedule, emits chirps through the speakers, pulses the Nightwing PS Move
controllers with unique colors, records the configured cameras and mics, then
fits each detected signal back to the same schedule.
"""

from __future__ import annotations

import argparse
import base64
import ctypes
import json
import math
import shutil
import subprocess
import sys
import threading
import time
import wave
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image

from move_latency_probe import build_events, detect_audio_peaks, fit_schedule, generate_chirp
from nightwing_audio_sync_probe import AsioRecorder, estimate_clock_offset_ns, mono_copy_wav, remote_log


DEFAULT_FFMPEG = (
    Path.home()
    / "AppData/Local/Microsoft/WinGet/Packages/Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe"
    / "ffmpeg-8.1.1-full_build/bin/ffmpeg.exe"
)
DEFAULT_FFPLAY = DEFAULT_FFMPEG.with_name("ffplay.exe")
DEFAULT_FFPROBE = DEFAULT_FFMPEG.with_name("ffprobe.exe")

DEFAULT_KIYO_PRO_VIDEO = (
    r"@device_pnp_\\?\usb#vid_1532&pid_0e05&mi_00#9&3c07f79&0&0000"
    r"#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"
)
DEFAULT_KIYO_VIDEO = (
    r"@device_pnp_\\?\usb#vid_1532&pid_0e03&mi_00#a&3ba76de&0&0000"
    r"#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"
)

DEFAULT_ASIO_DLL = Path(__file__).resolve().parents[1] / "native" / "asio_capture" / "build" / "Release" / "mimir_asio_capture.dll"
DEFAULT_ASIO_CLSID = "{AC4D0455-50D7-4498-B3CD-9A41D130B759}"

REMOTE_MULTI_MOVE = r"""
import os
import sys
import time

moves = {}
for spec in sys.argv[1].split(","):
    if not spec:
        continue
    name, path = spec.split("=", 1)
    moves[name] = path

events = []
for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    if line == "go":
        break
    offset, name, r, g, b = line.split()
    events.append((float(offset), name, int(r), int(g), int(b)))

log = os.path.expanduser("~/.local/state/gamecult/codex-ssh-activity.log")
os.makedirs(os.path.dirname(log), exist_ok=True)
with open(log, "a", encoding="utf-8") as f:
    f.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S%z')} Codex: Mimir sync sweep Move receiver armed for {len(moves)} Moves.\n")

def write_rgb(path, r, g, b):
    with open(path, "wb", buffering=0) as device:
        device.write(bytes([0x06, 0, max(0, min(255, r)), max(0, min(255, g)), max(0, min(255, b)), 0, 0, 0, 0]))

for path in moves.values():
    write_rgb(path, 0, 0, 0)

start = time.perf_counter()
for offset, name, r, g, b in events:
    wait = start + offset - time.perf_counter()
    if wait > 0:
        time.sleep(wait)
    path = moves.get(name)
    if path:
        write_rgb(path, r, g, b)

time.sleep(0.10)
for path in moves.values():
    write_rgb(path, 0, 0, 0)
"""


@dataclass(frozen=True)
class VideoSensor:
    name: str
    device: str
    width: int
    height: int
    fps: int


@dataclass(frozen=True)
class AudioSensor:
    name: str
    device: str


@dataclass(frozen=True)
class NightwingEye:
    name: str
    device: str
    width: int
    height: int
    fps: int


def parse_video(value: str) -> VideoSensor:
    name, rest = value.split("=", 1)
    device, mode = rest.rsplit(":", 1)
    size, fps = mode.split("@", 1)
    width, height = size.lower().split("x", 1)
    return VideoSensor(name, device, int(width), int(height), int(float(fps)))


def parse_audio(value: str) -> AudioSensor:
    name, device = value.split("=", 1)
    return AudioSensor(name, device)


def parse_nw_eye(value: str) -> NightwingEye:
    name, rest = value.split("=", 1)
    device, mode = rest.rsplit(":", 1)
    size, fps = mode.split("@", 1)
    width, height = size.lower().split("x", 1)
    return NightwingEye(name, device, int(width), int(height), int(float(fps)))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out-dir", default="artifacts/runtime/sync-sweep")
    parser.add_argument("--ffmpeg", default=str(DEFAULT_FFMPEG))
    parser.add_argument("--ffplay", default=str(DEFAULT_FFPLAY))
    parser.add_argument("--ffprobe", default=str(DEFAULT_FFPROBE))
    parser.add_argument("--ssh-target", default="nightwing")
    parser.add_argument("--nightwing-mic-device", default="hw:0,0")
    parser.add_argument("--nightwing-rate", type=int, default=48000)
    parser.add_argument("--nightwing-channels", type=int, default=2)
    parser.add_argument("--skip-nightwing-mic", action="store_true")
    parser.add_argument("--move", action="append", default=[
        "move-a=/dev/hidraw2:#ff2a00",
        "move-b=/dev/hidraw3:#00a8ff",
    ])
    parser.add_argument("--video", action="append", default=[
        f"kiyo-pro={DEFAULT_KIYO_PRO_VIDEO}:640x480@30",
        f"kiyo={DEFAULT_KIYO_VIDEO}:640x480@30",
    ])
    parser.add_argument("--nightwing-eye", action="append", default=[
        "nw-builtin-cam=/dev/video0:640x480@30",
        "nw-eye-0=/dev/video2:320x240@187",
        "nw-eye-1=/dev/video3:320x240@187",
    ])
    parser.add_argument("--audio", action="append", default=[
        "kiyo-pro-mic=Microphone (7- Razer Kiyo Pro)",
        "kiyo-mic=Microphone (4- Razer Kiyo)",
        "focusrite-dshow=Analogue 1 + 2 (2- Focusrite USB Audio)",
    ])
    parser.add_argument("--asio-dll", default=str(DEFAULT_ASIO_DLL))
    parser.add_argument("--asio-clsid", default=DEFAULT_ASIO_CLSID)
    parser.add_argument("--asio-channels", default="0,1,2,3")
    parser.add_argument("--skip-asio", action="store_true")
    parser.add_argument("--duration", type=float, default=9.0)
    parser.add_argument("--capture-pad", type=float, default=2.0)
    parser.add_argument("--lead", type=float, default=1.2)
    parser.add_argument("--interval", type=float, default=0.72)
    parser.add_argument("--pulses", type=int, default=10)
    parser.add_argument("--schedule", choices=("uniform", "debruijn"), default="debruijn")
    parser.add_argument("--alphabet-size", type=int, default=8)
    parser.add_argument("--order", type=int, default=3)
    parser.add_argument("--chirp-ms", type=float, default=70.0)
    parser.add_argument("--pulse-ms", type=float, default=95.0)
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def move_specs_and_events(args: argparse.Namespace, events: list[dict]) -> tuple[str, str]:
    moves: list[tuple[str, str, tuple[int, int, int]]] = []
    for spec in args.move:
        name_path, color_text = spec.split(":", 1)
        name, path = name_path.split("=", 1)
        color_text = color_text.lstrip("#")
        color = tuple(int(color_text[index:index + 2], 16) for index in (0, 2, 4))
        moves.append((name, path, color))  # type: ignore[arg-type]
    move_specs = ",".join(f"{name}={path}" for name, path, _color in moves)
    lines: list[str] = []
    pulse_s = args.pulse_ms * 0.001
    for event_index, event in enumerate(events):
        offset = float(event["offset"])
        for move_index, (name, _path, color) in enumerate(moves):
            scale = 1.0 if move_index == event_index % max(1, len(moves)) else 0.45
            r, g, b = (int(channel * scale) for channel in color)
            lines.append(f"{offset:.6f} {name} {r} {g} {b}")
            lines.append(f"{offset + pulse_s:.6f} {name} 0 0 0")
    return move_specs, "\n".join(lines) + "\ngo\n"


def start_move_receiver(args: argparse.Namespace, events: list[dict]) -> subprocess.Popen | None:
    if not args.move:
        return None
    move_specs, stdin_text = move_specs_and_events(args, events)
    remote_b64 = base64.b64encode(REMOTE_MULTI_MOVE.encode("utf-8")).decode("ascii")
    remote_cmd = (
        "tmp=$(mktemp); "
        f"printf '%s' '{remote_b64}' | base64 -d > \"$tmp\"; "
        f"python3 \"$tmp\" '{move_specs}'; "
        "status=$?; rm -f \"$tmp\"; exit $status"
    )
    proc = subprocess.Popen(["ssh", "-o", "BatchMode=yes", args.ssh_target, remote_cmd], stdin=subprocess.PIPE, text=True)
    assert proc.stdin is not None
    proc.stdin.write(stdin_text)
    proc.stdin.flush()
    proc.stdin.close()
    return proc


def start_video_capture(args: argparse.Namespace, sensor: VideoSensor, out_dir: Path) -> subprocess.Popen:
    log = out_dir / f"{sensor.name}.ffmpeg.err.log"
    path = out_dir / f"{sensor.name}.mkv"
    cmd = [
        args.ffmpeg,
        "-hide_banner",
        "-y",
        "-thread_queue_size",
        "1024",
        "-rtbufsize",
        "512M",
        "-f",
        "dshow",
        "-video_size",
        f"{sensor.width}x{sensor.height}",
        "-framerate",
        str(sensor.fps),
        "-i",
        f"video={sensor.device}",
        "-t",
        f"{args.duration + args.capture_pad:.3f}",
        "-an",
        "-c:v",
        "ffv1",
        "-level",
        "3",
        str(path),
    ]
    return subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=log.open("w", encoding="utf-8", errors="replace"))


def start_audio_capture(args: argparse.Namespace, sensor: AudioSensor, out_dir: Path) -> subprocess.Popen:
    log = out_dir / f"{sensor.name}.ffmpeg.err.log"
    path = out_dir / f"{sensor.name}.wav"
    cmd = [
        args.ffmpeg,
        "-hide_banner",
        "-y",
        "-thread_queue_size",
        "1024",
        "-f",
        "dshow",
        "-i",
        f"audio={sensor.device}",
        "-t",
        f"{args.duration + args.capture_pad:.3f}",
        "-ac",
        "1",
        "-ar",
        str(args.sample_rate),
        str(path),
    ]
    return subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=log.open("w", encoding="utf-8", errors="replace"))


def start_nightwing_mic(args: argparse.Namespace, out_dir: Path) -> subprocess.Popen:
    remote_log(args.ssh_target, "Mimir sync sweep arming Nightwing mic recorder.")
    return subprocess.Popen(
        [
            "ssh",
            args.ssh_target,
            "python3",
            "/tmp/nw_chirp_hint.py",
            "--device",
            args.nightwing_mic_device,
            "--rate",
            str(args.nightwing_rate),
            "--channels",
            str(args.nightwing_channels),
            "--seconds",
            f"{args.duration + args.capture_pad:.3f}",
            "--events-json",
            "/tmp/mimir-sync-sweep-events.json",
            "--chirp-ms",
            str(args.chirp_ms),
            "--interval",
            str(args.interval),
            "--out",
            "/tmp/mimir-sync-sweep-nightwing-mic.json",
        ],
        stdout=(out_dir / "nightwing-mic.out.log").open("w", encoding="utf-8", errors="replace"),
        stderr=(out_dir / "nightwing-mic.err.log").open("w", encoding="utf-8", errors="replace"),
        text=True,
    )


def start_nightwing_eye(args: argparse.Namespace, sensor: NightwingEye, out_dir: Path) -> subprocess.Popen:
    remote_json = f"/tmp/mimir-sync-sweep-{sensor.name}.json"
    move_names = ",".join(spec.split("=", 1)[0] for spec in args.move)
    return subprocess.Popen(
        [
            "ssh",
            args.ssh_target,
            "python3",
            "/tmp/nw_move_hint.py",
            "--device",
            sensor.device,
            "--source-id",
            sensor.name,
            "--width",
            str(sensor.width),
            "--height",
            str(sensor.height),
            "--fps",
            str(sensor.fps),
            "--seconds",
            f"{args.duration + args.capture_pad:.3f}",
            "--events-json",
            "/tmp/mimir-sync-sweep-events.json",
            "--moves",
            move_names,
            "--pulse-ms",
            str(args.pulse_ms),
            "--out",
            remote_json,
        ],
        stdout=(out_dir / f"{sensor.name}.out.log").open("w", encoding="utf-8", errors="replace"),
        stderr=(out_dir / f"{sensor.name}.err.log").open("w", encoding="utf-8", errors="replace"),
        text=True,
    )


def stage_nightwing_tools(args: argparse.Namespace, out_dir: Path) -> None:
    tool_names = ["nightwing_alsa_capture_probe.py", "nw_eye_cap.py", "nw_move_hint.py", "nw_chirp_hint.py"]
    for name in tool_names:
        shutil.copy2(Path(__file__).with_name(name), out_dir / name)
    files = [str(out_dir / name) for name in tool_names] + [str(out_dir / "event-schedule.json")]
    subprocess.run(["scp", *files, f"{args.ssh_target}:/tmp/"], check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    subprocess.run(["ssh", args.ssh_target, "cp /tmp/event-schedule.json /tmp/mimir-sync-sweep-events.json"], check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)


def write_asio_channels(recorder: AsioRecorder, out_dir: Path) -> list[Path]:
    paths: list[Path] = []
    for channel, values in recorder.samples.items():
        samples = np.clip(np.asarray(values, dtype=np.float32), -1.0, 1.0)
        path = out_dir / f"asio-ch{channel}.wav"
        with wave.open(str(path), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(recorder.sample_rate)
            wav.writeframes((samples * 32767.0).astype("<i2").tobytes())
        paths.append(path)
    return paths


def extract_video_frames(args: argparse.Namespace, out_dir: Path, sensor: VideoSensor) -> tuple[Path, list[float]]:
    video_path = out_dir / f"{sensor.name}.mkv"
    frames_dir = out_dir / f"{sensor.name}-frames"
    if frames_dir.exists():
        shutil.rmtree(frames_dir)
    frames_dir.mkdir(parents=True)
    subprocess.run(
        [args.ffmpeg, "-hide_banner", "-y", "-i", str(video_path), "-map", "0:v:0", "-vsync", "0", str(frames_dir / "frame_%06d.png")],
        stdout=subprocess.PIPE,
        stderr=(out_dir / f"{sensor.name}.extract.err.log").open("w", encoding="utf-8", errors="replace"),
        text=True,
        check=True,
    )
    probe = subprocess.run(
        [
            args.ffprobe,
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "frame=best_effort_timestamp_time",
            "-of",
            "json",
            str(video_path),
        ],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    data = json.loads(probe.stdout)
    times = [float(frame["best_effort_timestamp_time"]) for frame in data.get("frames", []) if "best_effort_timestamp_time" in frame]
    return frames_dir, times


def detect_video_peaks(frames_dir: Path, frame_times: list[float], interval: float, fps: int, pulses: int) -> tuple[list[float], dict]:
    frame_paths = sorted(frames_dir.glob("frame_*.png"))
    scores: list[float] = []
    previous: np.ndarray | None = None
    for path in frame_paths:
        arr = np.asarray(Image.open(path).convert("L"), dtype=np.float32)
        if previous is None:
            scores.append(0.0)
        else:
            positive_delta = np.maximum(arr - previous, 0.0)
            hot_delta = float(np.percentile(positive_delta, 99.92))
            bright_tail = float(np.percentile(arr, 99.90))
            scores.append(hot_delta + 0.025 * bright_tail)
        previous = arr
    if not scores:
        return [], {"frames": 0, "score_max": None}
    times = frame_times[: len(scores)] or [index / max(1, fps) for index in range(len(scores))]
    signal = np.maximum(np.asarray(scores, dtype=np.float32) - float(np.percentile(scores, 20)), 0.0)
    threshold = max(float(np.percentile(signal, 92)), float(np.max(signal)) * 0.38)
    min_gap = max(1, int(interval * 0.50 * fps))
    peaks: list[int] = []
    work = signal.copy()
    for _ in range(pulses):
        index = int(np.argmax(work))
        if work[index] <= threshold:
            break
        peaks.append(index)
        lo = max(0, index - min_gap)
        hi = min(work.size, index + min_gap)
        work[lo:hi] = 0.0
    return sorted(times[index] for index in peaks), {
        "frames": len(scores),
        "score_min": float(np.min(scores)),
        "score_max": float(np.max(scores)),
        "threshold": threshold,
    }


def detect_raw_luma_peaks(raw_path: Path, frame_times: list[float], width: int, height: int, interval: float, fps: int, pulses: int) -> tuple[list[float], dict]:
    raw = raw_path.read_bytes()
    frame_bytes = width * height
    frame_count = len(raw) // frame_bytes
    if frame_count <= 0:
        return [], {"frames": 0, "score_max": None}
    scores: list[float] = []
    previous: np.ndarray | None = None
    for index in range(frame_count):
        frame = np.frombuffer(raw[index * frame_bytes:(index + 1) * frame_bytes], dtype=np.uint8).astype(np.float32)
        if previous is None:
            scores.append(0.0)
        else:
            positive_delta = np.maximum(frame - previous, 0.0)
            scores.append(float(np.percentile(positive_delta, 99.95)) + 0.03 * float(np.percentile(frame, 99.92)))
        previous = frame
    times = frame_times[:frame_count] or [index / max(1, fps) for index in range(frame_count)]
    signal = np.maximum(np.asarray(scores, dtype=np.float32) - float(np.percentile(scores, 20)), 0.0)
    threshold = max(float(np.percentile(signal, 96)), float(np.max(signal)) * 0.44)
    min_gap = max(1, int(interval * 0.50 * fps))
    peaks: list[int] = []
    work = signal.copy()
    for _ in range(pulses):
        index = int(np.argmax(work))
        if work[index] <= threshold:
            break
        peaks.append(index)
        lo = max(0, index - min_gap)
        hi = min(work.size, index + min_gap)
        work[lo:hi] = 0.0
    return sorted(times[index] for index in peaks), {
        "frames": frame_count,
        "score_min": float(np.min(scores)),
        "score_max": float(np.max(scores)),
        "threshold": threshold,
        "raw_path": str(raw_path),
    }


def score_sensor(name: str, kind: str, peaks: list[float], events: list[dict], args: argparse.Namespace, extra: dict | None = None) -> dict:
    shift, matches = fit_schedule(peaks, events, args)
    residuals = [float(match["residual_ms"]) for match in matches if match.get("residual_ms") is not None]
    matched = len(residuals)
    required = max(2, int(math.ceil(args.pulses * 0.45)))
    summary = {
        "name": name,
        "kind": kind,
        "ok": shift is not None and matched >= required,
        "peaks": len(peaks),
        "matched": matched,
        "schedule_shift_ms": shift * 1000.0 if shift is not None else None,
        "median_abs_residual_ms": float(np.median(np.abs(residuals))) if residuals else None,
        "peak_times_s": peaks,
        "matches": matches,
    }
    if extra:
        summary.update(extra)
    return summary


def main() -> int:
    args = parse_args()
    out_dir = Path(args.out_dir)
    if not out_dir.is_absolute():
        out_dir = Path.cwd() / out_dir
    if out_dir.exists() and any(out_dir.iterdir()):
        out_dir = out_dir.with_name(out_dir.name + "-" + time.strftime("%Y%m%d-%H%M%S"))
    out_dir.mkdir(parents=True, exist_ok=True)

    videos = [parse_video(value) for value in args.video]
    nw_eyes = [parse_nw_eye(value) for value in args.nightwing_eye]
    audios = [parse_audio(value) for value in args.audio]
    events = build_events(args)
    chirp_path = out_dir / "chirp-train.wav"
    prototype = generate_chirp(chirp_path, args, events)
    (out_dir / "event-schedule.json").write_text(json.dumps(events, indent=2), encoding="utf-8")

    if args.dry_run:
        print(json.dumps({"out_dir": str(out_dir), "events": events}, indent=2))
        return 0

    if nw_eyes or not args.skip_nightwing_mic:
        stage_nightwing_tools(args, out_dir)

    sensor_results: list[dict] = []
    started: list[tuple[str, subprocess.Popen]] = []
    for sensor in videos:
        try:
            started.append((sensor.name, start_video_capture(args, sensor, out_dir)))
        except Exception as ex:
            sensor_results.append({"name": sensor.name, "kind": "video", "ok": False, "error": str(ex)})
    for sensor in audios:
        try:
            started.append((sensor.name, start_audio_capture(args, sensor, out_dir)))
        except Exception as ex:
            sensor_results.append({"name": sensor.name, "kind": "audio", "ok": False, "error": str(ex)})

    nw_proc: subprocess.Popen | None = None
    if not args.skip_nightwing_mic:
        try:
            nw_proc = start_nightwing_mic(args, out_dir)
        except Exception as ex:
            sensor_results.append({"name": "nightwing-mic", "kind": "audio", "ok": False, "error": str(ex)})

    nw_eye_procs: list[tuple[NightwingEye, subprocess.Popen]] = []
    for sensor in nw_eyes:
        try:
            nw_eye_procs.append((sensor, start_nightwing_eye(args, sensor, out_dir)))
        except Exception as ex:
            sensor_results.append({"name": sensor.name, "kind": "nightwing-video", "ok": False, "error": str(ex)})

    asio: AsioRecorder | None = None
    asio_thread: threading.Thread | None = None
    if not args.skip_asio:
        try:
            channels = {int(part.strip()) for part in args.asio_channels.split(",") if part.strip()}
            asio = AsioRecorder(Path(args.asio_dll), args.asio_clsid, args.sample_rate, channels)
            asio_thread = threading.Thread(target=asio.record_for, args=(args.duration + args.capture_pad,), daemon=True)
            asio_thread.start()
        except Exception as ex:
            sensor_results.append({"name": "asio", "kind": "audio", "ok": False, "error": str(ex)})
            asio = None

    time.sleep(0.8)
    move_proc = start_move_receiver(args, events)
    play_started_ns = time.time_ns()
    play = subprocess.run([args.ffplay, "-nodisp", "-autoexit", "-loglevel", "quiet", str(chirp_path)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    for name, proc in started:
        try:
            proc.wait(timeout=args.duration + args.capture_pad + 5.0)
        except subprocess.TimeoutExpired:
            proc.kill()
            sensor_results.append({"name": name, "kind": "capture", "ok": False, "error": "capture timeout"})
    if move_proc is not None:
        try:
            move_proc.wait(timeout=args.duration + 5.0)
        except subprocess.TimeoutExpired:
            move_proc.kill()
    if asio_thread is not None:
        asio_thread.join(timeout=args.duration + args.capture_pad + 2.0)
    if nw_proc is not None:
        try:
            nw_proc.wait(timeout=args.duration + args.capture_pad + 5.0)
        except subprocess.TimeoutExpired:
            nw_proc.kill()
    for sensor, proc in nw_eye_procs:
        try:
            proc.wait(timeout=args.duration + args.capture_pad + 5.0)
        except subprocess.TimeoutExpired:
            proc.kill()
            sensor_results.append({"name": sensor.name, "kind": "nightwing-video", "ok": False, "error": "remote capture timeout"})

    if asio is not None:
        try:
            for path in write_asio_channels(asio, out_dir):
                peaks = detect_audio_peaks(path, prototype, args, events)
                sensor_results.append(score_sensor(path.stem, "asio-audio", peaks, events, args, {"path": str(path)}))
        finally:
            asio.close()

    if nw_proc is not None:
        subprocess.run(["scp", f"{args.ssh_target}:/tmp/mimir-sync-sweep-nightwing-mic.json", str(out_dir / "nightwing-mic.json")], check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if (out_dir / "nightwing-mic.json").exists():
            try:
                hint = json.loads((out_dir / "nightwing-mic.json").read_text(encoding="utf-8"))
                enough = int(hint.get("matched", 0)) >= max(2, int(math.ceil(args.pulses * 0.35)))
                sensor_results.append({
                    "name": "nightwing-mic",
                    "kind": "nightwing-audio-hint",
                    "ok": nw_proc.returncode == 0 and enough,
                    "matched": hint.get("matched", 0),
                    "schedule_shift_ms": hint.get("schedule_shift_ms"),
                    "median_abs_residual_ms": hint.get("median_abs_residual_ms"),
                    "path": str(out_dir / "nightwing-mic.json"),
                    "hint": hint,
                    "error": None if nw_proc.returncode == 0 and enough else f"remote recorder exited {nw_proc.returncode}; insufficient matched pulses",
                })
            except Exception as ex:
                sensor_results.append({"name": "nightwing-mic", "kind": "nightwing-audio-hint", "ok": False, "error": str(ex)})
        else:
            sensor_results.append({"name": "nightwing-mic", "kind": "nightwing-audio-hint", "ok": False, "error": f"remote recorder exited {nw_proc.returncode}; no hint json"})

    for sensor, proc in nw_eye_procs:
        if proc.returncode != 0:
            sensor_results.append({"name": sensor.name, "kind": "nightwing-video", "ok": False, "error": f"remote capture exited {proc.returncode}"})
            continue
        remote_base = f"/tmp/mimir-sync-sweep-{sensor.name}"
        local_json = out_dir / f"{sensor.name}.json"
        subprocess.run(["scp", f"{args.ssh_target}:{remote_base}.json", str(local_json)], check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            hint = json.loads(local_json.read_text(encoding="utf-8"))
            stable = int(hint.get("stable_event_count", 0))
            observations = hint.get("observations", [])
            residuals = [abs(float(item["schedule_residual_ms"])) for item in observations if "schedule_residual_ms" in item]
            sensor_results.append({
                "name": sensor.name,
                "kind": "nightwing-move-hint",
                "ok": stable >= max(2, int(math.ceil(args.pulses * 0.35))),
                "matched": stable,
                "schedule_shift_ms": float(np.median([float(item["observed_time_s"]) - float(item["schedule_offset_s"]) for item in observations])) * 1000.0 if observations else None,
                "median_abs_residual_ms": float(np.median(residuals)) if residuals else None,
                "path": str(local_json),
                "hint": hint,
            })
        except Exception as ex:
            sensor_results.append({"name": sensor.name, "kind": "nightwing-move-hint", "ok": False, "error": str(ex)})

    for sensor in audios:
        path = out_dir / f"{sensor.name}.wav"
        if not path.exists():
            sensor_results.append({"name": sensor.name, "kind": "dshow-audio", "ok": False, "error": "wav missing"})
            continue
        try:
            peaks = detect_audio_peaks(path, prototype, args, events)
            sensor_results.append(score_sensor(sensor.name, "dshow-audio", peaks, events, args, {"path": str(path)}))
        except Exception as ex:
            sensor_results.append({"name": sensor.name, "kind": "dshow-audio", "ok": False, "error": str(ex)})

    for sensor in videos:
        path = out_dir / f"{sensor.name}.mkv"
        if not path.exists():
            sensor_results.append({"name": sensor.name, "kind": "video", "ok": False, "error": "capture missing"})
            continue
        try:
            frames_dir, times = extract_video_frames(args, out_dir, sensor)
            peaks, stats = detect_video_peaks(frames_dir, times, args.interval, sensor.fps, args.pulses)
            sensor_results.append(score_sensor(sensor.name, "video", peaks, events, args, {"path": str(path), "stats": stats}))
        except Exception as ex:
            sensor_results.append({"name": sensor.name, "kind": "video", "ok": False, "error": str(ex)})

    reference = next((item for item in sensor_results if item.get("ok") and item.get("name") == "asio-ch2"), None)
    if reference is None:
        reference = next((item for item in sensor_results if item.get("ok") and item.get("kind") == "asio-audio"), None)
    reference_shift = reference.get("schedule_shift_ms") if reference else None
    for item in sensor_results:
        shift = item.get("schedule_shift_ms")
        item["offset_from_reference_ms"] = (float(shift) - float(reference_shift)) if shift is not None and reference_shift is not None else None

    summary = {
        "kind": "mimir.chirp_move_sync_sweep.v1",
        "out_dir": str(out_dir),
        "created_unix": time.time(),
        "play_started_wall_ns": play_started_ns,
        "ffplay_returncode": play.returncode,
        "reference": reference.get("name") if reference else None,
        "reference_schedule_shift_ms": reference_shift,
        "events": events,
        "sensors": sensor_results,
        "valid_sensor_count": sum(1 for item in sensor_results if item.get("ok")),
        "note": "Schedule shifts are per-witness receipt fits. Offsets are relative to the selected ASIO witness when available; final runtime calibration still belongs in native timestamped Mimir/Fensalir paths.",
    }
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=2, sort_keys=True), encoding="utf-8")
    print(json.dumps(summary, indent=2, sort_keys=True))
    return 0 if summary["valid_sensor_count"] > 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
