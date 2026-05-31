#!/usr/bin/env python3
"""Compare Nightwing integrated mic chirp timing against Starfire Scarlett loopback."""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import wave
from pathlib import Path

import numpy as np

from move_latency_probe import build_events, detect_audio_peaks, fit_schedule, generate_chirp


DEFAULT_ASIO_DLL = Path(__file__).resolve().parents[1] / "native" / "asio_capture" / "build" / "Release" / "mimir_asio_capture.dll"
DEFAULT_ASIO_CLSID = "{AC4D0455-50D7-4498-B3CD-9A41D130B759}"
DEFAULT_FFPLAY = (
    Path.home()
    / "AppData/Local/Microsoft/WinGet/Packages/Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe"
    / "ffmpeg-8.1.1-full_build/bin/ffplay.exe"
)


class AsioRecorder:
    def __init__(self, dll_path: Path, clsid: str, sample_rate: int, channels: set[int]) -> None:
        self.dll = ctypes.WinDLL(str(dll_path))
        self.dll.mimir_asio_create.argtypes = [
            ctypes.c_char_p,
            ctypes.c_double,
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
        ]
        self.dll.mimir_asio_create.restype = ctypes.c_void_p
        self.dll.mimir_asio_start.argtypes = [ctypes.c_void_p]
        self.dll.mimir_asio_start.restype = ctypes.c_int
        self.dll.mimir_asio_read.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_longlong),
            ctypes.POINTER(ctypes.c_ulonglong),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_float),
            ctypes.c_int,
        ]
        self.dll.mimir_asio_read.restype = ctypes.c_int
        self.dll.mimir_asio_destroy.argtypes = [ctypes.c_void_p]
        self.dll.mimir_asio_destroy.restype = None
        actual_rate = ctypes.c_int()
        input_count = ctypes.c_int()
        max_frames = ctypes.c_int()
        self.handle = self.dll.mimir_asio_create(
            clsid.encode("utf-8"),
            float(sample_rate),
            ctypes.byref(actual_rate),
            ctypes.byref(input_count),
            ctypes.byref(max_frames),
        )
        if not self.handle:
            raise RuntimeError("Could not create ASIO capture")
        self.sample_rate = int(actual_rate.value)
        self.input_count = int(input_count.value)
        self.max_frames = int(max_frames.value)
        self.channels = channels
        self.buffer = (ctypes.c_float * self.max_frames)()
        self.samples: dict[int, list[float]] = {channel: [] for channel in sorted(channels)}
        self.first_read_wall_ns: int | None = None
        self.open_wall_ns = time.time_ns()
        self.read_calls = 0
        self.running = False
        if not self.dll.mimir_asio_start(self.handle):
            self.close()
            raise RuntimeError("Could not start ASIO capture")

    def record_for(self, seconds: float) -> None:
        self.running = True
        deadline = time.monotonic() + seconds
        channel = ctypes.c_int()
        timestamp_ns = ctypes.c_longlong()
        sequence = ctypes.c_ulonglong()
        frame_count = ctypes.c_int()
        while time.monotonic() < deadline:
            ok = self.dll.mimir_asio_read(
                self.handle,
                ctypes.byref(channel),
                ctypes.byref(timestamp_ns),
                ctypes.byref(sequence),
                ctypes.byref(frame_count),
                self.buffer,
                self.max_frames,
            )
            if not ok:
                time.sleep(0.001)
                continue
            self.read_calls += 1
            if self.first_read_wall_ns is None:
                self.first_read_wall_ns = time.time_ns()
            ch = int(channel.value)
            frames = max(0, min(int(frame_count.value), self.max_frames))
            if ch not in self.samples or frames == 0:
                continue
            self.samples[ch].extend(float(self.buffer[index]) for index in range(frames))
        self.running = False

    def mono(self) -> np.ndarray:
        arrays = [np.asarray(values, dtype=np.float32) for values in self.samples.values() if values]
        if not arrays:
            return np.zeros(0, dtype=np.float32)
        length = min(array.size for array in arrays)
        if length <= 0:
            return np.zeros(0, dtype=np.float32)
        stacked = np.stack([array[:length] for array in arrays])
        return np.mean(stacked, axis=0).astype(np.float32)

    def write_wav(self, path: Path) -> None:
        samples = np.clip(self.mono(), -1.0, 1.0)
        path.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(path), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(self.sample_rate)
            wav.writeframes((samples * 32767.0).astype("<i2").tobytes())

    def close(self) -> None:
        if self.handle:
            self.dll.mimir_asio_destroy(self.handle)
            self.handle = None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ssh-target", default="nightwing")
    parser.add_argument("--remote-device", default="hw:0,0")
    parser.add_argument("--remote-rate", type=int, default=48000)
    parser.add_argument("--remote-channels", type=int, default=2)
    parser.add_argument("--asio-dll", default=str(DEFAULT_ASIO_DLL))
    parser.add_argument("--asio-clsid", default=DEFAULT_ASIO_CLSID)
    parser.add_argument("--asio-channels", default="2,3")
    parser.add_argument("--ffplay", default=str(DEFAULT_FFPLAY))
    parser.add_argument("--out-dir", default="artifacts/runtime/nightwing-audio-sync")
    parser.add_argument("--duration", type=float, default=8.0)
    parser.add_argument("--capture-pad", type=float, default=2.0)
    parser.add_argument("--lead", type=float, default=1.2)
    parser.add_argument("--interval", type=float, default=0.8)
    parser.add_argument("--pulses", type=int, default=8)
    parser.add_argument("--schedule", choices=("uniform", "debruijn"), default="debruijn")
    parser.add_argument("--alphabet-size", type=int, default=8)
    parser.add_argument("--order", type=int, default=3)
    parser.add_argument("--chirp-ms", type=float, default=80.0)
    parser.add_argument("--sample-rate", type=int, default=48000)
    return parser.parse_args()


def mono_copy_wav(src: Path, dst: Path) -> None:
    with wave.open(str(src), "rb") as wav:
        rate = wav.getframerate()
        channels = wav.getnchannels()
        pcm = wav.readframes(wav.getnframes())
    samples = np.frombuffer(pcm, dtype="<i2").reshape((-1, channels)).astype(np.float32)
    mono = np.mean(samples, axis=1)
    dst.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(dst), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(np.clip(mono, -32768, 32767).astype("<i2").tobytes())


def estimate_clock_offset_ns(ssh_target: str, samples: int = 5) -> dict[str, float | int]:
    offsets: list[float] = []
    rtts: list[float] = []
    for _ in range(samples):
        start = time.time_ns()
        proc = subprocess.run(
            ["ssh", ssh_target, "python3 -c 'import time; print(time.time_ns())'"],
            check=True,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        end = time.time_ns()
        remote = int(proc.stdout.strip())
        midpoint = (start + end) / 2.0
        offsets.append(remote - midpoint)
        rtts.append(end - start)
    best = min(range(len(rtts)), key=lambda index: rtts[index])
    return {
        "remote_minus_local_clock_offset_ns": offsets[best],
        "best_rtt_ns": int(rtts[best]),
        "median_offset_ns": float(np.median(offsets)),
        "median_rtt_ns": float(np.median(rtts)),
    }


def remote_log(ssh_target: str, message: str) -> None:
    script = (
        "from pathlib import Path\n"
        "import datetime\n"
        "p=Path.home()/'.local/state/gamecult/codex-ssh-activity.log'\n"
        "p.parent.mkdir(parents=True, exist_ok=True)\n"
        f"p.open('a', encoding='utf-8').write(datetime.datetime.now().astimezone().isoformat() + ' Codex: {message}\\n')\n"
    )
    subprocess.run(["ssh", ssh_target, "python3", "-"], input=script, text=True, check=False)


def main() -> int:
    args = parse_args()
    out_dir = Path(args.out_dir)
    if out_dir.exists():
        suffix = time.strftime("%Y%m%d-%H%M%S")
        out_dir = out_dir.with_name(out_dir.name + "-" + suffix)
    out_dir.mkdir(parents=True, exist_ok=True)

    events = build_events(args)
    chirp_path = out_dir / "chirp-train.wav"
    prototype = generate_chirp(chirp_path, args, events)
    (out_dir / "event-schedule.json").write_text(json.dumps(events, indent=2), encoding="utf-8")
    shutil.copy2(Path(__file__).with_name("nightwing_alsa_capture_probe.py"), out_dir / "nightwing_alsa_capture_probe.py")

    clock = estimate_clock_offset_ns(args.ssh_target)
    remote_log(args.ssh_target, "Nightwing audio sync chirp probe arming integrated mic recorder.")
    remote_wav = "/tmp/nightwing-audio-sync-mic.wav"
    remote_json = "/tmp/nightwing-audio-sync-mic.json"
    remote_seconds = args.duration + args.capture_pad
    remote_cmd = [
        "ssh",
        args.ssh_target,
        "python3",
        "/tmp/nightwing_alsa_capture_probe.py",
        "--device",
        args.remote_device,
        "--rate",
        str(args.remote_rate),
        "--channels",
        str(args.remote_channels),
        "--seconds",
        f"{remote_seconds:.3f}",
        "--wav",
        remote_wav,
        "--json",
        remote_json,
        "--visible-log",
    ]
    remote = subprocess.Popen(remote_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    time.sleep(0.5)

    channels = {int(part.strip()) for part in args.asio_channels.split(",") if part.strip()}
    asio = AsioRecorder(Path(args.asio_dll), args.asio_clsid, args.sample_rate, channels)
    asio_thread = threading.Thread(target=asio.record_for, args=(remote_seconds,), daemon=True)
    asio_thread.start()
    time.sleep(0.2)
    play_wall_ns = time.time_ns()
    play = subprocess.run(
        [args.ffplay, "-nodisp", "-autoexit", "-loglevel", "quiet", str(chirp_path)],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    asio_thread.join(timeout=remote_seconds + 2.0)
    asio.write_wav(out_dir / "starfire-loopback.wav")
    local_first_wall_ns = asio.first_read_wall_ns
    asio.close()

    remote_stdout, remote_stderr = remote.communicate(timeout=remote_seconds + 5.0)
    (out_dir / "nightwing-recorder.out.log").write_text(remote_stdout, encoding="utf-8", errors="replace")
    (out_dir / "nightwing-recorder.err.log").write_text(remote_stderr, encoding="utf-8", errors="replace")
    subprocess.run(["scp", f"{args.ssh_target}:{remote_wav}", str(out_dir / "nightwing-integrated-mic.wav")], check=True)
    subprocess.run(["scp", f"{args.ssh_target}:{remote_json}", str(out_dir / "nightwing-integrated-mic.json")], check=True)
    mono_copy_wav(out_dir / "nightwing-integrated-mic.wav", out_dir / "nightwing-integrated-mic-mono.wav")

    local_peaks = detect_audio_peaks(out_dir / "starfire-loopback.wav", prototype, args, events)
    remote_peaks = detect_audio_peaks(out_dir / "nightwing-integrated-mic-mono.wav", prototype, args, events)
    local_shift, local_matches = fit_schedule(local_peaks, events, args)
    remote_shift, remote_matches = fit_schedule(remote_peaks, events, args)
    remote_stats = json.loads((out_dir / "nightwing-integrated-mic.json").read_text(encoding="utf-8"))
    remote_first_wall_ns = remote_stats.get("first_read_wall_ns")
    offset_ns = clock["remote_minus_local_clock_offset_ns"]

    schedule_delta_ms = None
    wall_delta_ms = None
    if local_shift is not None and remote_shift is not None:
        schedule_delta_ms = (remote_shift - local_shift) * 1000.0
    if local_shift is not None and remote_shift is not None and local_first_wall_ns is not None and remote_first_wall_ns is not None:
        local_event_ns = int(local_first_wall_ns + local_shift * 1_000_000_000.0)
        remote_event_local_ns = int(remote_first_wall_ns - float(offset_ns) + remote_shift * 1_000_000_000.0)
        wall_delta_ms = (remote_event_local_ns - local_event_ns) / 1_000_000.0

    summary = {
        "receipt_valid": len(local_peaks) >= 3 and len(remote_peaks) >= 3 and local_shift is not None and remote_shift is not None,
        "out_dir": str(out_dir),
        "play_wall_ns": play_wall_ns,
        "ffplay_returncode": play.returncode,
        "clock": clock,
        "local_first_read_wall_ns": local_first_wall_ns,
        "remote_first_read_wall_ns": remote_first_wall_ns,
        "local_peaks_s": local_peaks,
        "remote_peaks_s": remote_peaks,
        "local_schedule_shift_ms": local_shift * 1000.0 if local_shift is not None else None,
        "remote_schedule_shift_ms": remote_shift * 1000.0 if remote_shift is not None else None,
        "schedule_remote_minus_loopback_ms": schedule_delta_ms,
        "wall_remote_minus_loopback_ms": wall_delta_ms,
        "local_matches": local_matches,
        "remote_matches": remote_matches,
        "remote_stats": remote_stats,
        "events": events,
    }
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=2, sort_keys=True), encoding="utf-8")
    remote_log(
        args.ssh_target,
        "Nightwing audio sync chirp probe complete "
        f"valid={summary['receipt_valid']} remoteMinusLoopbackMs={wall_delta_ms} artifact={out_dir}",
    )
    print(json.dumps(summary, indent=2, sort_keys=True))
    return 0 if summary["receipt_valid"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
