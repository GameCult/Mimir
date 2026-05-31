#!/usr/bin/env python3
"""Crunch a sync_sweep run into camera marker observations.

This is offline evidence extraction. It does not own calibration; it turns a
shared articulated gesture plan plus captured sensor receipts into a compact
observation table that the Starfire frustum/global residual path can ingest.
"""

from __future__ import annotations

import argparse
import csv
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image


DEFAULT_FFPROBE = (
    Path.home()
    / "AppData/Local/Microsoft/WinGet/Packages/Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe"
    / "ffmpeg-8.1.1-full_build/bin/ffprobe.exe"
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sweep_dir")
    parser.add_argument("--ffprobe", default=str(DEFAULT_FFPROBE))
    parser.add_argument("--output-json", default=None)
    parser.add_argument("--output-csv", default=None)
    parser.add_argument("--shift-min", type=float, default=-5.0)
    parser.add_argument("--shift-max", type=float, default=1.0)
    parser.add_argument("--shift-step", type=float, default=0.010)
    return parser.parse_args()


def frame_times(ffprobe: str, video_path: Path, count: int, fallback_fps: float) -> list[float]:
    try:
        probe = subprocess.run(
            [
                ffprobe,
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
        if len(times) >= count:
            return times[:count]
    except Exception:
        pass
    return [index / max(1.0, fallback_fps) for index in range(count)]


def analyze_frame(path: Path) -> dict:
    arr = np.asarray(Image.open(path).convert("RGB"), dtype=np.float32)
    height, width = arr.shape[:2]
    luma = arr[:, :, 0] * 0.2126 + arr[:, :, 1] * 0.7152 + arr[:, :, 2] * 0.0722
    cutoff = max(float(np.percentile(luma, 99.80)), float(np.max(luma)) * 0.70, 12.0)
    mask = luma >= cutoff
    if not np.any(mask):
        mask = luma >= float(np.percentile(luma, 99.95))
    ys, xs = np.nonzero(mask)
    weights = luma[mask]
    total = max(1e-6, float(np.sum(weights)))
    x = float(np.sum(xs * weights) / total)
    y = float(np.sum(ys * weights) / total)
    rgb = np.mean(arr[mask], axis=0) if np.any(mask) else np.zeros(3, dtype=np.float32)
    return {
        "x_px": x,
        "y_px": y,
        "clip_x": (x / max(1.0, width - 1)) * 2.0 - 1.0,
        "clip_y": 1.0 - (y / max(1.0, height - 1)) * 2.0,
        "area_px": int(mask.sum()),
        "peak_luma": float(np.max(luma)),
        "mean_luma": float(np.mean(weights)) if weights.size else 0.0,
        "mean_rgb": [float(v) for v in rgb],
        "width": width,
        "height": height,
    }


def estimate_shift(events: list[dict], times: list[float], scores: np.ndarray, args: argparse.Namespace) -> tuple[float, float]:
    signal = np.maximum(scores - float(np.percentile(scores, 30)), 0.0)
    best_shift = 0.0
    best_score = -1.0
    shifts = np.arange(args.shift_min, args.shift_max + args.shift_step * 0.5, args.shift_step)
    for shift in shifts:
        score = 0.0
        hits = 0
        for event in events:
            target = float(event["offset"]) + float(shift)
            duration = event_duration(event)
            indices = [i for i, t in enumerate(times) if target <= t <= target + duration]
            if not indices:
                continue
            score += float(np.max(signal[indices]))
            hits += 1
        if hits:
            score /= hits
        if score > best_score:
            best_score = score
            best_shift = float(shift)
    return best_shift, best_score


def event_duration(event: dict) -> float:
    durations = []
    for pulse in event.get("move_pulses", []):
        word = pulse.get("visual_word", {})
        if "duration_seconds" in word:
            durations.append(float(word["duration_seconds"]))
        elif word.get("samples"):
            durations.append(max(float(sample.get("offset_seconds", 0.0)) for sample in word["samples"]))
    return max(0.050, max(durations, default=0.090))


def crunch_video(sweep_dir: Path, name: str, events: list[dict], args: argparse.Namespace) -> dict:
    frames_dir = sweep_dir / f"{name}-frames"
    frame_paths = sorted(frames_dir.glob("frame_*.png"))
    if not frame_paths:
        return {"source_id": name, "kind": "local-video", "ok": False, "error": "no extracted frames", "observations": []}
    times = frame_times(args.ffprobe, sweep_dir / f"{name}.mkv", len(frame_paths), 30.0)
    frame_info = [analyze_frame(path) for path in frame_paths]
    scores = np.asarray([info["peak_luma"] * 0.7 + info["mean_luma"] * 0.3 for info in frame_info], dtype=np.float32)
    shift, shift_score = estimate_shift(events, times, scores, args)
    threshold = max(float(np.percentile(scores, 82)), float(np.max(scores)) * 0.35)
    observations = []
    for event in events:
        target = float(event["offset"]) + shift
        duration = event_duration(event)
        candidates = [i for i, t in enumerate(times) if target <= t <= target + duration]
        if not candidates:
            continue
        index = max(candidates, key=lambda i: scores[i])
        info = frame_info[index]
        if scores[index] < threshold:
            continue
        observations.append({
            "source_id": name,
            "event_index": int(event["index"]),
            "symbol": int(event["symbol"]),
            "observed_time_s": float(times[index]),
            "schedule_offset_s": float(event["offset"]),
            "schedule_shift_s": shift,
            "schedule_residual_ms": (float(times[index]) - (float(event["offset"]) + shift)) * 1000.0,
            "controller_id": expected_controller(event),
            **info,
        })
    return {
        "source_id": name,
        "kind": "local-video",
        "ok": len(observations) > 0,
        "frame_count": len(frame_paths),
        "estimated_schedule_shift_s": shift,
        "shift_score": shift_score,
        "threshold": threshold,
        "observations": observations,
    }


def expected_controller(event: dict) -> str | None:
    pulses = event.get("move_pulses", [])
    emphasized = [pulse.get("name") for pulse in pulses if pulse.get("visual_word", {}).get("emphasized")]
    return str(emphasized[0]) if emphasized else (str(pulses[0].get("name")) if pulses else None)


def load_nightwing(sweep_dir: Path, name: str) -> dict | None:
    path = sweep_dir / f"{name}.json"
    if not path.exists():
        return None
    data = json.loads(path.read_text(encoding="utf-8"))
    observations = data.get("observations", [])
    return {
        "source_id": name,
        "kind": "nightwing-video",
        "ok": bool(observations),
        "frame_count": data.get("frame_count"),
        "estimated_schedule_shift_s": None,
        "observations": observations,
    }


def main() -> int:
    args = parse_args()
    sweep_dir = Path(args.sweep_dir)
    summary = json.loads((sweep_dir / "summary.json").read_text(encoding="utf-8"))
    events = summary["events"]
    sensors = []
    for name in ("nw-builtin-cam", "nw-eye-0", "nw-eye-1"):
        loaded = load_nightwing(sweep_dir, name)
        if loaded:
            sensors.append(loaded)
    for name in ("kiyo-pro", "kiyo"):
        sensors.append(crunch_video(sweep_dir, name, events, args))
    observations = [obs for sensor in sensors for obs in sensor.get("observations", [])]
    pair_overlaps = compute_pair_overlaps(observations)
    event_coverage = compute_event_coverage(observations)
    document = {
        "kind": "mimir.articulated_sync_observation_crunch.v1",
        "sweep_dir": str(sweep_dir),
        "event_count": len(events),
        "sensor_count": len(sensors),
        "observation_count": len(observations),
        "pair_overlaps": pair_overlaps,
        "event_coverage": event_coverage,
        "sensors": sensors,
    }
    output_json = Path(args.output_json) if args.output_json else sweep_dir / "calibration-observations.json"
    output_csv = Path(args.output_csv) if args.output_csv else sweep_dir / "calibration-observations.csv"
    output_json.write_text(json.dumps(document, indent=2, sort_keys=True), encoding="utf-8")
    with output_csv.open("w", newline="", encoding="utf-8") as handle:
        fields = ["source_id", "event_index", "symbol", "controller_id", "observed_time_s", "schedule_offset_s", "schedule_shift_s", "schedule_residual_ms", "clip_x", "clip_y", "x_px", "y_px", "area_px", "peak_luma", "mean_luma"]
        writer = csv.DictWriter(handle, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(observations)
    print(json.dumps({
        "output_json": str(output_json),
        "output_csv": str(output_csv),
        "observation_count": len(observations),
        "pair_overlaps": pair_overlaps,
    }, indent=2))
    return 0 if observations else 1


def compute_pair_overlaps(observations: list[dict]) -> list[dict]:
    by_event: dict[int, dict[str, dict]] = {}
    for obs in observations:
        event_index = int(obs["event_index"])
        by_event.setdefault(event_index, {})[str(obs["source_id"])] = obs
    pairs: dict[tuple[str, str], int] = {}
    for sensors in by_event.values():
        names = sorted(sensors)
        for left_index, left in enumerate(names):
            for right in names[left_index + 1:]:
                pairs[(left, right)] = pairs.get((left, right), 0) + 1
    return [
        {"left": left, "right": right, "shared_events": count}
        for (left, right), count in sorted(pairs.items(), key=lambda item: (-item[1], item[0]))
    ]


def compute_event_coverage(observations: list[dict]) -> list[dict]:
    by_event: dict[int, set[str]] = {}
    for obs in observations:
        by_event.setdefault(int(obs["event_index"]), set()).add(str(obs["source_id"]))
    return [
        {"event_index": event_index, "sensor_count": len(sources), "sources": sorted(sources)}
        for event_index, sources in sorted(by_event.items())
    ]


if __name__ == "__main__":
    raise SystemExit(main())
