#!/usr/bin/env python3
"""Drive all Nightwing PS Moves from Starfire Realtek loopback music.

This is an online calibration worker. Starfire owns the loopback-derived music
state; Nightwing owns local PS Move HID actuation. The worker emits unique,
smooth RGB contours for every connected Move and writes a JSONL trace that can
be scored offline against Eye observations.
"""

from __future__ import annotations

import argparse
import base64
import colorsys
import json
import math
import errno
import os
import subprocess
import sys
import time
from collections import deque
from dataclasses import dataclass
from pathlib import Path

import numpy as np


REMOTE_MULTI_RECEIVER = r"""
import os
import sys
import time

moves = {}
for spec in sys.argv[1].split(","):
    if not spec:
        continue
    name, path = spec.split("=", 1)
    moves[name] = path

log = os.path.expanduser("~/.local/state/gamecult/codex-ssh-activity.log")
os.makedirs(os.path.dirname(log), exist_ok=True)
with open(log, "a", encoding="utf-8") as f:
    f.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S%z')} Codex: online Move music sync receiver armed for {len(moves)} Moves.\n")

last = {}

def write_rgb(path, r, g, b):
    try:
        with open(path, "wb", buffering=0) as device:
            device.write(bytes([0x06, 0, max(0, min(255, r)), max(0, min(255, g)), max(0, min(255, b)), 0, 0, 0, 0]))
        return True
    except OSError as ex:
        if ex.errno in (errno.ENOENT, errno.ENODEV, errno.EACCES):
            return False
        raise

for path in moves.values():
    write_rgb(path, 0, 0, 0)

for line in sys.stdin:
    parts = line.strip().split()
    if len(parts) != 4:
        continue
    name = parts[0]
    path = moves.get(name)
    if not path:
        continue
    try:
        rgb = tuple(max(0, min(255, int(part))) for part in parts[1:4])
    except ValueError:
        continue
    if last.get(name) == rgb:
        continue
    if write_rgb(path, rgb[0], rgb[1], rgb[2]):
        last[name] = rgb

for path in moves.values():
    write_rgb(path, 0, 0, 0)
"""


@dataclass(frozen=True)
class Move:
    name: str
    hidraw: str
    base_rgb: tuple[int, int, int]


def parse_move(value: str) -> Move:
    name, rest = value.split("=", 1)
    hidraw, color = rest.split(":", 1)
    color = color.lstrip("#")
    if len(color) != 6:
        raise ValueError(f"invalid move color: {value}")
    rgb = (int(color[0:2], 16), int(color[2:4], 16), int(color[4:6], 16))
    return Move(name=name, hidraw=hidraw, base_rgb=rgb)


def rgb_to_hsv01(rgb: tuple[int, int, int]) -> tuple[float, float, float]:
    return colorsys.rgb_to_hsv(rgb[0] / 255.0, rgb[1] / 255.0, rgb[2] / 255.0)


class OnlineAnalyzer:
    def __init__(self, rate: int, fps: float, fft_size: int, args: argparse.Namespace) -> None:
        self.rate = rate
        self.fps = fps
        self.fft_size = fft_size
        self.args = args
        self.window = np.hanning(fft_size).astype(np.float32)
        bins = fft_size // 2 + 1
        self.fast = np.zeros(bins, dtype=np.float32)
        self.slow = np.ones(bins, dtype=np.float32) * 1e-5
        self.delta_fast = np.zeros(bins, dtype=np.float32)
        self.delta_slow = np.ones(bins, dtype=np.float32) * 1e-5
        self.minv = np.zeros(bins, dtype=np.float32)
        self.maxv = np.ones(bins, dtype=np.float32) * 1e-4
        self.flux_history: deque[float] = deque(maxlen=max(8, int(args.onset_history_seconds * fps)))
        self.onset_series: deque[float] = deque(maxlen=max(32, int(args.tempo_window_seconds * fps)))
        self.pending = np.zeros(0, dtype=np.float32)
        self.last_peak = 0.0
        self.onset_env = 0.0
        self.level_env = 0.0
        self.bpm = 140.0
        self.bpm_confidence = 0.0
        self.beat_phase = 0.0
        self.last_t = time.monotonic()

    def analyze(self, mono: np.ndarray, now: float) -> dict[str, float | tuple[float, float, float]]:
        if mono.size < self.fft_size:
            mono = np.pad(mono, (self.fft_size - mono.size, 0))
        frame = mono[-self.fft_size :].astype(np.float32) * self.window
        mag = np.abs(np.fft.rfft(frame)).astype(np.float32)
        mag[0:2] *= 0.1

        a = self.args
        self.fast = a.whiten_fast * mag + (1.0 - a.whiten_fast) * self.fast
        self.slow = a.whiten_slow * mag + (1.0 - a.whiten_slow) * self.slow
        positive = np.maximum(0.0, self.fast - self.slow)
        self.delta_fast = a.whiten_delta_fast * positive + (1.0 - a.whiten_delta_fast) * self.delta_fast
        self.delta_slow = a.whiten_delta_slow * positive + (1.0 - a.whiten_delta_slow) * self.delta_slow
        delta = np.maximum(0.0, self.delta_fast - self.delta_slow)

        decay = a.whiten_decay
        self.minv = np.minimum(delta, self.minv * decay + delta * (1.0 - decay))
        self.maxv = np.maximum(delta, self.maxv * decay + delta * (1.0 - decay))
        norm = np.clip((delta - self.minv) / np.maximum(1e-6, self.maxv - self.minv), 0.0, 1.0)
        shaped = np.power(norm, max(0.5, a.whiten_contrast))
        flux = float(np.mean(shaped))
        body = float(np.sqrt(np.mean(np.square(mono)))) if mono.size else 0.0

        self.flux_history.append(flux)
        ordered = sorted(self.flux_history)
        rank = 0.0
        if ordered:
            below = sum(1 for value in ordered if value <= flux)
            rank = below / len(ordered)
        warm = len(self.flux_history) >= min(self.flux_history.maxlen or 1, int(a.warmup_seconds * self.fps))
        hit = 0.0
        if warm and rank >= a.onset_threshold and (now - self.last_peak) >= a.onset_cooldown_seconds:
            hit = min(1.0, max(0.0, rank) ** a.onset_exponent)
            self.last_peak = now
            self.beat_phase = 0.0

        self.onset_env = max(hit, self.onset_env * a.onset_decay)
        self.level_env = max(self.onset_env, body * a.body_gain, self.level_env * a.level_decay)
        self.onset_series.append(flux)
        if len(self.onset_series) >= int(self.fps * 4) and int(now * 2) != int(self.last_t * 2):
            self._estimate_tempo()

        dt = max(0.0, now - self.last_t)
        self.last_t = now
        self.beat_phase = (self.beat_phase + dt * self.bpm / 60.0) % 1.0
        f0, color = self._estimate_fundamental_and_color(mag)
        return {
            "flux": flux,
            "percentile": rank,
            "hit": hit,
            "body": body,
            "level": self.level_env,
            "bpm": self.bpm,
            "bpm_confidence": self.bpm_confidence,
            "beat_phase": self.beat_phase,
            "fundamental_hz": f0,
            "color_balance": color,
        }

    def _estimate_tempo(self) -> None:
        x = np.asarray(self.onset_series, dtype=np.float32)
        x = x - float(np.mean(x))
        if float(np.max(np.abs(x))) < 1e-6:
            return
        best_bpm = self.bpm
        best_score = 0.0
        for bpm in np.linspace(self.args.tempo_min_bpm, self.args.tempo_max_bpm, 161):
            lag = int(round((60.0 / float(bpm)) * self.fps))
            if lag <= 1 or lag >= len(x) - 4:
                continue
            a = x[lag:]
            b = x[:-lag]
            denom = float(np.linalg.norm(a) * np.linalg.norm(b))
            score = 0.0 if denom <= 1e-9 else float(np.dot(a, b) / denom)
            if score > best_score:
                best_score = score
                best_bpm = float(bpm)
        self.bpm = 0.88 * self.bpm + 0.12 * best_bpm
        self.bpm_confidence = max(0.0, min(1.0, best_score))

    def _estimate_fundamental_and_color(self, mag: np.ndarray) -> tuple[float, tuple[float, float, float]]:
        freqs = np.fft.rfftfreq(self.fft_size, 1.0 / self.rate)
        lo = max(1, int(np.searchsorted(freqs, self.args.fundamental_min)))
        hi = min(len(freqs) - 1, int(np.searchsorted(freqs, self.args.fundamental_max)))
        best_f0 = 110.0
        best_score = 0.0
        for idx in range(lo, hi):
            f0 = freqs[idx]
            score = 0.0
            h = 1
            while f0 * h < self.rate * 0.45:
                j = int(np.searchsorted(freqs, f0 * h))
                if 0 <= j < len(mag):
                    score += float(mag[j]) / h
                h += 1
            if score > best_score:
                best_score = score
                best_f0 = float(f0)
        bands = np.zeros(3, dtype=np.float32)
        for f, m in zip(freqs[2:], mag[2:]):
            if f <= 0:
                continue
            octave = (math.log2(max(f, 1e-6) / max(best_f0, 1e-6)) % 1.0)
            bands[int(octave * 3.0) % 3] += float(m)
        total = float(np.sum(bands))
        if total <= 1e-6:
            return best_f0, (0.25, 0.20, 0.55)
        bands /= total
        bands = np.power(np.maximum(bands, 0.0), self.args.color_contrast)
        bands /= max(1e-6, float(np.max(bands)))
        return best_f0, (float(bands[0]), float(bands[1]), float(bands[2]))


def move_rgb(move: Move, move_index: int, move_count: int, state: dict[str, object], args: argparse.Namespace) -> tuple[int, int, int]:
    level = float(state["level"])
    beat = float(state["beat_phase"])
    hit = float(state["hit"])
    base_h, base_s, _ = rgb_to_hsv01(move.base_rgb)
    spectral = state["color_balance"]
    assert isinstance(spectral, tuple)
    phase = (beat + move_index / max(1, move_count)) % 1.0
    gesture = 0.52 + 0.48 * (0.5 + 0.5 * math.sin(2.0 * math.pi * phase)) ** 1.7
    accent = min(1.0, level * gesture + hit * 0.55)
    hue = (base_h + 0.045 * math.sin(2.0 * math.pi * phase)) % 1.0
    sat = min(1.0, max(0.72, base_s) + 0.18 * hit)
    val = min(1.0, accent ** 0.55)
    rr, gg, bb = colorsys.hsv_to_rgb(hue, sat, val)
    r = rr * (0.45 + 0.55 * float(spectral[0]))
    g = gg * (0.45 + 0.55 * float(spectral[1]))
    b = bb * (0.45 + 0.55 * float(spectral[2]))
    white = max(0.0, hit - 0.92) / 0.08
    return (
        int(min(255, 255 * r + 18 * white)),
        int(min(255, 255 * g + 18 * white)),
        int(min(255, 255 * b + 18 * white)),
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--duration", type=float, default=1800.0)
    parser.add_argument("--out-dir", default="artifacts/runtime/online-move-music-sync")
    parser.add_argument("--wasapi", default="src/Mimir.WasapiLoopback/bin/Debug/net10.0-windows/Mimir.WasapiLoopback.exe")
    parser.add_argument("--device", default="Realtek")
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--channels", type=int, default=2)
    parser.add_argument("--fps", type=float, default=60.0)
    parser.add_argument("--fft-size", type=int, default=512)
    parser.add_argument("--ssh-target", default="nightwing")
    parser.add_argument("--move", action="append", required=True)
    parser.add_argument("--body-gain", type=float, default=0.22)
    parser.add_argument("--level-decay", type=float, default=0.80)
    parser.add_argument("--onset-decay", type=float, default=0.18)
    parser.add_argument("--onset-threshold", type=float, default=0.62)
    parser.add_argument("--onset-exponent", type=float, default=2.0)
    parser.add_argument("--onset-history-seconds", type=float, default=4.5)
    parser.add_argument("--onset-cooldown-seconds", type=float, default=0.11)
    parser.add_argument("--warmup-seconds", type=float, default=0.5)
    parser.add_argument("--whiten-fast", type=float, default=0.78)
    parser.add_argument("--whiten-slow", type=float, default=0.12)
    parser.add_argument("--whiten-delta-fast", type=float, default=0.62)
    parser.add_argument("--whiten-delta-slow", type=float, default=0.08)
    parser.add_argument("--whiten-decay", type=float, default=0.78)
    parser.add_argument("--whiten-contrast", type=float, default=3.6)
    parser.add_argument("--color-contrast", type=float, default=3.4)
    parser.add_argument("--tempo-window-seconds", type=float, default=12.0)
    parser.add_argument("--tempo-min-bpm", type=float, default=70.0)
    parser.add_argument("--tempo-max-bpm", type=float, default=260.0)
    parser.add_argument("--fundamental-min", type=float, default=55.0)
    parser.add_argument("--fundamental-max", type=float, default=880.0)
    args = parser.parse_args()

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    moves = [parse_move(value) for value in args.move]
    (out_dir / "moves.json").write_text(json.dumps([move.__dict__ for move in moves], indent=2), encoding="utf-8")

    remote_b64 = base64.b64encode(REMOTE_MULTI_RECEIVER.encode("utf-8")).decode("ascii")
    move_specs = ",".join(f"{move.name}={move.hidraw}" for move in moves)
    remote_cmd = (
        "tmp=$(mktemp); "
        f"printf '%s' '{remote_b64}' | base64 -d > \"$tmp\"; "
        f"python3 \"$tmp\" '{move_specs}'; "
        "status=$?; rm -f \"$tmp\"; exit $status"
    )
    ssh = subprocess.Popen(["ssh", "-o", "BatchMode=yes", args.ssh_target, remote_cmd], stdin=subprocess.PIPE)
    wasapi = subprocess.Popen(
        [
            args.wasapi,
            "--device",
            args.device,
            "--output",
            "stdout",
            "--sample-rate",
            str(args.sample_rate),
            "--channels",
            str(args.channels),
            "--seconds",
            str(args.duration),
        ],
        stdout=subprocess.PIPE,
        stderr=(out_dir / "wasapi.err.log").open("w", encoding="utf-8", errors="replace"),
    )
    assert wasapi.stdout is not None and ssh.stdin is not None
    analyzer = OnlineAnalyzer(args.sample_rate, args.fps, args.fft_size, args)
    frame_samples = max(args.fft_size, int(args.sample_rate / args.fps))
    frame_bytes = frame_samples * args.channels * 4
    trace_path = out_dir / "online-sync.jsonl"
    start = time.monotonic()
    frames = 0
    try:
        with trace_path.open("w", encoding="utf-8") as trace:
            while time.monotonic() - start < args.duration:
                data = wasapi.stdout.read(frame_bytes)
                if len(data) < frame_bytes:
                    break
                block = np.frombuffer(data, dtype=np.float32).reshape((-1, args.channels))
                mono = np.mean(block, axis=1)
                now = time.monotonic()
                state = analyzer.analyze(mono, now)
                rgbs = {}
                for index, move in enumerate(moves):
                    rgb = move_rgb(move, index, len(moves), state, args)
                    rgbs[move.name] = rgb
                    ssh.stdin.write(f"{move.name} {rgb[0]} {rgb[1]} {rgb[2]}\n".encode("ascii"))
                ssh.stdin.flush()
                frames += 1
                record = {
                    "kind": "mimir.online_move_music_sync_frame.v1",
                    "t_monotonic": now,
                    "elapsed_seconds": now - start,
                    "frame": frames,
                    **{key: value for key, value in state.items() if key != "color_balance"},
                    "color_balance": list(state["color_balance"]),  # type: ignore[index]
                    "moves": {name: list(rgb) for name, rgb in rgbs.items()},
                }
                trace.write(json.dumps(record, sort_keys=True) + "\n")
                if frames % max(1, int(args.fps * 2)) == 0:
                    trace.flush()
                    print(
                        f"t={record['elapsed_seconds']:.1f}s level={record['level']:.3f} "
                        f"hit={record['hit']:.3f} pct={record['percentile']:.3f} "
                        f"bpm={record['bpm']:.1f}/{record['bpm_confidence']:.2f} "
                        f"f0={record['fundamental_hz']:.1f}",
                        flush=True,
                    )
    finally:
        try:
            for move in moves:
                ssh.stdin.write(f"{move.name} 0 0 0\n".encode("ascii"))
            ssh.stdin.close()
        except Exception:
            pass
        for proc in (wasapi, ssh):
            if proc.poll() is None:
                proc.terminate()
        time.sleep(0.2)
        for proc in (wasapi, ssh):
            if proc.poll() is None:
                proc.kill()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
