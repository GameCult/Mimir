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
import ctypes
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


def parse_channel_names(values: list[str] | None) -> dict[int, str]:
    names: dict[int, str] = {}
    for value in values or []:
        if not value:
            continue
        channel_text, name = value.split("=", 1)
        names[int(channel_text.strip())] = name.strip()
    return names


def midi_from_hz(hz: float) -> float:
    if hz <= 0.0:
        return 0.0
    return 69.0 + 12.0 * math.log2(hz / 440.0)


def note_name_from_midi(midi: float) -> str:
    note = int(round(midi))
    octave = note // 12 - 1
    return f"{PITCH_NAMES[note % 12]}{octave}"


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
        self.tempo_candidates: list[dict[str, float]] = []
        self.cyclic_tempogram = np.zeros(12, dtype=np.float32)
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
        f0, color, spectral_hue, spectral_concentration = self._estimate_fundamental_and_color(mag)
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
            "tempo_candidates": tuple(dict(item) for item in self.tempo_candidates),
            "cyclic_tempogram": tuple(float(value) for value in self.cyclic_tempogram),
            "beat_phase": self.beat_phase,
            "fundamental_hz": f0,
            "color_balance": color,
            "spectral_hue": spectral_hue,
            "spectral_concentration": spectral_concentration,
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
        candidates: list[dict[str, float]] = []
        tempogram = np.zeros(12, dtype=np.float32)
        for bpm in np.linspace(self.args.tempo_min_bpm, self.args.tempo_max_bpm, 161):
            lag = int(round((60.0 / float(bpm)) * self.fps))
            if lag <= 1 or lag >= len(x) - 4:
                continue
            a = x[lag:]
            b = x[:-lag]
            denom = float(np.linalg.norm(a) * np.linalg.norm(b))
            score = 0.0 if denom <= 1e-9 else float(np.dot(a, b) / denom)
            if score > 0.0:
                tempo_class = (math.log2(float(bpm) / 60.0) % 1.0) * 12.0
                tempogram[int(round(tempo_class)) % 12] += score
                candidates.append({"bpm": float(bpm), "lag_frames": float(lag), "score": score})
            if score > best_score:
                best_score = score
                best_bpm = float(bpm)
        self.bpm = 0.88 * self.bpm + 0.12 * best_bpm
        self.bpm_confidence = max(0.0, min(1.0, best_score))
        candidates.sort(key=lambda item: item["score"], reverse=True)
        self.tempo_candidates = candidates[:8]
        total = float(np.sum(tempogram))
        self.cyclic_tempogram = tempogram / total if total > 1e-6 else tempogram

    def _estimate_fundamental_and_color(self, mag: np.ndarray) -> tuple[float, tuple[float, float, float], float, float]:
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
        hue_x = 0.0
        hue_y = 0.0
        hue_weight = 0.0
        for f, m in zip(freqs[2:], mag[2:]):
            if f <= 0:
                continue
            octave = (math.log2(max(f, 1e-6) / max(best_f0, 1e-6)) % 1.0)
            energy = float(m)
            bands[int(octave * 3.0) % 3] += energy
            hue_x += math.cos(math.tau * octave) * energy
            hue_y += math.sin(math.tau * octave) * energy
            hue_weight += energy
        total = float(np.sum(bands))
        if total <= 1e-6:
            return best_f0, (0.25, 0.20, 0.55), 0.66, 0.0
        spectral_hue = (math.atan2(hue_y, hue_x) / math.tau) % 1.0 if hue_weight > 1e-6 else 0.66
        spectral_concentration = min(1.0, math.sqrt(hue_x * hue_x + hue_y * hue_y) / max(1e-6, hue_weight))
        bands /= total
        bands = np.power(np.maximum(bands, 0.0), self.args.color_contrast)
        bands /= max(1e-6, float(np.max(bands)))
        return best_f0, (float(bands[0]), float(bands[1]), float(bands[2])), float(spectral_hue), float(spectral_concentration)

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
            "role": "aux-audio",
            "hit": 0.0,
            "body": 0.0,
            "percentile": 0.0,
            "score_strength": 0.0,
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
            "role": "aux-audio",
            "hit": float(hit),
            "body": float(body),
            "percentile": float(percentile),
            "onset": float(onset),
            "score_strength": max(float(hit), float(onset), float(body)),
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


class MultiAsioScoreReader:
    def __init__(self, dll_path: str, clsid: str, sample_rate: int, channels: list[int], fps: float, fft_size: int, args: argparse.Namespace) -> None:
        self.channels = sorted(set(channels))
        self.args = args
        self.pending = {channel: [] for channel in self.channels}
        self.analyzers: dict[int, OnlineAnalyzer] = {}
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
        self.buffer = (ctypes.c_float * self.max_frames)()
        if not self.dll.mimir_asio_start(self.handle):
            self.close()
            raise RuntimeError("Could not start Mimir ASIO capture source")
        self.analyzers = {channel: OnlineAnalyzer(self.sample_rate, fps, fft_size, args) for channel in self.channels}

    def read_scores(self, deadline: float, drain_blocks: int, source_names: dict[int, str]) -> list[dict[str, object]]:
        channel = ctypes.c_int()
        timestamp_ns = ctypes.c_longlong()
        sequence = ctypes.c_ulonglong()
        frame_count = ctypes.c_int()
        reads = 0
        while reads < max(1, drain_blocks) and time.monotonic() < deadline:
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
            reads += 1
            source_channel = int(channel.value)
            if source_channel not in self.pending:
                continue
            frames = max(0, min(int(frame_count.value), self.max_frames))
            if frames == 0:
                continue
            pending = self.pending[source_channel]
            pending.extend(float(self.buffer[index]) for index in range(frames))
            keep = self.args.fft_size * 8
            if len(pending) > keep:
                del pending[: len(pending) - keep]

        now = time.monotonic()
        states: list[dict[str, object]] = []
        for source_channel in self.channels:
            pending = self.pending[source_channel]
            if len(pending) < self.args.fft_size:
                continue
            frame = np.asarray(pending[-self.args.fft_size :], dtype=np.float32)
            pending.clear()
            state = self.analyzers[source_channel].analyze(frame, now)
            strength = max(float(state["hit"]), float(state["loudness_gate"]), float(state["level"]))
            state.update(
                {
                    "source": source_names.get(source_channel, f"scarlett-asio-ch{source_channel}"),
                    "role": "audio-score-stream",
                    "channel": source_channel,
                    "score_strength": strength,
                }
            )
            states.append(state)
        return states

    def close(self) -> None:
        if getattr(self, "handle", None):
            self.dll.mimir_asio_destroy(self.handle)
            self.handle = None


def fuse_music_sources(primary: dict[str, object], source_states: list[dict[str, object]]) -> dict[str, object]:
    primary_source = {
        "source": "realtek-loopback",
        "role": "program-loopback",
        "bpm": primary["bpm"],
        "bpm_confidence": primary["bpm_confidence"],
        "tempo_candidates": primary.get("tempo_candidates", ()),
        "cyclic_tempogram": primary.get("cyclic_tempogram", ()),
        "hit": primary["hit"],
        "body": primary["body"],
        "loudness_gate": primary["loudness_gate"],
        "loudness_percentile": primary["loudness_percentile"],
        "fundamental_hz": primary["fundamental_hz"],
        "key_name": primary["key_name"],
        "key_mode": primary["key_mode"],
        "key_confidence": primary["key_confidence"],
        "chord_name": primary["chord_name"],
        "chord_confidence": primary["chord_confidence"],
        "score_strength": max(float(primary["hit"]), float(primary["loudness_gate"])),
    }
    if not source_states:
        primary["music_sources"] = [primary_source]
        primary["score_source_count"] = 1
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
    primary_source["bpm"] = primary["bpm"]
    primary_source["bpm_confidence"] = primary["bpm_confidence"]
    primary_source["tempo_candidates"] = primary.get("tempo_candidates", ())
    primary_source["cyclic_tempogram"] = primary.get("cyclic_tempogram", ())
    primary["music_sources"] = [primary_source, *source_states]
    primary["score_source_count"] = len(primary["music_sources"])
    return primary


class LiveScoreEstimator:
    def __init__(self, move_count: int, args: argparse.Namespace) -> None:
        self.args = args
        self.move_count = move_count
        self.voices: dict[str, dict[str, object]] = {}

    def update(self, state: dict[str, object], now: float) -> dict[str, object]:
        sources = state.get("music_sources", [])
        if not isinstance(sources, list):
            sources = []
        seen: set[str] = set()
        voice_records: list[dict[str, object]] = []
        for source in sources:
            if not isinstance(source, dict):
                continue
            source_id = str(source.get("source", f"source-{len(voice_records)}"))
            seen.add(source_id)
            voice = self._voice_from_source(source_id, source, now)
            if voice is not None:
                self.voices[source_id] = voice
                voice_records.append(dict(voice))

        stale_after = max(0.1, float(self.args.score_voice_release_seconds))
        for source_id in list(self.voices.keys()):
            voice = self.voices[source_id]
            if source_id in seen:
                continue
            if now - float(voice.get("last_seen", now)) > stale_after:
                del self.voices[source_id]
            else:
                voice["active"] = False
                voice_records.append(dict(voice))

        voice_records.sort(key=lambda item: float(item.get("confidence", 0.0)), reverse=True)
        active_voices = [voice for voice in voice_records if bool(voice.get("active", False))]
        confidence = self._score_confidence(state, active_voices)
        deficit = max(0.0, float(self.args.score_target_confidence) - confidence)
        move_targets = self._assign_move_targets(state, active_voices, deficit)
        return {
            "kind": "mimir.live_score.v1",
            "tempo_bpm": float(state.get("bpm", 0.0)),
            "tempo_confidence": float(state.get("bpm_confidence", 0.0)),
            "tempo_candidates": state.get("tempo_candidates", ()),
            "cyclic_tempogram": state.get("cyclic_tempogram", ()),
            "beat_phase": float(state.get("beat_phase", 0.0)),
            "key": state.get("key_name", ""),
            "mode": state.get("key_mode", ""),
            "key_confidence": float(state.get("key_confidence", 0.0)),
            "chord": state.get("chord_name", ""),
            "chord_confidence": float(state.get("chord_confidence", 0.0)),
            "voices": voice_records,
            "active_voice_count": len(active_voices),
            "confidence": confidence,
            "target_confidence": float(self.args.score_target_confidence),
            "confidence_deficit": deficit,
            "gesture_density": max(
                float(self.args.score_min_improv_density),
                min(float(self.args.score_max_improv_density), float(self.args.score_min_improv_density) + deficit * float(self.args.score_confidence_gesture_gain)),
            ),
            "move_targets": move_targets,
        }

    def _voice_from_source(self, source_id: str, source: dict[str, object], now: float) -> dict[str, object] | None:
        strength = max(
            float(source.get("score_strength", 0.0)),
            float(source.get("hit", 0.0)),
            float(source.get("loudness_gate", 0.0)),
        )
        body = float(source.get("body", 0.0))
        hz = float(source.get("fundamental_hz", 0.0))
        if strength < self.args.score_min_voice_strength and body < self.args.score_min_voice_body:
            previous = self.voices.get(source_id)
            if previous is None:
                return None
            previous["active"] = False
            previous["confidence"] = max(0.0, float(previous.get("confidence", 0.0)) * self.args.score_voice_decay)
            return previous
        midi = midi_from_hz(hz)
        note = int(round(midi))
        previous = self.voices.get(source_id, {})
        started_at = float(previous.get("started_at", now))
        if previous.get("note") != note or not bool(previous.get("active", False)):
            started_at = now
        confidence = max(
            0.0,
            min(
                1.0,
                0.48 * strength
                + 0.18 * min(1.0, body * 20.0)
                + 0.16 * float(source.get("bpm_confidence", 0.0))
                + 0.10 * float(source.get("key_confidence", 0.0))
                + 0.08 * float(source.get("chord_confidence", 0.0)),
            ),
        )
        return {
            "voice_id": source_id,
            "source": source_id,
            "active": True,
            "midi": midi,
            "note": note,
            "note_name": note_name_from_midi(midi),
            "frequency_hz": hz,
            "cents": (midi - note) * 100.0,
            "strength": strength,
            "confidence": confidence,
            "started_at": started_at,
            "last_seen": now,
            "duration_seconds": max(0.0, now - started_at),
            "chord": source.get("chord_name", ""),
            "key": source.get("key_name", ""),
            "role": source.get("role", ""),
        }

    def _score_confidence(self, state: dict[str, object], voices: list[dict[str, object]]) -> float:
        voice_confidence = 0.0
        if voices:
            weights = [float(voice.get("confidence", 0.0)) for voice in voices]
            voice_confidence = sum(weights[:4]) / min(4, max(1, len(weights)))
        return max(
            0.0,
            min(
                1.0,
                0.30 * float(state.get("bpm_confidence", 0.0))
                + 0.25 * float(state.get("key_confidence", 0.0))
                + 0.20 * float(state.get("chord_confidence", 0.0))
                + 0.25 * voice_confidence,
            ),
        )

    def _assign_move_targets(self, state: dict[str, object], voices: list[dict[str, object]], deficit: float) -> list[dict[str, object]]:
        chord_root = int(state.get("chord_root", 0))
        key_mode = str(state.get("key_mode", "major"))
        scale = (0, 3, 5, 7, 10, 12, 15) if key_mode == "minor" else (0, 2, 4, 7, 9, 11, 14)
        targets: list[dict[str, object]] = []
        for index in range(self.move_count):
            if voices:
                voice = voices[index % len(voices)]
                base_note = int(voice.get("note", chord_root + scale[index % len(scale)]))
                confidence = float(voice.get("confidence", 0.0))
                source = str(voice.get("source", "score"))
            else:
                base_note = chord_root + scale[index % len(scale)] + 12 * ((index // len(scale)) % 2)
                confidence = 0.0
                source = "score-fill"
            spread = int(round(deficit * 12.0))
            target_note = float(base_note + (index - (self.move_count - 1) * 0.5) * max(0, spread) / max(1, self.move_count))
            targets.append(
                {
                    "move_index": index,
                    "source": source,
                    "target_note": target_note,
                    "note_name": note_name_from_midi(target_note),
                    "confidence": confidence,
                    "calibration_priority": max(0.0, min(1.0, deficit + (1.0 - confidence) * 0.35)),
                    "spectral_lane": index / max(1, self.move_count),
                }
            )
        return targets


class ScoreGestureScheduler:
    def __init__(self, move_count: int, args: argparse.Namespace) -> None:
        self.args = args
        self.envelopes = [0.0 for _ in range(move_count)]
        self.last_slots = [-1 for _ in range(move_count)]
        self.last_score_slot = -1
        self.score_slot_counter = 0
        self.pending_strikes: list[dict[str, object]] = []
        self.voice_contours = [
            {
                "active": False,
                "previous_note": 0.0,
                "target_note": 0.0,
                "current_note": 0.0,
                "glide": 1.0,
                "vibrato": 0.0,
                "harmonic": 1.0,
                "intensity": 0.0,
            }
            for _ in range(move_count)
        ]
        self.vibrato_phases = [0.0 for _ in range(move_count)]

    def update(self, state: dict[str, object]) -> tuple[float, ...]:
        self.pending_strikes = []
        beat = float(state.get("beat_phase", 0.0))
        slot_count = max(1, int(self.args.score_subdivisions))
        slot = int(math.floor((beat % 1.0) * slot_count))
        if slot != self.last_score_slot:
            self.last_score_slot = slot
            self.score_slot_counter += 1
        self._advance_voice_contours()
        loudness_rank = float(state.get("loudness_percentile", 0.0))
        loudness_gate = float(state.get("loudness_gate", 0.0))
        hit = float(state.get("hit", 0.0))
        confidence = float(state.get("bpm_confidence", 0.0))
        key_confidence = float(state.get("key_confidence", 0.0))
        chord_confidence = float(state.get("chord_confidence", 0.0))
        live_score = state.get("live_score", {})
        live_score_confidence = float(live_score.get("confidence", 0.0)) if isinstance(live_score, dict) else 0.0
        live_score_deficit = float(live_score.get("confidence_deficit", 0.0)) if isinstance(live_score, dict) else 0.0
        chord_root = int(state.get("chord_root", 0))
        key_mode = str(state.get("key_mode", "major"))
        flux_rank = float(state.get("percentile", 0.0))
        onset_intensity = max(flux_rank ** self.args.syrinx_onset_exponent, hit, loudness_gate * self.args.score_loudness_weight)
        score_lock = min(1.0, confidence * 0.30 + key_confidence * 0.22 + chord_confidence * 0.18 + live_score_confidence * 0.30)
        loudness_can_play = (
            loudness_rank >= self.args.score_loudness_threshold
            and loudness_gate >= self.args.score_min_loudness_gate
        )
        onset_can_play = (
            flux_rank >= self.args.score_min_flux_percentile
            or onset_intensity >= self.args.score_min_onset_intensity
        )
        can_play = score_lock >= self.args.score_min_music_confidence and (loudness_can_play or onset_can_play)
        loud_span = max(1e-6, 1.0 - self.args.score_loudness_threshold)
        loud_accent = max(0.0, min(1.0, (loudness_rank - self.args.score_loudness_threshold) / loud_span))
        rise_accent = max(loud_accent, loudness_gate, onset_intensity * (0.45 + 0.55 * score_lock))
        onset_accent = 0.35 + 0.65 * min(1.0, hit + onset_intensity * score_lock)
        ensemble_accent = (
            can_play
            and score_lock >= self.args.score_ensemble_min_music_confidence
            and (
                (
                    loudness_rank >= self.args.score_ensemble_loudness_threshold
                    and loudness_gate >= self.args.score_ensemble_min_loudness_gate
                )
                or onset_intensity >= self.args.score_ensemble_min_onset_intensity
            )
        )
        for index in range(len(self.envelopes)):
            self.envelopes[index] *= self.args.score_release
            if slot == self.last_slots[index]:
                continue
            self.last_slots[index] = slot
            if not can_play:
                continue
            if not ensemble_accent and not self._slot_belongs_to_instrument(slot, index):
                continue
            if not ensemble_accent and not self._improv_allows(slot, index, score_lock, onset_intensity, live_score):
                continue
            accent = (rise_accent ** self.args.score_loudness_exponent) * onset_accent
            if ensemble_accent:
                accent = max(accent, self.args.score_ensemble_min_accent)
            if live_score_deficit > 0.0:
                accent = min(1.0, accent * (1.0 + live_score_deficit * self.args.score_confidence_accent_gain))
            if accent < self.args.score_min_accent:
                continue
            lane_scale = 1.0 if ensemble_accent else (0.72 + 0.28 * debruijn_accent(beat, index, self.args))
            self.envelopes[index] = max(self.envelopes[index], min(self.args.score_max_envelope, accent * lane_scale))
            self._strike_voice(index, chord_root, key_mode, score_lock, onset_intensity, ensemble_accent, live_score)
        return tuple(float(value) for value in self.envelopes)

    def _slot_belongs_to_instrument(self, slot: int, move_index: int) -> bool:
        if self.args.debruijn_polyrhythm:
            bit_index = (slot + move_index * 3) % len(DEBRUIJN_2_3)
            if DEBRUIJN_2_3[bit_index] == "0":
                return False
        spacing = max(1, int(self.args.score_instrument_spacing))
        return (slot + move_index) % spacing == 0

    def _improv_allows(self, slot: int, move_index: int, score_lock: float, onset_intensity: float, live_score: object) -> bool:
        if slot == 0 and onset_intensity >= self.args.score_downbeat_min_onset:
            return True
        move_count = max(1, len(self.envelopes))
        if onset_intensity >= self.args.score_call_response_min_onset:
            return (self.score_slot_counter + move_index) % move_count == 0
        live_density = 0.0
        if isinstance(live_score, dict):
            live_density = float(live_score.get("gesture_density", 0.0))
        density = max(live_density, self.args.score_min_improv_density + (self.args.score_max_improv_density - self.args.score_min_improv_density) * score_lock)
        density *= 0.45 + 0.55 * max(0.0, min(1.0, onset_intensity))
        cursor = math.sin((slot + 1) * 12.9898 + (move_index + 1) * 78.233) * 43758.5453
        return (cursor - math.floor(cursor)) < max(0.0, min(1.0, density))

    def _advance_voice_contours(self) -> None:
        glide_step = max(0.001, self.args.voice_glide_rate / max(1.0, self.args.fps))
        for index, contour in enumerate(self.voice_contours):
            contour["glide"] = min(1.0, float(contour["glide"]) + glide_step)
            contour["intensity"] = max(0.0, float(contour["intensity"]) * self.args.score_release)
            self.vibrato_phases[index] = (self.vibrato_phases[index] + self.args.voice_vibrato_hz / max(1.0, self.args.fps)) % 1.0
            glide = smoothstep(float(contour["glide"]))
            previous_note = float(contour["previous_note"])
            target_note = float(contour["target_note"])
            vibrato = math.sin(2.0 * math.pi * self.vibrato_phases[index])
            contour["vibrato"] = vibrato
            contour["current_note"] = previous_note + (target_note - previous_note) * glide + (self.args.voice_vibrato_cents / 100.0) * vibrato
            contour["active"] = bool(float(contour["intensity"]) > 0.001)

    def _strike_voice(
        self,
        move_index: int,
        chord_root: int,
        key_mode: str,
        score_lock: float,
        onset_intensity: float,
        ensemble_accent: bool,
        live_score: object,
    ) -> None:
        major_degrees = (0, 2, 4, 7, 9, 11, 14)
        minor_degrees = (0, 3, 5, 7, 10, 12, 15)
        degrees = minor_degrees if key_mode == "minor" else major_degrees
        phrase_step = self.score_slot_counter + move_index * 2
        degree = degrees[phrase_step % len(degrees)]
        octave = 12 * ((phrase_step // len(degrees)) % 2)
        target = float(chord_root + degree + octave)
        target_source = "chord-scale"
        if isinstance(live_score, dict):
            targets = live_score.get("move_targets", [])
            if isinstance(targets, list) and move_index < len(targets) and isinstance(targets[move_index], dict):
                target = float(targets[move_index].get("target_note", target))
                target_source = str(targets[move_index].get("source", target_source))
        contour = self.voice_contours[move_index]
        contour["previous_note"] = float(contour["current_note"])
        contour["target_note"] = target
        contour["glide"] = 0.0
        contour["harmonic"] = self.args.harmonic_base ** (move_index / max(1, len(self.envelopes))) * (2.0 if ensemble_accent else 1.0)
        contour["intensity"] = max(float(contour["intensity"]), min(1.0, onset_intensity * (0.55 + 0.45 * score_lock)))
        contour["active"] = True
        self.pending_strikes.append(
            {
                "kind": "mimir.syrinx_move_witness_event.v1",
                "move_index": move_index,
                "score_slot": self.score_slot_counter,
                "target_note": target,
                "target_note_name": note_name_from_midi(target),
                "target_source": target_source,
                "previous_note": contour["previous_note"],
                "harmonic": contour["harmonic"],
                "intensity": contour["intensity"],
                "score_lock": score_lock,
                "onset_intensity": onset_intensity,
                "ensemble": ensemble_accent,
            }
        )


DEBRUIJN_2_3 = "00010111"


def smoothstep(value: float) -> float:
    x = max(0.0, min(1.0, value))
    return x * x * (3.0 - 2.0 * x)


def debruijn_accent(frame_phase: float, move_index: int, args: argparse.Namespace) -> float:
    if not args.debruijn_polyrhythm:
        return 1.0
    lane_rate = args.debruijn_rate + move_index
    cursor = int(math.floor(frame_phase * max(1, lane_rate))) % len(DEBRUIJN_2_3)
    bit = 1.0 if DEBRUIJN_2_3[(cursor + move_index) % len(DEBRUIJN_2_3)] == "1" else 0.0
    return 0.58 + 0.42 * bit


def move_rgb(move: Move, move_index: int, move_count: int, state: dict[str, object], args: argparse.Namespace) -> tuple[int, int, int]:
    envelopes = state.get("score_gesture_envelopes", ())
    score_envelope = float(envelopes[move_index]) if isinstance(envelopes, tuple) and move_index < len(envelopes) else 0.0
    if score_envelope <= 0.0005:
        return (0, 0, 0)
    level = float(state["level"])
    beat = float(state["beat_phase"])
    hit = float(state["hit"])
    loudness_gate = float(state.get("loudness_gate", 0.0))
    loudness_percentile = float(state.get("loudness_percentile", 0.0))
    base_h, base_s, _ = rgb_to_hsv01(move.base_rgb)
    spectral = state["color_balance"]
    assert isinstance(spectral, tuple)
    spectral_hue = float(state.get("spectral_hue", base_h))
    spectral_concentration = float(state.get("spectral_concentration", 0.0))
    voice_contours = state.get("score_voice_contours", ())
    voice = voice_contours[move_index] if isinstance(voice_contours, list) and move_index < len(voice_contours) else {}
    harmonic = args.harmonic_base ** (move_index / max(1, move_count))
    micro = 2.0 ** ((move_index - (move_count - 1) * 0.5) * args.microtonal_cents / 1200.0)
    phase = (beat * harmonic * micro + move_index / max(1, move_count)) % 1.0
    poly = debruijn_accent(beat, move_index, args)
    gesture = poly * (0.42 + 0.58 * (0.5 + 0.5 * math.sin(2.0 * math.pi * phase)) ** 2.1)
    score_lock = min(
        1.0,
        float(state.get("bpm_confidence", 0.0)) * 0.45
        + float(state.get("key_confidence", 0.0)) * 0.35
        + float(state.get("chord_confidence", 0.0)) * 0.20,
    )
    onset_intensity = max(float(state.get("percentile", 0.0)) ** args.syrinx_onset_exponent, hit, loudness_gate * 0.72) * (0.35 + 0.65 * score_lock)
    accent = min(1.0, score_envelope * max(0.24, onset_intensity) * (0.42 + level * gesture + hit * (0.22 + 0.08 * poly)))
    lane_offset = (move_index - (move_count - 1) * 0.5) * args.syrinx_lane_hue_spread
    hue = (
        spectral_hue
        + lane_offset
        + args.hue_bend * spectral_concentration * math.sin(2.0 * math.pi * phase)
        + math.log2(harmonic * micro) * 0.0417
    ) % 1.0
    if isinstance(voice, dict) and voice.get("active"):
        current_note = float(voice.get("current_note", 0.0))
        note_hue = (current_note % 12.0) / 12.0
        hue = mix_hue(hue, note_hue, args.voice_note_hue_mix)
        hue = (hue + args.voice_vibrato_hue_width * float(voice.get("vibrato", 0.0))) % 1.0
    chord_confidence = float(state.get("chord_confidence", 0.0))
    chord_root = int(state.get("chord_root", 0))
    if chord_confidence > args.chord_hue_threshold:
        chord_degrees = (0, 4, 7, 10)
        pitch_class = (chord_root + chord_degrees[move_index % len(chord_degrees)]) % 12
        pitch_hue = pitch_class / 12.0
        mix = min(args.chord_hue_mix, chord_confidence * args.chord_hue_mix)
        hue = mix_hue(hue, pitch_hue, mix)
    voice_intensity = float(voice.get("intensity", 0.0)) if isinstance(voice, dict) else 0.0
    sat = min(1.0, max(0.78, base_s) + 0.16 * spectral_concentration + 0.10 * onset_intensity + 0.08 * voice_intensity)
    if onset_intensity <= 0.0:
        val = 0.0
    else:
        val = min(args.max_brightness, max(args.quiet_brightness, accent ** args.brightness_exponent))
        if loudness_percentile < args.loudness_threshold and onset_intensity < args.score_min_onset_intensity:
            val = min(val, args.quiet_brightness)
    rr, gg, bb = colorsys.hsv_to_rgb(hue, sat, val)
    r = rr * (0.45 + 0.55 * float(spectral[0]))
    g = gg * (0.45 + 0.55 * float(spectral[1]))
    b = bb * (0.45 + 0.55 * float(spectral[2]))
    if isinstance(voice, dict) and voice.get("active"):
        harmonic_content = min(1.0, abs(math.sin(float(voice.get("harmonic", 1.0)) * math.pi * phase)))
        r *= 0.86 + 0.14 * harmonic_content
        g *= 0.90 + 0.10 * (1.0 - harmonic_content)
        b *= 0.84 + 0.16 * harmonic_content
    white = score_envelope * loudness_gate * max(0.0, hit - 0.97) / 0.03
    return (
        int(min(255, 255 * r + 18 * white)),
        int(min(255, 255 * g + 18 * white)),
        int(min(255, 255 * b + 18 * white)),
    )


def mix_hue(a: float, b: float, amount: float) -> float:
    delta = ((b - a + 0.5) % 1.0) - 0.5
    return (a + delta * max(0.0, min(1.0, amount))) % 1.0


class BioacousticRealtekTrigger:
    def __init__(self, args: argparse.Namespace, out_dir: Path) -> None:
        self.args = args
        self.out_dir = out_dir
        self.render_path = out_dir / "bioacoustic-syrinx-f32.raw"
        self.processes: deque[subprocess.Popen[bytes]] = deque()
        self.last_trigger = 0.0
        self.stdout = (out_dir / "bioacoustic-realtk.out.log").open("ab")
        self.stderr = (out_dir / "bioacoustic-realtk.err.log").open("ab")

    @classmethod
    def create(cls, args: argparse.Namespace, out_dir: Path) -> "BioacousticRealtekTrigger | None":
        if not args.emit_bioacoustic_realtk:
            return None
        trigger = cls(args, out_dir)
        render_cmd = [
            "dotnet",
            "run",
            "--project",
            str(Path(__file__).resolve().parents[1] / "src" / "Mimir.BufferSmoke" / "Mimir.BufferSmoke.csproj"),
            "--",
            "--render-contestant-f32",
            "--output",
            str(trigger.render_path),
            "--sample-rate",
            str(args.sample_rate),
            "--seconds",
            str(args.bioacoustic_loop_seconds),
            "--song",
            args.bioacoustic_song,
        ]
        render = subprocess.run(render_cmd, cwd=Path(__file__).resolve().parents[1], capture_output=True, text=True)
        (out_dir / "bioacoustic-render.out.log").write_text(render.stdout, encoding="utf-8")
        (out_dir / "bioacoustic-render.err.log").write_text(render.stderr, encoding="utf-8")
        if render.returncode != 0 or not trigger.render_path.exists():
            trigger.close()
            print(f"bioacoustic Realtek trigger disabled: render failed code={render.returncode}", file=sys.stderr, flush=True)
            return None
        return trigger

    def trigger(self, event: dict[str, object], now: float) -> bool:
        self._reap()
        if now - self.last_trigger < self.args.bioacoustic_min_interval_seconds:
            return False
        self.last_trigger = now
        gain = float(self.args.bioacoustic_gain) * max(0.12, min(1.0, float(event.get("intensity", 0.0))))
        cmd = [
            str(Path(self.args.wasapi).resolve()),
            "--play-f32-mono",
            "--input",
            str(self.render_path.resolve()),
            "--device",
            self.args.bioacoustic_device,
            "--sample-rate",
            str(int(self.args.sample_rate)),
            "--gain",
            f"{gain:.4f}",
        ]
        proc = subprocess.Popen(cmd, cwd=Path(__file__).resolve().parents[1], stdout=self.stdout, stderr=self.stderr)
        self.processes.append(proc)
        while len(self.processes) > self.args.bioacoustic_max_active_calls:
            old = self.processes.popleft()
            if old.poll() is None:
                old.terminate()
        return True

    def _reap(self) -> None:
        self.processes = deque(proc for proc in self.processes if proc.poll() is None)

    def close(self) -> None:
        for proc in list(self.processes):
            if proc.poll() is None:
                proc.terminate()
        time.sleep(0.05)
        for proc in list(self.processes):
            if proc.poll() is None:
                proc.kill()
        self.stdout.close()
        self.stderr.close()


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
    parser.add_argument("--asio-music-source-name", action="append", default=[], help="Optional ASIO channel source label, as channel=name.")
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
    parser.add_argument("--quiet-brightness", type=float, default=0.0)
    parser.add_argument("--max-brightness", type=float, default=0.34)
    parser.add_argument("--brightness-exponent", type=float, default=0.88)
    parser.add_argument("--score-subdivisions", type=int, default=8)
    parser.add_argument("--score-instrument-spacing", type=int, default=2)
    parser.add_argument("--score-release", type=float, default=0.68)
    parser.add_argument("--score-loudness-threshold", type=float, default=0.90)
    parser.add_argument("--score-min-loudness-gate", type=float, default=0.08)
    parser.add_argument("--score-loudness-exponent", type=float, default=1.8)
    parser.add_argument("--score-loudness-weight", type=float, default=0.28)
    parser.add_argument("--score-min-flux-percentile", type=float, default=0.92)
    parser.add_argument("--score-min-onset-intensity", type=float, default=0.42)
    parser.add_argument("--score-min-music-confidence", type=float, default=0.30)
    parser.add_argument("--score-min-improv-density", type=float, default=0.04)
    parser.add_argument("--score-max-improv-density", type=float, default=0.22)
    parser.add_argument("--score-target-confidence", type=float, default=0.78)
    parser.add_argument("--score-confidence-gesture-gain", type=float, default=0.34)
    parser.add_argument("--score-confidence-accent-gain", type=float, default=0.55)
    parser.add_argument("--score-min-voice-strength", type=float, default=0.08)
    parser.add_argument("--score-min-voice-body", type=float, default=0.004)
    parser.add_argument("--score-voice-release-seconds", type=float, default=1.2)
    parser.add_argument("--score-voice-decay", type=float, default=0.72)
    parser.add_argument("--score-downbeat-min-onset", type=float, default=0.62)
    parser.add_argument("--score-call-response-min-onset", type=float, default=0.48)
    parser.add_argument("--score-min-accent", type=float, default=0.025)
    parser.add_argument("--score-max-envelope", type=float, default=0.78)
    parser.add_argument("--score-ensemble-loudness-threshold", type=float, default=0.96)
    parser.add_argument("--score-ensemble-min-loudness-gate", type=float, default=0.25)
    parser.add_argument("--score-ensemble-min-onset-intensity", type=float, default=0.86)
    parser.add_argument("--score-ensemble-min-music-confidence", type=float, default=0.55)
    parser.add_argument("--score-ensemble-min-accent", type=float, default=0.30)
    parser.add_argument("--score-min-tempo-confidence", type=float, default=0.0)
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
    parser.add_argument("--syrinx-onset-exponent", type=float, default=1.55)
    parser.add_argument("--syrinx-lane-hue-spread", type=float, default=0.055)
    parser.add_argument("--voice-glide-rate", type=float, default=3.4)
    parser.add_argument("--voice-vibrato-hz", type=float, default=5.2)
    parser.add_argument("--voice-vibrato-cents", type=float, default=18.0)
    parser.add_argument("--voice-note-hue-mix", type=float, default=0.46)
    parser.add_argument("--voice-vibrato-hue-width", type=float, default=0.012)
    parser.add_argument("--emit-bioacoustic-realtk", action="store_true", help="Loop a rendered Mimir bioacoustic/Syrinx call on the Realtek output so the Well decoders can place it on the timeline.")
    parser.add_argument("--bioacoustic-song", default="aquasynth-formant-weaver")
    parser.add_argument("--bioacoustic-device", default="Realtek")
    parser.add_argument("--bioacoustic-gain", type=float, default=1.05)
    parser.add_argument("--bioacoustic-loop-seconds", type=float, default=0.42)
    parser.add_argument("--bioacoustic-min-interval-seconds", type=float, default=0.18)
    parser.add_argument("--bioacoustic-max-active-calls", type=int, default=3)
    args = parser.parse_args()

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    moves = [parse_move(value) for value in args.move]
    (out_dir / "moves.json").write_text(json.dumps([move.__dict__ for move in moves], indent=2), encoding="utf-8")
    bioacoustic_trigger = BioacousticRealtekTrigger.create(args, out_dir)

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
    score_estimator = LiveScoreEstimator(len(moves), args)
    score_scheduler = ScoreGestureScheduler(len(moves), args)
    asio_reader = None
    asio_source_names = parse_channel_names(args.asio_music_source_name)
    if args.asio_music_channels.strip():
        try:
            asio_channels = [int(part.strip()) for part in args.asio_music_channels.split(",") if part.strip()]
            asio_reader = MultiAsioScoreReader(args.asio_dll, args.asio_clsid, args.sample_rate, asio_channels, args.fps, args.fft_size, args)
            print(
                f"asio-music enabled channels={asio_reader.channels} sampleRate={asio_reader.sample_rate} inputs={asio_reader.input_count}",
                file=sys.stderr,
                flush=True,
            )
        except Exception as ex:
            print(f"asio-music disabled: {ex}", file=sys.stderr, flush=True)
            asio_reader = None
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
                if asio_reader is not None:
                    try:
                        auxiliary_states.extend(asio_reader.read_scores(
                            deadline=time.monotonic() + (0.35 / args.fps),
                            drain_blocks=args.asio_drain_blocks,
                            source_names=asio_source_names,
                        ))
                    except Exception as ex:
                        print(f"asio-music disabled during read: {ex}", file=sys.stderr, flush=True)
                        try:
                            asio_reader.close()
                        except Exception:
                            pass
                        asio_reader = None
                state = fuse_music_sources(state, auxiliary_states)
                state["live_score"] = score_estimator.update(state, now)
                state["score_gesture_envelopes"] = score_scheduler.update(state)
                state["score_voice_contours"] = score_scheduler.voice_contours
                score_voice_events = [dict(event) for event in score_scheduler.pending_strikes]
                emitted_audio_events = []
                if bioacoustic_trigger is not None:
                    for event in score_voice_events:
                        if bioacoustic_trigger.trigger(event, now):
                            emitted_audio_events.append(event)
                rgbs = {}
                for index, move in enumerate(moves):
                    rgb = move_rgb(move, index, len(moves), state, args)
                    rgbs[move.name] = rgb
                    ssh.stdin.write(f"{move.name} {rgb[0]} {rgb[1]} {rgb[2]}\n".encode("ascii"))
                for event in score_voice_events:
                    move_index = int(event["move_index"])
                    if 0 <= move_index < len(moves):
                        event["move_name"] = moves[move_index].name
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
                    "live_score": state["live_score"],
                    "score_gesture_envelopes": list(state["score_gesture_envelopes"]),  # type: ignore[index]
                    "score_voice_contours": state["score_voice_contours"],
                    "score_voice_events": score_voice_events,
                    "emitted_audio_event_count": len(emitted_audio_events),
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
        if bioacoustic_trigger is not None:
            bioacoustic_trigger.close()
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
