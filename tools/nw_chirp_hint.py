#!/usr/bin/env python3
"""Nightwing-local builtin mic chirp schedule fitter.

Stdlib-only. Nightwing analyzes its own builtin mic and sends schedule-fit
state back to Starfire; it does not stream raw audio as authority.
"""

from __future__ import annotations

import argparse
import json
import wave
from pathlib import Path

import nightwing_alsa_capture_probe as alsa_probe


def mono_wav(path: Path) -> tuple[int, list[float]]:
    with wave.open(str(path), "rb") as wav:
        rate = wav.getframerate()
        channels = wav.getnchannels()
        pcm = wav.readframes(wav.getnframes())
    values = memoryview(pcm).cast("h")
    frames = len(values) // max(1, channels)
    samples: list[float] = []
    for frame in range(frames):
        total = 0
        for channel in range(channels):
            total += int(values[frame * channels + channel])
        samples.append((total / channels) / 32768.0)
    return rate, samples


def percentile(values: list[float], q: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, int(round((len(ordered) - 1) * q))))
    return ordered[index]


def detect(samples: list[float], sample_rate: int, events: list[dict], _chirp_ms: float, interval: float) -> list[float]:
    window = max(64, int(sample_rate * 0.006))
    hop = max(32, int(sample_rate * 0.003))
    envelope: list[float] = []
    times: list[float] = []
    previous = 0.0
    for start in range(0, max(0, len(samples) - window), hop):
        block = samples[start:start + window]
        energy = sum(value * value for value in block) / len(block)
        rise = max(0.0, energy - previous)
        previous = previous * 0.92 + energy * 0.08
        envelope.append(rise)
        times.append((start + window * 0.5) / sample_rate)
    if not envelope:
        return []
    threshold = max(percentile(envelope, 0.965), max(envelope) * 0.32)
    min_gap_s = max(0.18, interval * 0.45)
    peaks: list[float] = []
    work = envelope[:]
    for _ in events:
        index = max(range(len(work)), key=lambda item: work[item])
        if work[index] <= threshold:
            break
        peak_time = times[index]
        peaks.append(peak_time)
        for item, value_time in enumerate(times):
            if abs(value_time - peak_time) <= min_gap_s:
                work[item] = 0.0
    return sorted(peaks)


def median(values: list[float]) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    mid = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[mid]
    return (ordered[mid - 1] + ordered[mid]) * 0.5


def fit(peaks: list[float], events: list[dict], interval: float) -> tuple[float | None, list[dict]]:
    if not peaks:
        return None, []
    offsets = [float(event["offset"]) for event in events]
    tolerance = max(0.08, interval * 0.18)
    best_shift = None
    best_key = None
    for candidate in sorted(peak - offset for peak in peaks for offset in offsets):
        residuals = [min(abs((offset + candidate) - peak) for peak in peaks) for offset in offsets]
        inliers = [residual for residual in residuals if residual <= tolerance]
        if not inliers:
            continue
        key = (len(inliers), -median(inliers), -abs(candidate))
        if best_key is None or key > best_key:
            best_key = key
            best_shift = candidate
    if best_shift is None:
        return None, []
    used: set[int] = set()
    matches = []
    for event in events:
        expected = float(event["offset"]) + best_shift
        candidates = [index for index in range(len(peaks)) if index not in used]
        nearest = min(candidates, key=lambda index: abs(peaks[index] - expected), default=None)
        if nearest is None or abs(peaks[nearest] - expected) > tolerance:
            matches.append({"index": int(event["index"]), "symbol": int(event["symbol"]), "observed_s": None, "residual_ms": None})
            continue
        used.add(nearest)
        matches.append({"index": int(event["index"]), "symbol": int(event["symbol"]), "observed_s": peaks[nearest], "residual_ms": (peaks[nearest] - expected) * 1000.0})
    return best_shift, matches


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", default="hw:0,0")
    parser.add_argument("--rate", type=int, default=48000)
    parser.add_argument("--channels", type=int, default=2)
    parser.add_argument("--seconds", type=float, default=8.0)
    parser.add_argument("--events-json", required=True)
    parser.add_argument("--chirp-ms", type=float, default=70.0)
    parser.add_argument("--interval", type=float, default=0.72)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    wav_path = Path(args.out).with_suffix(".wav")
    stats_path = Path(args.out).with_suffix(".capture.json")
    pcm, stats = alsa_probe.capture(args.device, args.rate, args.channels, args.seconds, 50_000)
    alsa_probe.write_wav(wav_path, pcm, args.rate, args.channels)
    stats_path.write_text(json.dumps(stats, indent=2, sort_keys=True), encoding="utf-8")
    rate, samples = mono_wav(wav_path)
    events = json.loads(Path(args.events_json).read_text(encoding="utf-8"))
    peaks = detect(samples, rate, events, args.chirp_ms, args.interval)
    shift, matches = fit(peaks, events, args.interval)
    residuals = [abs(float(match["residual_ms"])) for match in matches if match.get("residual_ms") is not None]
    result = {
        "kind": "mimir.audio_chirp_witness_state.v1",
        "source_id": "nightwing-builtin-mic",
        "device": args.device,
        "capture": stats,
        "peaks_s": peaks,
        "schedule_shift_ms": shift * 1000.0 if shift is not None else None,
        "matched": len(residuals),
        "median_abs_residual_ms": median(residuals) if residuals else None,
        "matches": matches,
    }
    Path(args.out).write_text(json.dumps(result, indent=2, sort_keys=True), encoding="utf-8")
    wav_path.unlink(missing_ok=True)
    print(json.dumps(result, sort_keys=True))
    return 0 if result["matched"] >= 2 else 1


if __name__ == "__main__":
    raise SystemExit(main())
