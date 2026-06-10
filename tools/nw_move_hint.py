#!/usr/bin/env python3
"""Nightwing-local PS Eye / PS Move localization hint extractor.

Stdlib-only on purpose. Nightwing owns Eye frames and Move observations; the
wire format back to Starfire is compact witness state, not raw media.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import nw_eye_cap


def percentile(values: list[float], q: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, int(round((len(ordered) - 1) * q))))
    return float(ordered[index])


def best_blob(frame: bytes, width: int, height: int) -> dict | None:
    sampled = [frame[index] for index in range(0, len(frame), max(1, len(frame) // 4000))]
    threshold = max(40, int(percentile([float(v) for v in sampled], 0.995)))
    seen = bytearray(width * height)
    best: dict | None = None
    for y in range(height):
        row = y * width
        for x in range(width):
            start = row + x
            if seen[start] or frame[start] < threshold:
                continue
            stack = [start]
            seen[start] = 1
            count = 0
            sx = 0.0
            sy = 0.0
            sw = 0.0
            total = 0.0
            peak = 0
            while stack:
                index = stack.pop()
                yy, xx = divmod(index, width)
                value = int(frame[index])
                weight = max(1.0, float(value - threshold + 1))
                count += 1
                sx += xx * weight
                sy += yy * weight
                sw += weight
                total += value
                peak = max(peak, value)
                for nx, ny in ((xx - 1, yy), (xx + 1, yy), (xx, yy - 1), (xx, yy + 1)):
                    if 0 <= nx < width and 0 <= ny < height:
                        ni = ny * width + nx
                        if not seen[ni] and frame[ni] >= threshold:
                            seen[ni] = 1
                            stack.append(ni)
            if count < 3 or sw <= 0.0:
                continue
            cx = sx / sw
            cy = sy / sw
            mean_luma = total / count
            confidence = min(1.0, (mean_luma / 255.0) * min(1.0, count / 80.0) * 1.6)
            blob = {
                "x_px": cx,
                "y_px": cy,
                "clip_x": (cx / max(1, width - 1)) * 2.0 - 1.0,
                "clip_y": 1.0 - (cy / max(1, height - 1)) * 2.0,
                "area_px": count,
                "radius_px": math.sqrt(count / math.pi),
                "mean_luma": mean_luma,
                "peak_luma": peak,
                "confidence": confidence,
            }
            if best is None or (blob["confidence"], blob["area_px"], blob["peak_luma"]) > (best["confidence"], best["area_px"], best["peak_luma"]):
                best = blob
    return best


def analyze(raw_path: Path, stats: dict, events: list[dict], moves: list[str], pulse_seconds: float, source_id: str) -> dict:
    width = int(stats["width"])
    height = int(stats["height"])
    frame_bytes = width * height
    raw = raw_path.read_bytes()
    count = min(len(raw) // frame_bytes, len(stats.get("timestamps_s", [])))
    timestamps = [float(value) for value in stats.get("timestamps_s", [])[:count]]
    observations = []
    for event in events:
        event_index = int(event["index"])
        expected = float(event["offset"])
        move_id = moves[event_index % len(moves)] if moves else "move"
        best: dict | None = None
        for frame_index, timestamp in enumerate(timestamps):
            if timestamp < expected - 0.18 or timestamp > expected + max(0.25, pulse_seconds + 0.18):
                continue
            frame = raw[frame_index * frame_bytes:(frame_index + 1) * frame_bytes]
            blob = best_blob(frame, width, height)
            if blob is None:
                continue
            blob.update({
                "event_index": event_index,
                "symbol": int(event["symbol"]),
                "controller_id": move_id,
                "source_id": source_id,
                "observed_time_s": timestamp,
                "schedule_offset_s": expected,
                "schedule_residual_ms": (timestamp - expected) * 1000.0,
            })
            if best is None or blob["confidence"] > best["confidence"]:
                best = blob
        if best is not None:
            observations.append(best)
    return {
        "kind": "mimir.move_controller_observation_state.v1",
        "source_id": source_id,
        "width": width,
        "height": height,
        "frame_count": count,
        "first_device_timestamp_s": stats.get("first_device_timestamp_s"),
        "duration_s": stats.get("duration_s"),
        "sequence_gap": stats.get("sequence_gap"),
        "observations": observations,
        "observation_count": len(observations),
        "stable_event_count": len([item for item in observations if float(item["confidence"]) >= 0.25]),
        "mean_confidence": sum(float(item["confidence"]) for item in observations) / len(observations) if observations else 0.0,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", required=True)
    parser.add_argument("--source-id", required=True)
    parser.add_argument("--width", type=int, default=320)
    parser.add_argument("--height", type=int, default=240)
    parser.add_argument("--fps", type=int, default=187)
    parser.add_argument("--seconds", type=float, default=8.0)
    parser.add_argument("--events-json", required=True)
    parser.add_argument("--moves", default="move-a,move-b")
    parser.add_argument("--pulse-ms", type=float, default=95.0)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    out = Path(args.out)
    raw_stats_path = out.with_suffix(".capture.json")
    cap_args = argparse.Namespace(
        device=args.device,
        width=args.width,
        height=args.height,
        format="YUYV",
        fps=args.fps,
        seconds=args.seconds,
        buffers=8,
        out=str(raw_stats_path),
    )
    stats = nw_eye_cap.capture(cap_args)
    events = json.loads(Path(args.events_json).read_text(encoding="utf-8"))
    moves = [part for part in args.moves.split(",") if part]
    result = analyze(Path(stats["frames_path"]), stats, events, moves, args.pulse_ms * 0.001, args.source_id)
    out.write_text(json.dumps(result, indent=2, sort_keys=True), encoding="utf-8")
    Path(stats["frames_path"]).unlink(missing_ok=True)
    print(json.dumps(result, sort_keys=True))
    return 0 if result["observation_count"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
