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

try:
    from nightwing_psmove_music_pulse import AsioOnsetReader
except Exception:  # pragma: no cover - optional live hardware path
    AsioOnsetReader = None  # type: ignore[assignment]

PITCH_NAMES = ("C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B")
MAJOR_KEY_PROFILE = np.asarray([6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88], dtype=np.float32)
MINOR_KEY_PROFILE = np.asarray([6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17], dtype=np.float32)


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
        self.loudness_history: deque[float] = deque(maxlen=max(8, int(args.loudness_history_seconds * fps)))
        self.onset_series: deque[float] = deque(maxlen=max(32, int(args.tempo_window_seconds * fps)))
        self.pending = np.zeros(0, dtype=np.float32)
        self.last_peak = 0.0
        self.onset_env = 0.0
        self.level_env = 0.0
        self.loudness_env = 0.0
        self.chroma_smooth = np.ones(12, dtype=np.float32) / 12.0
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
        self.loudness_history.append(body)
        rank = percentile_rank(self.flux_history, flux)
        loudness_rank = percentile_rank(self.loudness_history, body)
        loudness_scale = self._scaled_loudness(body)
        loudness_gate = 0.0
        if body >= a.loudness_floor and loudness_rank >= a.loudness_threshold:
            span = max(1e-6, 1.0 - a.loudness_threshold)
            loudness_gate = ((loudness_rank - a.loudness_threshold) / span) ** a.loudness_exponent
            loudness_gate *= loudness_scale
        self.loudness_env = max(loudness_gate, self.loudness_env * a.loudness_decay)
        warm_needed = int(a.warmup_seconds * self.fps)
        warm = (
            len(self.flux_history) >= min(self.flux_history.maxlen or 1, warm_needed)
            and len(self.loudness_history) >= min(self.loudness_history.maxlen or 1, warm_needed)
        )
        hit = 0.0
        if (
            warm
            and rank >= a.onset_threshold
            and loudness_rank >= a.hit_loudness_threshold
            and loudness_gate > 0.0
            and (now - self.last_peak) >= a.onset_cooldown_seconds
        ):
            hit = min(1.0, max(0.0, rank) ** a.onset_exponent)
            hit *= max(0.0, min(1.0, loudness_gate))
            self.last_peak = now
            self.beat_phase = 0.0

        self.onset_env = max(hit, self.onset_env * a.onset_decay)
        musical_level = self.loudness_env * min(1.0, body * a.body_gain)
        self.level_env = max(self.onset_env, musical_level, self.level_env * a.level_decay)
        self.onset_series.append(flux)
        if len(self.onset_series) >= int(self.fps * 4) and int(now * 2) != int(self.last_t * 2):
            self._estimate_tempo()

        dt = max(0.0, now - self.last_t)
        self.last_t = now
        self.beat_phase = (self.beat_phase + dt * self.bpm / 60.0) % 1.0
        f0, color = self._estimate_fundamental_and_color(mag)
        chroma, key_name, key_mode, key_confidence, chord_name, chord_confidence, chord_root = self._estimate_chroma_key_chord(mag)
        return {
            "flux": flux,
            "percentile": rank,
            "hit": hit,
            "body": body,
            "loudness_percentile": loudness_rank,
            "loudness_scale": loudness_scale,
            "loudness_gate": loudness_gate,
            "level": self.level_env,
            "bpm": self.bpm,
            "bpm_confidence": self.bpm_confidence,
            "beat_phase": self.beat_phase,
            "fundamental_hz": f0,
            "color_balance": color,
            "chroma": tuple(float(value) for value in chroma),
            "key_name": key_name,
            "key_mode": key_mode,
            "key_confidence": key_confidence,
            "chord_name": chord_name,
            "chord_confidence": chord_confidence,
            "chord_root": chord_root,
        }

    def _scaled_loudness(self, body: float) -> float:
        if len(self.loudness_history) < 4:
            return 0.0
        values = np.asarray(self.loudness_history, dtype=np.float32)
        floor = max(self.args.loudness_floor, float(np.percentile(values, 35)))
        ceiling = max(floor + 1e-6, float(np.percentile(values, 96)))
        return max(0.0, min(1.0, (body - floor) / (ceiling - floor)))

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

    def _estimate_chroma_key_chord(self, mag: np.ndarray) -> tuple[np.ndarray, str, str, float, str, float, int]:
        freqs = np.fft.rfftfreq(self.fft_size, 1.0 / self.rate)
        chroma = np.zeros(12, dtype=np.float32)
        for f, m in zip(freqs[2:], mag[2:]):
            if f < self.args.chroma_min_hz or f > self.args.chroma_max_hz:
                continue
            midi = 69.0 + 12.0 * math.log2(float(f) / 440.0)
            pitch_class = int(round(midi)) % 12
            chroma[pitch_class] += float(m)
        total = float(np.sum(chroma))
        if total > 1e-6:
            chroma /= total
            chroma = np.power(np.maximum(chroma, 0.0), self.args.chroma_contrast)
            chroma /= max(1e-6, float(np.sum(chroma)))
            self.chroma_smooth = (
                self.args.chroma_smoothing * self.chroma_smooth
                + (1.0 - self.args.chroma_smoothing) * chroma
            )
            self.chroma_smooth /= max(1e-6, float(np.sum(self.chroma_smooth)))
        key_root, key_mode, key_confidence = score_key(self.chroma_smooth)
        chord_root, chord_quality, chord_confidence = score_chord(self.chroma_smooth)
        key_name = PITCH_NAMES[key_root]
        chord_name = f"{PITCH_NAMES[chord_root]}{'' if chord_quality == 'maj' else 'm'}"
        return self.chroma_smooth.copy(), key_name, key_mode, key_confidence, chord_name, chord_confidence, chord_root


def percentile_rank(values: deque[float], value: float) -> float:
    if not values:
        return 0.0
    below = sum(1 for item in values if item <= value)
    return below / len(values)


def centered_profile_score(chroma: np.ndarray, profile: np.ndarray) -> float:
    x = chroma - float(np.mean(chroma))
    y = profile - float(np.mean(profile))
    denom = float(np.linalg.norm(x) * np.linalg.norm(y))
    if denom <= 1e-9:
        return 0.0
    return float(np.dot(x, y) / denom)


def score_key(chroma: np.ndarray) -> tuple[int, str, float]:
    scores: list[tuple[float, int, str]] = []
    for root in range(12):
        scores.append((centered_profile_score(chroma, np.roll(MAJOR_KEY_PROFILE, root)), root, "major"))
        scores.append((centered_profile_score(chroma, np.roll(MINOR_KEY_PROFILE, root)), root, "minor"))
    scores.sort(reverse=True)
    best, root, mode = scores[0]
    runner_up = scores[1][0] if len(scores) > 1 else 0.0
    confidence = max(0.0, min(1.0, (best - runner_up) * 2.5 + max(0.0, best) * 0.35))
    return root, mode, confidence


def score_chord(chroma: np.ndarray) -> tuple[int, str, float]:
    scores: list[tuple[float, int, str]] = []
    for root in range(12):
        major = float(chroma[root] + 0.85 * chroma[(root + 4) % 12] + chroma[(root + 7) % 12])
        minor = float(chroma[root] + 0.85 * chroma[(root + 3) % 12] + chroma[(root + 7) % 12])
        scores.append((major, root, "maj"))
        scores.append((minor, root, "min"))
    scores.sort(reverse=True)
    best, root, quality = scores[0]
    runner_up = scores[1][0] if len(scores) > 1 else 0.0
    confidence = max(0.0, min(1.0, (best - runner_up) * 4.0 + best * 0.25))
    return root, quality, confidence


class AuxiliaryOnsetStream:
    def __init__(self, name: str, fps: float, args: argparse.Namespace) -> None:
        self.name = name
        self.fps = fps
        self.args = args
        self.series: deque[float] = deque(maxlen=max(32, int(args.tempo_window_seconds * fps)))
        self.bpm = 140.0
        self.confidence = 0.0
        self.last_estimate_t = 0.0
        self.last_state: dict[str, object] = {
            "source": name,
            "hit": 0.0,
            "body": 0.0,
            "percentile": 0.0,
            "fundamental_hz": 0.0,
            "bpm": self.bpm,
            "bpm_confidence": self.confidence,
        }

    def update(
        self,
        now: float,
        hit: float,
        body: float,
        color_balance: tuple[float, float, float],
        fundamental_hz: float,
        range_hit: float,
        percentile: float,
    ) -> dict[str, object]:
        onset = max(float(hit), (max(0.0, min(1.0, float(percentile))) ** 2) * max(0.0, min(1.5, float(range_hit))))
        self.series.append(onset)
        if len(self.series) >= int(self.fps * 4) and now - self.last_estimate_t >= 0.5:
            self._estimate_tempo()
            self.last_estimate_t = now
        self.last_state = {
            "source": self.name,
            "hit": float(hit),
            "body": float(body),
            "percentile": float(percentile),
            "onset": float(onset),
            "fundamental_hz": float(fundamental_hz),
            "color_balance": tuple(float(value) for value in color_balance),
            "bpm": self.bpm,
            "bpm_confidence": self.confidence,
        }
        return self.last_state

    def _estimate_tempo(self) -> None:
        x = np.asarray(self.series, dtype=np.float32)
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
        self.confidence = max(0.0, min(1.0, best_score))


def fuse_music_sources(primary: dict[str, object], source_states: list[dict[str, object]]) -> dict[str, object]:
    if not source_states:
        primary["music_sources"] = [{"source": "realtek-loopback", "bpm": primary["bpm"], "bpm_confidence": primary["bpm_confidence"]}]
        return primary
    weighted_bpm = float(primary["bpm"]) * (0.25 + float(primary["bpm_confidence"]))
    weight_sum = 0.25 + float(primary["bpm_confidence"])
    fundamentals = [(float(primary["fundamental_hz"]), 0.5 + float(primary["loudness_gate"]))]
    for source in source_states:
        confidence = float(source.get("bpm_confidence", 0.0))
        weight = 0.15 + confidence
        weighted_bpm += float(source.get("bpm", primary["bpm"])) * weight
        weight_sum += weight
        body_weight = max(0.0, min(1.0, float(source.get("body", 0.0)) * 20.0))
        fundamentals.append((float(source.get("fundamental_hz", primary["fundamental_hz"])), body_weight))
    if weight_sum > 1e-6:
        primary["bpm"] = weighted_bpm / weight_sum
        primary["bpm_confidence"] = max(float(primary["bpm_confidence"]), max(float(s.get("bpm_confidence", 0.0)) for s in source_states))
    fundamental_weight = sum(weight for _, weight in fundamentals)
    if fundamental_weight > 1e-6:
        primary["fundamental_hz"] = sum(freq * weight for freq, weight in fundamentals) / fundamental_weight
    primary["music_sources"] = [
        {"source": "realtek-loopback", "bpm": primary["bpm"], "bpm_confidence": primary["bpm_confidence"]},
        *source_states,
    ]
    return primary


DEBRUIJN_2_3 = "00010111"


def debruijn_accent(frame_phase: float, move_index: int, args: argparse.Namespace) -> float:
    if not args.debruijn_polyrhythm:
        return 1.0
    lane_rate = args.debruijn_rate + move_index
    cursor = int(math.floor(frame_phase * max(1, lane_rate))) % len(DEBRUIJN_2_3)
    bit = 1.0 if DEBRUIJN_2_3[(cursor + move_index) % len(DEBRUIJN_2_3)] == "1" else 0.0
    return 0.58 + 0.42 * bit


def move_rgb(move: Move, move_index: int, move_count: int, state: dict[str, object], args: argparse.Namespace) -> tuple[int, int, int]:
    level = float(state["level"])
    beat = float(state["beat_phase"])
    hit = float(state["hit"])
    loudness_gate = float(state.get("loudness_gate", 0.0))
    loudness_percentile = float(state.get("loudness_percentile", 0.0))
    base_h, base_s, _ = rgb_to_hsv01(move.base_rgb)
    spectral = state["color_balance"]
    assert isinstance(spectral, tuple)
    harmonic = args.harmonic_base ** (move_index / max(1, move_count))
    micro = 2.0 ** ((move_index - (move_count - 1) * 0.5) * args.microtonal_cents / 1200.0)
    phase = (beat * harmonic * micro + move_index / max(1, move_count)) % 1.0
    poly = debruijn_accent(beat, move_index, args)
    gesture = poly * (0.42 + 0.58 * (0.5 + 0.5 * math.sin(2.0 * math.pi * phase)) ** 2.1)
    accent = min(1.0, loudness_gate * (level * gesture + hit * (0.34 + 0.12 * poly)))
    hue = (base_h + args.hue_bend * math.sin(2.0 * math.pi * phase) + math.log2(harmonic * micro) * 0.0833) % 1.0
    chord_confidence = float(state.get("chord_confidence", 0.0))
    chord_root = int(state.get("chord_root", 0))
    if chord_confidence > args.chord_hue_threshold:
        chord_degrees = (0, 4, 7, 10)
        pitch_class = (chord_root + chord_degrees[move_index % len(chord_degrees)]) % 12
        pitch_hue = pitch_class / 12.0
        mix = min(args.chord_hue_mix, chord_confidence * args.chord_hue_mix)
        hue = mix_hue(hue, pitch_hue, mix)
    sat = min(1.0, max(0.72, base_s) + 0.22 * loudness_gate + 0.08 * hit)
    if loudness_gate <= 0.0:
        val = 0.0
    else:
        val = min(args.max_brightness, max(args.quiet_brightness, accent ** args.brightness_exponent))
        if loudness_percentile < args.loudness_threshold:
            val = min(val, args.quiet_brightness)
    rr, gg, bb = colorsys.hsv_to_rgb(hue, sat, val)
    r = rr * (0.45 + 0.55 * float(spectral[0]))
    g = gg * (0.45 + 0.55 * float(spectral[1]))
    b = bb * (0.45 + 0.55 * float(spectral[2]))
    white = loudness_gate * max(0.0, hit - 0.94) / 0.06
    return (
        int(min(255, 255 * r + 18 * white)),
        int(min(255, 255 * g + 18 * white)),
        int(min(255, 255 * b + 18 * white)),
    )


def mix_hue(a: float, b: float, amount: float) -> float:
    delta = ((b - a + 0.5) % 1.0) - 0.5
    return (a + delta * max(0.0, min(1.0, amount))) % 1.0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--duration", type=float, default=1800.0)
    parser.add_argument("--out-dir", default="artifacts/runtime/online-move-music-sync")
    parser.add_argument("--wasapi", default="src/Mimir.WasapiLoopback/bin/Debug/net10.0-windows/Mimir.WasapiLoopback.exe")
    parser.add_argument("--device", default="Realtek")
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--channels", type=int, default=2)
    parser.add_argument("--asio-dll", default=str(Path(__file__).resolve().parents[1] / "native" / "asio_capture" / "build" / "Release" / "mimir_asio_capture.dll"))
    parser.add_argument("--asio-clsid", default="{AC4D0455-50D7-4498-B3CD-9A41D130B759}")
    parser.add_argument("--asio-music-channels", default="", help="Optional comma-separated Scarlett ASIO channels to fold into music evidence.")
    parser.add_argument("--asio-drain-blocks", type=int, default=1024)
    parser.add_argument("--fps", type=float, default=60.0)
    parser.add_argument("--fft-size", type=int, default=512)
    parser.add_argument("--ssh-target", default="nightwing")
    parser.add_argument("--move", action="append", required=True)
    parser.add_argument("--body-gain", type=float, default=0.22)
    parser.add_argument("--level-decay", type=float, default=0.80)
    parser.add_argument("--loudness-decay", type=float, default=0.62)
    parser.add_argument("--loudness-history-seconds", type=float, default=12.0)
    parser.add_argument("--loudness-threshold", type=float, default=0.84)
    parser.add_argument("--hit-loudness-threshold", type=float, default=0.82)
    parser.add_argument("--loudness-floor", type=float, default=0.006)
    parser.add_argument("--loudness-exponent", type=float, default=2.4)
    parser.add_argument("--quiet-brightness", type=float, default=0.012)
    parser.add_argument("--max-brightness", type=float, default=0.76)
    parser.add_argument("--brightness-exponent", type=float, default=0.82)
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
    parser.add_argument("--chroma-min-hz", type=float, default=55.0)
    parser.add_argument("--chroma-max-hz", type=float, default=4200.0)
    parser.add_argument("--chroma-contrast", type=float, default=1.7)
    parser.add_argument("--chroma-smoothing", type=float, default=0.92)
    parser.add_argument("--chord-hue-threshold", type=float, default=0.16)
    parser.add_argument("--chord-hue-mix", type=float, default=0.34)
    parser.add_argument("--debruijn-polyrhythm", action="store_true")
    parser.add_argument("--debruijn-rate", type=int, default=3)
    parser.add_argument("--harmonic-base", type=float, default=1.5)
    parser.add_argument("--microtonal-cents", type=float, default=17.0)
    parser.add_argument("--hue-bend", type=float, default=0.075)
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
    asio_reader = None
    asio_tracker = None
    if args.asio_music_channels.strip():
        if AsioOnsetReader is None:
            print("asio-music disabled: nightwing_psmove_music_pulse.AsioOnsetReader is unavailable", file=sys.stderr, flush=True)
        else:
            try:
                asio_channels = {int(part.strip()) for part in args.asio_music_channels.split(",") if part.strip()}
                asio_reader = AsioOnsetReader(args.asio_dll, args.asio_clsid, args.sample_rate, asio_channels, args.fft_size)
                asio_tracker = AuxiliaryOnsetStream("scarlett-asio", args.fps, args)
                print(f"asio-music enabled channels={sorted(asio_channels)}", file=sys.stderr, flush=True)
            except Exception as ex:
                print(f"asio-music disabled: {ex}", file=sys.stderr, flush=True)
                asio_reader = None
                asio_tracker = None
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
                auxiliary_states: list[dict[str, object]] = []
                if asio_reader is not None and asio_tracker is not None:
                    try:
                        asio_result = asio_reader.read_onset(
                            floor=args.loudness_floor,
                            gain=1.0,
                            deadline=time.monotonic() + (0.35 / args.fps),
                            whiten_fast=args.whiten_fast,
                            whiten_slow=args.whiten_slow,
                            whiten_delta_fast=args.whiten_delta_fast,
                            whiten_delta_slow=args.whiten_delta_slow,
                            whiten_decay=args.whiten_decay,
                            whiten_contrast=args.whiten_contrast,
                            fundamental_min=args.fundamental_min,
                            fundamental_max=args.fundamental_max,
                            onset_percentile_threshold=0.0,
                            onset_exponent=args.onset_exponent,
                            onset_history_ms=args.onset_history_seconds * 1000.0,
                            onset_cooldown_ms=args.onset_cooldown_seconds * 1000.0,
                            warmup_ms=args.warmup_seconds * 1000.0,
                            drain_blocks=args.asio_drain_blocks,
                        )
                        if asio_result is not None:
                            auxiliary_states.append(asio_tracker.update(now, *asio_result))
                    except Exception as ex:
                        print(f"asio-music disabled during read: {ex}", file=sys.stderr, flush=True)
                        try:
                            asio_reader.close()
                        except Exception:
                            pass
                        asio_reader = None
                        asio_tracker = None
                state = fuse_music_sources(state, auxiliary_states)
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
                    "chroma": list(state["chroma"]),  # type: ignore[index]
                    "music_sources": state["music_sources"],
                    "moves": {name: list(rgb) for name, rgb in rgbs.items()},
                }
                trace.write(json.dumps(record, sort_keys=True) + "\n")
                if frames % max(1, int(args.fps * 2)) == 0:
                    trace.flush()
                    print(
                        f"t={record['elapsed_seconds']:.1f}s level={record['level']:.3f} "
                        f"gate={record['loudness_gate']:.3f} hit={record['hit']:.3f} "
                        f"pct={record['percentile']:.3f}/{record['loudness_percentile']:.3f} "
                        f"bpm={record['bpm']:.1f}/{record['bpm_confidence']:.2f} "
                        f"key={record['key_name']} {record['key_mode']} "
                        f"chord={record['chord_name']} f0={record['fundamental_hz']:.1f}",
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
        if asio_reader is not None:
            try:
                asio_reader.close()
            except Exception:
                pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
