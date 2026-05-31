#!/usr/bin/env python3
"""Emit beat/key-aligned chirps and unique PS Move color pulses.

This is the musical calibration word planner for the current Mimir/Nightwing
setup. It uses the same Perlines-style adaptive whitening onset reader as
``nightwing_psmove_music_pulse.py``, estimates tempo from autocorrelation of
the whitened broadband spectral-rise function, estimates a lightweight root
from recent best-fit fundamentals, then schedules chirps on the beat in that
scale while pulsing each Move with a unique coded color.

Provenance:
- The onset whitening path is intentionally shared with
  ``tools/nightwing_psmove_music_pulse.py`` and its Perlines-derived adaptive
  FFT delta whitening.
- The beat/key feature vocabulary mirrors AquaSynth song challenge analysis:
  tempo_bpm, beat_seconds, root_note, suggested_scale, scale_frequencies_hz,
  and whitened-spectral-autocorr.
- The event codebook reuses the de Bruijn schedule idea already present in
  ``tools/move_latency_probe.py``.
"""

from __future__ import annotations

import argparse
import base64
import json
import math
import os
import colorsys
import subprocess
import sys
import time
import wave
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from nightwing_psmove_music_pulse import AsioOnsetReader


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
    fields = line.split()
    if len(fields) != 5:
        continue
    offset, name, r, g, b = fields
    events.append((float(offset), name, int(r), int(g), int(b)))

log = os.path.expanduser("~/.local/state/gamecult/codex-ssh-activity.log")
os.makedirs(os.path.dirname(log), exist_ok=True)
with open(log, "a", encoding="utf-8") as f:
    f.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S%z')} Codex: multi-Move beat/key chirp receiver armed for {len(moves)} Moves.\n")

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

time.sleep(0.08)
for path in moves.values():
    write_rgb(path, 0, 0, 0)
"""


NOTE_NAMES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
MINOR_PENTATONIC = [0, 3, 5, 7, 10, 12, 15, 17, 19, 22, 24]


@dataclass(frozen=True)
class MoveTarget:
    name: str
    hidraw: str
    color: tuple[int, int, int]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("live", "synthetic"), default="live")
    parser.add_argument("--out-dir", default="artifacts/runtime/music-keyed-move-chirp-sync")
    parser.add_argument("--ffplay", default=str(Path(r"C:\Users\Meta\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffplay.exe")))
    parser.add_argument("--ssh-target", default="nightwing")
    parser.add_argument(
        "--move",
        action="append",
        default=[],
        help="Move spec name=/dev/hidrawN:#rrggbb. Defaults to the two wireless Nightwing Moves.",
    )
    parser.add_argument("--analyze-seconds", type=float, default=8.0)
    parser.add_argument("--events", type=int, default=16)
    parser.add_argument("--lead-seconds", type=float, default=0.55)
    parser.add_argument("--chirp-ms", type=float, default=55.0)
    parser.add_argument("--pulse-ms", type=float, default=90.0)
    parser.add_argument("--visual-gesture", choices=("square", "contour"), default="contour")
    parser.add_argument("--visual-gesture-hz", type=float, default=80.0)
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--fps", type=float, default=60.0)
    parser.add_argument("--fft-size", type=int, default=512)
    parser.add_argument("--drain-blocks", type=int, default=4096)
    parser.add_argument("--tempo-min-bpm", type=float, default=60.0)
    parser.add_argument("--tempo-max-bpm", type=float, default=200.0)
    parser.add_argument("--tempo-prefer-min-bpm", type=float, default=75.0)
    parser.add_argument("--tempo-prefer-max-bpm", type=float, default=165.0)
    parser.add_argument(
        "--tempo-grid",
        choices=("preferred", "strongest", "doubletime"),
        default="preferred",
        help="Beat grid selection. Use doubletime for dense drum-and-bass/dubstep style calibration pulses.",
    )
    parser.add_argument("--dry-run", action="store_true", help="Write the plan and WAV without playing or touching Moves.")
    parser.add_argument("--source-gain", type=float, default=0.35)
    parser.add_argument("--asio-dll", default=str(Path(__file__).resolve().parents[1] / "native" / "asio_capture" / "build" / "Release" / "mimir_asio_capture.dll"))
    parser.add_argument("--asio-clsid", default="{AC4D0455-50D7-4498-B3CD-9A41D130B759}")
    parser.add_argument("--asio-channels", default="2,3")
    return parser.parse_args()


def parse_moves(values: list[str]) -> list[MoveTarget]:
    specs = values or [
        "move-00-07-04-a6-be-5f=/dev/hidraw2:#ff2a00",
        "move-00-06-f5-23-e2-d1=/dev/hidraw3:#00a8ff",
    ]
    moves: list[MoveTarget] = []
    for spec in specs:
        name_path, color_text = spec.split(":", 1)
        name, path = name_path.split("=", 1)
        color_text = color_text.lstrip("#")
        if len(color_text) != 6:
            raise ValueError(f"invalid color in move spec: {spec}")
        color = tuple(int(color_text[index : index + 2], 16) for index in (0, 2, 4))
        moves.append(MoveTarget(name, path, color))  # type: ignore[arg-type]
    return moves


def build_debruijn(alphabet_size: int, order: int) -> list[int]:
    sequence: list[int] = []
    a = [0] * (alphabet_size * order)

    def db(t: int, p: int) -> None:
        if t > order:
            if order % p == 0:
                sequence.extend(a[1 : p + 1])
            return
        a[t] = a[t - p]
        db(t + 1, p)
        for value in range(a[t - p] + 1, alphabet_size):
            a[t] = value
            db(t + 1, t)

    db(1, 1)
    return sequence


def rotate_to_distinct_opening(symbols: list[int], order: int) -> list[int]:
    if len(symbols) <= order:
        return symbols
    windows: set[tuple[int, ...]] = set()
    for index in range(len(symbols)):
        window = tuple(symbols[(index + offset) % len(symbols)] for offset in range(order))
        if window in windows:
            continue
        windows.add(window)
        if len(set(window)) == min(order, len(set(symbols))):
            return symbols[index:] + symbols[:index]
    return symbols


def synthetic_analysis(args: argparse.Namespace) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    frame_count = max(32, int(args.analyze_seconds * args.fps))
    times = np.arange(frame_count, dtype=np.float32) / args.fps
    beat_seconds = 0.5
    onset = np.zeros(frame_count, dtype=np.float32)
    for beat in np.arange(0.25, args.analyze_seconds, beat_seconds):
        onset += np.exp(-((times - beat) ** 2) / 0.0009).astype(np.float32)
    fundamentals = 220.0 + 8.0 * np.sin(times * 2.0 * math.pi * 0.5)
    weights = np.maximum(onset, 0.05).astype(np.float32)
    return times, onset, fundamentals.astype(np.float32) * weights


def live_analysis(args: argparse.Namespace) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    channels = {int(part.strip()) for part in args.asio_channels.split(",") if part.strip()}
    reader = AsioOnsetReader(args.asio_dll, args.asio_clsid, args.sample_rate, channels, args.fft_size)
    times: list[float] = []
    onset_scores: list[float] = []
    weighted_fundamentals: list[float] = []
    start = time.monotonic()
    try:
        while time.monotonic() - start < args.analyze_seconds:
            result = reader.read_onset(
                floor=0.008,
                gain=1.0,
                deadline=time.monotonic() + (1.0 / args.fps),
                whiten_fast=0.78,
                whiten_slow=0.12,
                whiten_delta_fast=0.62,
                whiten_delta_slow=0.08,
                whiten_decay=0.78,
                whiten_contrast=3.4,
                fundamental_min=55.0,
                fundamental_max=880.0,
                onset_percentile_threshold=0.0,
                onset_exponent=2.0,
                onset_history_ms=4500.0,
                onset_cooldown_ms=0.0,
                warmup_ms=450.0,
                drain_blocks=args.drain_blocks,
            )
            if result is None:
                continue
            hit, _body, _balance, fundamental, range_hit, percentile = result
            score = max(hit, (max(0.0, min(1.0, percentile)) ** 2) * max(0.2, min(1.5, range_hit)))
            times.append(time.monotonic() - start)
            onset_scores.append(score)
            weighted_fundamentals.append(fundamental * max(score, 0.0))
    finally:
        reader.close()
    if len(onset_scores) < 8:
        raise RuntimeError("not enough live onset frames collected")
    return (
        np.asarray(times, dtype=np.float32),
        np.asarray(onset_scores, dtype=np.float32),
        np.asarray(weighted_fundamentals, dtype=np.float32),
    )


def estimate_tempo(args: argparse.Namespace, onset: np.ndarray, fps: float) -> tuple[float, float, float, list[dict[str, float]]]:
    centered = np.maximum(onset - float(np.mean(onset)), 0.0)
    energy = float(np.dot(centered, centered))
    if energy <= 1e-8:
        return 120.0, 0.5, 0.0, []
    min_bpm = max(20.0, min(args.tempo_min_bpm, args.tempo_max_bpm))
    max_bpm = max(min_bpm + 1.0, max(args.tempo_min_bpm, args.tempo_max_bpm))
    prefer_min = max(min_bpm, min(args.tempo_prefer_min_bpm, args.tempo_prefer_max_bpm))
    prefer_max = min(max_bpm, max(args.tempo_prefer_min_bpm, args.tempo_prefer_max_bpm))
    min_lag = max(1, int(math.floor(fps * 60.0 / max_bpm)))
    max_lag = min(len(centered) - 1, int(math.ceil(fps * 60.0 / min_bpm)))
    candidates: list[dict[str, float]] = []
    for lag in range(min_lag, max_lag + 1):
        score = float(np.dot(centered[:-lag], centered[lag:]) / energy)
        bpm = 60.0 / (lag / fps)
        if prefer_min <= bpm <= prefer_max:
            tempo_weight = 1.0
        else:
            distance = min(abs(bpm - prefer_min), abs(bpm - prefer_max))
            tempo_weight = max(0.60, 1.0 - distance / 160.0)
        candidates.append({"lag": float(lag), "bpm": bpm, "score": max(0.0, score), "weighted_score": max(0.0, score) * tempo_weight})
    if args.tempo_grid == "strongest":
        best = max(candidates, key=lambda item: item["score"], default={"lag": fps * 0.5, "bpm": 120.0, "score": 0.0})
    elif args.tempo_grid == "doubletime":
        parent = max(candidates, key=lambda item: item["weighted_score"], default={"lag": fps * 0.5, "bpm": 120.0, "score": 0.0})
        target = min(max_bpm, max(min_bpm, parent["bpm"] * 2.0))
        nearby = [
            item for item in candidates
            if abs(item["bpm"] - target) <= max(6.0, target * 0.08) and item["score"] >= parent["score"] * 0.45
        ]
        best = max(nearby, key=lambda item: item["score"], default=min(candidates, key=lambda item: abs(item["bpm"] - target)))
    else:
        best = max(candidates, key=lambda item: item["weighted_score"], default={"lag": fps * 0.5, "bpm": 120.0, "score": 0.0})
    beat_seconds = float(best["lag"] / fps)
    return float(best["bpm"]), beat_seconds, float(max(0.0, min(1.0, best["score"]))), candidates


def estimate_root(weighted_fundamentals: np.ndarray, onset: np.ndarray) -> tuple[int, str, float]:
    chroma = np.zeros(12, dtype=np.float32)
    for weighted, score in zip(weighted_fundamentals, onset):
        if weighted <= 0.0 or score <= 0.0:
            continue
        hz = float(weighted / max(score, 1e-6))
        if hz < 40.0 or hz > 4000.0:
            continue
        midi = int(round(69.0 + 12.0 * math.log2(hz / 440.0)))
        chroma[midi % 12] += float(score)
    if float(np.sum(chroma)) <= 1e-6:
        return 57, "A", 0.0
    root_pc = int(np.argmax(chroma))
    root_midi = root_pc + 60
    while root_midi > 69:
        root_midi -= 12
    while root_midi < 45:
        root_midi += 12
    confidence = float(chroma[root_pc] / max(1e-6, float(np.sum(chroma))))
    return root_midi, NOTE_NAMES[root_pc], max(0.0, min(1.0, confidence))


def midi_to_hz(midi: int) -> float:
    return 440.0 * (2.0 ** ((midi - 69) / 12.0))


def rgb_to_hsv(rgb: tuple[int, int, int]) -> tuple[float, float, float]:
    return colorsys.rgb_to_hsv(*(channel / 255.0 for channel in rgb))


def hsv_to_rgb255(hue: float, saturation: float, value: float) -> tuple[int, int, int]:
    r, g, b = colorsys.hsv_to_rgb(hue % 1.0, max(0.0, min(1.0, saturation)), max(0.0, min(1.0, value)))
    return tuple(int(round(channel * 255.0)) for channel in (r, g, b))  # type: ignore[return-value]


def visual_contour_samples(
    symbol: int,
    move: MoveTarget,
    move_index: int,
    move_count: int,
    pulse_seconds: float,
    gesture_hz: float,
    emphasized: bool,
) -> list[dict]:
    if pulse_seconds <= 0.0:
        return []
    step = 1.0 / max(1.0, gesture_hz)
    sample_count = max(3, int(math.ceil(pulse_seconds / step)) + 1)
    base_hue, base_sat, base_value = rgb_to_hsv(move.color)
    symbol_phase = ((symbol * 0.071428571) + move_index / max(1, move_count)) % 1.0
    hue_span = 0.07 + 0.015 * (symbol % 5)
    tremolo_cycles = 1 + (symbol % 3)
    tremolo_depth = 0.16 + 0.05 * ((symbol // 3) % 3)
    identity_floor = 0.22 if emphasized else 0.13
    peak = 1.0 if emphasized else 0.58
    samples: list[dict] = []
    for sample_index in range(sample_count):
        offset = min(pulse_seconds, sample_index * step)
        t = offset / pulse_seconds
        attack = min(1.0, t / 0.22)
        release = min(1.0, (1.0 - t) / 0.34)
        envelope = max(0.0, min(1.0, attack, release))
        articulation = 1.0 - tremolo_depth * (0.5 - 0.5 * math.cos(2.0 * math.pi * tremolo_cycles * t + symbol_phase * math.tau))
        value = max(identity_floor, peak * envelope * articulation)
        hue = base_hue + hue_span * math.sin(math.pi * (t - 0.5)) + symbol_phase * 0.035
        saturation = min(1.0, max(0.55, base_sat + 0.10 * math.sin(math.tau * t + symbol_phase * math.tau)))
        rgb = hsv_to_rgb255(hue, saturation, value * base_value)
        samples.append({"offset_seconds": offset, "rgb": rgb, "envelope": envelope, "value": value, "hue": hue % 1.0})
    samples.append({"offset_seconds": pulse_seconds, "rgb": (0, 0, 0), "envelope": 0.0, "value": 0.0, "hue": base_hue})
    return samples


def render_chirp(start_hz: float, end_hz: float, sample_rate: int, chirp_ms: float) -> np.ndarray:
    length = max(16, int(sample_rate * chirp_ms * 0.001))
    t = np.arange(length, dtype=np.float32) / sample_rate
    seconds = max(1e-6, chirp_ms * 0.001)
    k = (end_hz - start_hz) / seconds
    phase = 2.0 * math.pi * (start_hz * t + 0.5 * k * t * t)
    chirp = np.sin(phase).astype(np.float32)
    chirp *= np.hanning(length).astype(np.float32)
    return chirp


def write_wav(path: Path, samples: np.ndarray, sample_rate: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    pcm = np.clip(samples, -1.0, 1.0)
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes((pcm * 32767.0).astype("<i2").tobytes())


def tempo_family(bpm: float, candidates: list[dict[str, float]]) -> dict:
    half = bpm * 0.5
    double = bpm * 2.0

    def nearest(target: float) -> dict[str, float] | None:
        if not candidates:
            return None
        return min(candidates, key=lambda item: abs(item["bpm"] - target))

    return {
        "selected_bpm": bpm,
        "half_time": nearest(half),
        "double_time": nearest(double),
        "strongest": max(candidates, key=lambda item: item["score"], default=None),
    }


def build_plan(args: argparse.Namespace, moves: list[MoveTarget], bpm: float, beat_seconds: float, tempo_confidence: float, tempo_candidates: list[dict[str, float]], root_midi: int, root_name: str, key_confidence: float) -> dict:
    symbols = rotate_to_distinct_opening(build_debruijn(max(2, len(MINOR_PENTATONIC)), 3), 3)
    scale = [midi_to_hz(root_midi + interval) for interval in MINOR_PENTATONIC]
    events = []
    pulse_seconds = args.pulse_ms * 0.001
    for index in range(args.events):
        symbol = symbols[index % len(symbols)]
        note_hz = scale[symbol % len(scale)]
        chirp_target_hz = scale[(symbol + 4) % len(scale)]
        offset = args.lead_seconds + index * beat_seconds
        move_events = []
        for move_index, move in enumerate(moves):
            emphasized = move_index == symbol % max(1, len(moves))
            intensity = 1.0 if emphasized else 0.55
            color = tuple(int(channel * intensity) for channel in move.color)
            contour = (
                visual_contour_samples(symbol, move, move_index, len(moves), pulse_seconds, args.visual_gesture_hz, emphasized)
                if args.visual_gesture == "contour"
                else [
                    {"offset_seconds": 0.0, "rgb": color, "envelope": 1.0, "value": intensity, "hue": rgb_to_hsv(color)[0]},
                    {"offset_seconds": pulse_seconds, "rgb": (0, 0, 0), "envelope": 0.0, "value": 0.0, "hue": rgb_to_hsv(color)[0]},
                ]
            )
            move_events.append({
                "name": move.name,
                "hidraw": move.hidraw,
                "base_rgb": color,
                "visual_word": {
                    "kind": f"mimir.move_visual_{args.visual_gesture}_word.v1",
                    "sample_rate_hz": args.visual_gesture_hz,
                    "duration_seconds": pulse_seconds,
                    "symbol": symbol,
                    "emphasized": emphasized,
                    "samples": contour,
                },
            })
        events.append(
            {
                "index": index,
                "symbol": symbol,
                "offset_seconds": offset,
                "chirp_start_hz": note_hz,
                "chirp_end_hz": chirp_target_hz,
                "move_pulses": move_events,
            }
        )
    return {
        "kind": "mimir.music_keyed_move_chirp_plan.v1",
        "created_unix": time.time(),
        "tempo_bpm": bpm,
        "beat_seconds": beat_seconds,
        "tempo_confidence": tempo_confidence,
        "tempo_grid": args.tempo_grid,
        "tempo_family": tempo_family(bpm, tempo_candidates),
        "root_note": root_name,
        "root_midi": root_midi,
        "key_confidence": key_confidence,
        "suggested_scale": "minor-pentatonic",
        "scale_frequencies_hz": scale,
        "visual_gesture": {
            "mode": args.visual_gesture,
            "sample_rate_hz": args.visual_gesture_hz,
            "pulse_seconds": pulse_seconds,
            "encoding": "identity color bias plus symbol-dependent hue glide, envelope, and tremolo articulation",
        },
        "moves": [move.__dict__ for move in moves],
        "events": events,
    }


def render_audio(plan: dict, args: argparse.Namespace, path: Path) -> None:
    duration = max(event["offset_seconds"] for event in plan["events"]) + 1.0
    audio = np.zeros(max(1, int(duration * args.sample_rate)), dtype=np.float32)
    for event in plan["events"]:
        chirp = render_chirp(event["chirp_start_hz"], event["chirp_end_hz"], args.sample_rate, args.chirp_ms)
        start = int(event["offset_seconds"] * args.sample_rate)
        end = min(len(audio), start + len(chirp))
        audio[start:end] += chirp[: end - start] * args.source_gain
    write_wav(path, audio, args.sample_rate)


def emit(plan: dict, args: argparse.Namespace, moves: list[MoveTarget], wav_path: Path) -> None:
    move_specs = ",".join(f"{move.name}={move.hidraw}" for move in moves)
    remote_b64 = base64.b64encode(REMOTE_MULTI_MOVE.encode("utf-8")).decode("ascii")
    remote_cmd = (
        "tmp=$(mktemp); "
        f"printf '%s' '{remote_b64}' | base64 -d > \"$tmp\"; "
        f"python3 \"$tmp\" '{move_specs}'; "
        "status=$?; rm -f \"$tmp\"; exit $status"
    )
    ssh = subprocess.Popen(["ssh", "-o", "BatchMode=yes", args.ssh_target, remote_cmd], stdin=subprocess.PIPE, text=True)
    assert ssh.stdin is not None
    for event in plan["events"]:
        for pulse in event["move_pulses"]:
            samples = pulse.get("visual_word", {}).get("samples") or [
                {"offset_seconds": 0.0, "rgb": pulse.get("base_rgb", (0, 0, 0))},
                {"offset_seconds": args.pulse_ms * 0.001, "rgb": (0, 0, 0)},
            ]
            for sample in samples:
                r, g, b = sample["rgb"]
                ssh.stdin.write(f"{event['offset_seconds'] + sample['offset_seconds']:.6f} {pulse['name']} {int(r)} {int(g)} {int(b)}\n")
    ssh.stdin.write("go\n")
    ssh.stdin.flush()
    audio = subprocess.Popen([args.ffplay, "-nodisp", "-autoexit", "-loglevel", "quiet", str(wav_path)])
    audio.wait(timeout=max(event["offset_seconds"] for event in plan["events"]) + 5)
    ssh.stdin.close()
    ssh.wait(timeout=10)


def main() -> int:
    args = parse_args()
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    moves = parse_moves(args.move)
    if args.mode == "synthetic":
        _times, onset, weighted_fundamentals = synthetic_analysis(args)
    else:
        _times, onset, weighted_fundamentals = live_analysis(args)
    bpm, beat_seconds, tempo_confidence, autocorr = estimate_tempo(args, onset, args.fps)
    root_midi, root_name, key_confidence = estimate_root(weighted_fundamentals, onset)
    plan = build_plan(args, moves, bpm, beat_seconds, tempo_confidence, autocorr, root_midi, root_name, key_confidence)
    plan["whitened_spectral_autocorr"] = autocorr
    plan_path = out_dir / "music-keyed-move-chirp-plan.json"
    wav_path = out_dir / "music-keyed-chirps.wav"
    plan_path.write_text(json.dumps(plan, indent=2), encoding="utf-8")
    render_audio(plan, args, wav_path)
    print(f"plan={plan_path}")
    print(f"wav={wav_path}")
    print(
        f"tempo={bpm:.2f}bpm beat={beat_seconds:.3f}s conf={tempo_confidence:.3f} "
        f"root={root_name} keyConf={key_confidence:.3f} events={len(plan['events'])}"
    )
    if not args.dry_run:
        emit(plan, args, moves, wav_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
