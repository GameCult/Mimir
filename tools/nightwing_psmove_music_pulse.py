#!/usr/bin/env python3
"""Pulse a Nightwing-connected PS Move light from Starfire audio.

Starfire owns the audio envelope. Nightwing owns the PS Move HID output.
This bridge keeps that boundary explicit: FFmpeg reads the local Focusrite
capture device, this process turns audio chunks into RGB frames, and a tiny
remote Python receiver writes PS Move report 0x06 to hidraw.

Reference for the 9-byte LED report: PS Move API `PSMove_Data_LEDs`
(`src/psmove.c`), BSD-licensed by Thomas Perl et al.
"""

from __future__ import annotations

import argparse
import base64
import colorsys
import ctypes
import math
import os
import signal
import struct
import subprocess
import sys
import time

import numpy as np


REMOTE_RECEIVER = r"""
import os
import sys
import time

hidraw = sys.argv[1]
log = os.path.expanduser("~/.local/state/gamecult/codex-ssh-activity.log")
os.makedirs(os.path.dirname(log), exist_ok=True)
with open(log, "a", encoding="utf-8") as f:
    f.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S%z')} Codex: PS Move music LED receiver started on {hidraw}.\n")

last = None
for line in sys.stdin:
    parts = line.strip().split()
    if len(parts) != 3:
        continue
    try:
        r, g, b = [max(0, min(255, int(part))) for part in parts]
    except ValueError:
        continue
    rgb = (r, g, b)
    if rgb == last:
        continue
    with open(hidraw, "wb", buffering=0) as device:
        device.write(bytes([0x06, 0, r, g, b, 0, 0, 0, 0]))
    last = rgb
"""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--device",
        default="Analogue 1 + 2 (2- Focusrite USB Audio)",
        help="DirectShow audio capture device on Starfire.",
    )
    parser.add_argument("--ffmpeg", default="ffmpeg.exe", help="FFmpeg executable.")
    parser.add_argument(
        "--source",
        choices=("asio", "dshow"),
        default="asio",
        help="Audio capture source. ASIO is the Scarlett loopback authority.",
    )
    parser.add_argument(
        "--asio-dll",
        default=str(
            os.path.join(
                os.path.dirname(os.path.dirname(__file__)),
                "native",
                "asio_capture",
                "build",
                "Release",
                "mimir_asio_capture.dll",
            )
        ),
        help="Native Mimir ASIO capture DLL.",
    )
    parser.add_argument(
        "--asio-clsid",
        default="{AC4D0455-50D7-4498-B3CD-9A41D130B759}",
        help="Focusrite USB ASIO driver CLSID.",
    )
    parser.add_argument(
        "--asio-channels",
        default="2,3",
        help="Comma-separated ASIO channels to treat as the loopback envelope.",
    )
    parser.add_argument("--ssh-target", default="nightwing", help="SSH target for Nightwing.")
    parser.add_argument("--hidraw", default="/dev/hidraw1", help="Nightwing PS Move hidraw path.")
    parser.add_argument("--rate", type=int, default=48000, help="Sample rate.")
    parser.add_argument("--fps", type=float, default=30.0, help="LED update rate.")
    parser.add_argument("--gain", type=float, default=3.2, help="Audio envelope gain.")
    parser.add_argument("--floor", type=float, default=0.008, help="Noise floor.")
    parser.add_argument("--fft-size", type=int, default=512, help="Tiny FFT size for onset flux.")
    parser.add_argument("--onset-gain", type=float, default=7.0, help="Spectral-rise onset gain.")
    parser.add_argument("--onset-decay", type=float, default=0.38, help="Per-frame onset decay.")
    parser.add_argument("--onset-threshold", type=float, default=1.45, help="Adaptive onset threshold in standard deviations.")
    parser.add_argument("--onset-baseline-rate", type=float, default=0.015, help="Adaptive novelty baseline update rate.")
    parser.add_argument("--onset-cooldown-ms", type=float, default=70.0, help="Suppress duplicate onset spikes inside this window.")
    parser.add_argument("--whiten-rate", type=float, default=0.035, help="Adaptive spectral-floor update rate.")
    parser.add_argument("--whiten-contrast", type=float, default=2.4, help="Adaptive whitening contrast exponent.")
    parser.add_argument("--color-contrast", type=float, default=1.8, help="Folded octave color balance contrast.")
    parser.add_argument("--fundamental-min", type=float, default=55.0, help="Minimum best-fit fundamental in Hz.")
    parser.add_argument("--fundamental-max", type=float, default=880.0, help="Maximum best-fit fundamental in Hz.")
    return parser.parse_args()


class AsioOnsetReader:
    def __init__(self, dll_path: str, clsid: str, sample_rate: int, channels: set[int], fft_size: int) -> None:
        self.channels = channels
        self.fft_size = max(64, int(fft_size))
        self.pending: list[float] = []
        self.previous = np.zeros(self.fft_size // 2 + 1, dtype=np.float32)
        self.floor = np.zeros(self.fft_size // 2 + 1, dtype=np.float32)
        self.window = np.hanning(self.fft_size).astype(np.float32)
        self.color_balance = np.array([0.25, 0.2, 0.55], dtype=np.float32)
        self.flux_mean = 0.0
        self.flux_var = 1e-6
        self.frame_index = 0
        self.last_onset_frame = -1_000_000
        self.last_onset_score = 0.0
        self.dll = ctypes.WinDLL(dll_path)
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
            raise RuntimeError("Could not create Mimir ASIO capture source")
        self.sample_rate = int(actual_rate.value)
        self.input_count = int(input_count.value)
        self.max_frames = max(1, int(max_frames.value))
        self.freqs = np.fft.rfftfreq(self.fft_size, 1.0 / self.sample_rate)
        if not self.dll.mimir_asio_start(self.handle):
            self.close()
            raise RuntimeError("Could not start Mimir ASIO capture source")
        self.buffer = (ctypes.c_float * self.max_frames)()

    def read_onset(
        self,
        floor: float,
        gain: float,
        deadline: float,
        whiten_rate: float,
        whiten_contrast: float,
        fundamental_min: float,
        fundamental_max: float,
        onset_threshold: float,
        onset_baseline_rate: float,
        onset_cooldown_ms: float,
    ) -> tuple[float, float, tuple[float, float, float], float] | None:
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
            frames = max(0, min(int(frame_count.value), self.max_frames))
            if int(channel.value) not in self.channels or frames == 0:
                continue
            for index in range(frames):
                self.pending.append(float(self.buffer[index]))
            if len(self.pending) >= self.fft_size:
                frame = np.asarray(self.pending[: self.fft_size], dtype=np.float32)
                del self.pending[: self.fft_size // 2]
                rms = float(np.sqrt(np.mean(frame * frame)))
                spectrum = np.abs(np.fft.rfft(frame * self.window)).astype(np.float32)
                spectrum = np.log1p(spectrum)
                if not np.any(self.floor):
                    self.floor = spectrum.copy()
                floor_alpha = max(0.001, min(0.5, whiten_rate))
                self.floor = np.minimum(
                    spectrum,
                    (1.0 - floor_alpha) * self.floor + floor_alpha * spectrum,
                )
                whitened = np.maximum(spectrum - self.floor, 0.0)
                if whitened.size > 4:
                    whitened -= np.quantile(whitened[2:], 0.35)
                whitened = np.maximum(whitened, 0.0)
                if whitened.size > 4:
                    peak = float(np.quantile(whitened[2:], 0.97))
                    if peak > 1e-6:
                        whitened = np.clip(whitened / peak, 0.0, 2.0)
                whitened = np.power(whitened, max(0.5, whiten_contrast)).astype(np.float32)

                rise = np.maximum(whitened - self.previous, 0.0)
                self.previous = whitened
                active = rise[2:]
                flux = float(np.mean(active) + 0.35 * np.percentile(active, 92))
                baseline_rate = max(0.001, min(0.2, onset_baseline_rate))
                baseline = self.flux_mean
                flux_std = math.sqrt(max(1e-9, self.flux_var))
                novelty = max(0.0, flux - baseline - max(0.0, onset_threshold) * flux_std)
                delta = flux - self.flux_mean
                adapt_rate = baseline_rate * (0.25 if novelty > 0.0 else 1.0)
                self.flux_mean += adapt_rate * delta
                self.flux_var = (1.0 - adapt_rate) * self.flux_var + adapt_rate * delta * delta
                hop_seconds = (self.fft_size // 2) / max(1, self.sample_rate)
                cooldown_frames = max(1, int((onset_cooldown_ms / 1000.0) / hop_seconds))
                if self.frame_index - self.last_onset_frame < cooldown_frames:
                    if novelty <= self.last_onset_score * 1.35:
                        novelty = 0.0
                if novelty > 0.0:
                    self.last_onset_frame = self.frame_index
                    self.last_onset_score = novelty
                self.frame_index += 1
                fundamental = self._estimate_fundamental(whitened, fundamental_min, fundamental_max)
                color = self._fold_color(whitened + rise * 1.8, fundamental)
                onset = max(0.0, novelty * gain)
                body = max(0.0, (rms - floor) * 0.45)
                return onset, body, color, fundamental
        if not self.pending:
            return None
        return 0.0, 0.0, tuple(float(v) for v in self.color_balance), 0.0

    def _estimate_fundamental(self, spectrum: np.ndarray, min_hz: float, max_hz: float) -> float:
        nyquist = self.sample_rate * 0.5
        min_hz = max(20.0, min_hz)
        max_hz = min(max_hz, nyquist * 0.45)
        candidates = np.geomspace(min_hz, max_hz, 48)
        best_hz = min_hz
        best_score = 0.0
        for candidate in candidates:
            score = 0.0
            harmonic = 1
            while harmonic * candidate < nyquist:
                freq = harmonic * candidate
                bin_index = int(round(freq * self.fft_size / self.sample_rate))
                if 1 <= bin_index < spectrum.size:
                    score += float(spectrum[bin_index]) / math.sqrt(harmonic)
                harmonic += 1
            score /= max(1.0, math.sqrt(harmonic - 1))
            if score > best_score:
                best_score = score
                best_hz = float(candidate)
        return best_hz if best_score > 1e-4 else 0.0

    def _fold_color(self, spectrum: np.ndarray, fundamental: float) -> tuple[float, float, float]:
        if fundamental <= 0.0:
            return tuple(float(v) for v in self.color_balance)
        accum = np.zeros(3, dtype=np.float32)
        for bin_index in range(2, spectrum.size):
            energy = float(spectrum[bin_index])
            if energy <= 0.0:
                continue
            freq = float(self.freqs[bin_index])
            if freq < fundamental * 0.5:
                continue
            octave_phase = math.log2(freq / fundamental) % 1.0
            r, g, b = colorsys.hsv_to_rgb(octave_phase, 0.95, 1.0)
            accum += energy * np.array([r, g, b], dtype=np.float32)
        total = float(np.sum(accum))
        if total <= 1e-6:
            target = self.color_balance
        else:
            target = accum / total
        self.color_balance = 0.72 * self.color_balance + 0.28 * target
        color_total = float(np.sum(self.color_balance))
        if color_total > 1e-6:
            self.color_balance /= color_total
        return tuple(float(v) for v in self.color_balance)

    def close(self) -> None:
        if getattr(self, "handle", None):
            self.dll.mimir_asio_destroy(self.handle)
            self.handle = None


def rgb_from_level(level: float, balance: tuple[float, float, float], color_contrast: float) -> tuple[int, int, int]:
    level = max(0.0, min(1.0, level))
    glow = level ** 0.55
    color = np.asarray(balance, dtype=np.float32)
    color = np.power(np.maximum(color, 0.0), max(0.5, color_contrast))
    color /= max(1e-6, float(np.max(color)))
    white = max(0.0, glow - 0.86) / 0.14
    r = int(8 + 232 * glow * (0.12 + 0.88 * float(color[0])) + 14 * white)
    g = int(8 + 232 * glow * (0.12 + 0.88 * float(color[1])) + 14 * white)
    b = int(12 + 232 * glow * (0.12 + 0.88 * float(color[2])) + 14 * white)
    return (min(255, r), min(255, g), min(255, b))


def main() -> int:
    args = parse_args()
    samples_per_frame = max(256, int(args.rate / args.fps))
    bytes_per_frame = samples_per_frame * 2

    remote_b64 = base64.b64encode(REMOTE_RECEIVER.encode("utf-8")).decode("ascii")
    remote_cmd = (
        "tmp=$(mktemp); "
        f"printf '%s' '{remote_b64}' | base64 -d > \"$tmp\"; "
        f"python3 \"$tmp\" '{args.hidraw}'; "
        "status=$?; rm -f \"$tmp\"; exit $status"
    )

    ssh_cmd = ["ssh", "-o", "BatchMode=yes", args.ssh_target, remote_cmd]

    ffmpeg = None
    asio = None
    if args.source == "dshow":
        ffmpeg_cmd = [
            args.ffmpeg,
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "dshow",
            "-audio_buffer_size",
            "50",
            "-i",
            f"audio={args.device}",
            "-ac",
            "1",
            "-ar",
            str(args.rate),
            "-f",
            "s16le",
            "-",
        ]
        ffmpeg = subprocess.Popen(ffmpeg_cmd, stdout=subprocess.PIPE)
        assert ffmpeg.stdout is not None
    else:
        channels = {int(part.strip()) for part in args.asio_channels.split(",") if part.strip()}
        asio = AsioOnsetReader(args.asio_dll, args.asio_clsid, args.rate, channels, args.fft_size)
        print(
            f"asio sampleRate={asio.sample_rate} inputs={asio.input_count} channels={sorted(channels)}",
            flush=True,
        )

    ssh = subprocess.Popen(ssh_cmd, stdin=subprocess.PIPE)

    assert ssh.stdin is not None

    stopping = False

    def stop(_signum: int, _frame: object) -> None:
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)

    env = 0.0
    onset_env = 0.0
    frames = 0
    try:
        while not stopping:
            if asio is not None:
                onset = asio.read_onset(
                    args.floor,
                    args.onset_gain,
                    time.monotonic() + (1.0 / args.fps),
                    args.whiten_rate,
                    args.whiten_contrast,
                    args.fundamental_min,
                    args.fundamental_max,
                    args.onset_threshold,
                    args.onset_baseline_rate,
                    args.onset_cooldown_ms,
                )
                if onset is None:
                    continue
                hit, body, balance, fundamental = onset
                onset_env = max(hit, onset_env * args.onset_decay)
                level = max(body * args.gain, onset_env)
            else:
                assert ffmpeg is not None and ffmpeg.stdout is not None
                data = ffmpeg.stdout.read(bytes_per_frame)
                if len(data) < bytes_per_frame:
                    break
                count = len(data) // 2
                samples = struct.unpack("<" + "h" * count, data)
                rms = math.sqrt(sum(sample * sample for sample in samples) / count) / 32768.0
                level = max(0.0, (rms - args.floor) * args.gain)
                balance = (0.25, 0.2, 0.55)
                fundamental = 0.0
                hit = 0.0
            env_decay = args.onset_decay if asio is not None else 0.82
            env = max(level, env * env_decay)
            r, g, b = rgb_from_level(env, balance, args.color_contrast)
            if ssh.poll() is not None:
                raise RuntimeError(f"SSH LED receiver exited with {ssh.returncode}")
            ssh.stdin.write(f"{r} {g} {b}\n".encode("ascii"))
            ssh.stdin.flush()
            frames += 1
            if hit > 0.02:
                print(
                    f"peak onset={hit:.3f} f0={fundamental:.1f} "
                    f"balance={balance[0]:.2f},{balance[1]:.2f},{balance[2]:.2f} "
                    f"rgb={r},{g},{b}",
                    flush=True,
                )
            if frames % int(args.fps * 2) == 0:
                print(
                    "level="
                    f"{env:.3f} onset={onset_env:.3f} f0={fundamental:.1f} "
                    f"balance={balance[0]:.2f},{balance[1]:.2f},{balance[2]:.2f} "
                    f"rgb={r},{g},{b}",
                    flush=True,
                )
    finally:
        try:
            ssh.stdin.write(b"0 0 32\n")
            ssh.stdin.close()
        except Exception:
            pass
        if ffmpeg is not None:
            ffmpeg.terminate()
        if asio is not None:
            asio.close()
        ssh.terminate()
        time.sleep(0.2)
        for proc in (ffmpeg, ssh):
            if proc is None:
                continue
            if proc.poll() is None:
                proc.kill()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
