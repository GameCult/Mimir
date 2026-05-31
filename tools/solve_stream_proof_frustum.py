#!/usr/bin/env python3
"""Build the first stream-proof Kiyo frustum/trail receipt from sweep observations.

This is intentionally offline and explicit about its authority. Nightwing views
define a provisional canonical witness space from matched Move flashes; Kiyo
observations are fit into that space so we can render an AR trail receipt. The
result is calibration evidence for Starfire, not final room-scale truth.
"""

from __future__ import annotations

import argparse
import json
import math
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw


COLORS = {
    "move-00-07-04-a6-be-5f": (0, 190, 255),
    "move-00-06-f5-23-e2-d1": (255, 54, 118),
    "move-a": (255, 200, 70),
    "move-b": (120, 255, 145),
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("observations_json")
    parser.add_argument("--target-source", default="kiyo-pro")
    parser.add_argument("--anchor-left", default="nw-builtin-cam")
    parser.add_argument("--anchor-right", default="nw-eye-1")
    parser.add_argument("--out", default=None)
    parser.add_argument("--preview", default=None)
    return parser.parse_args()


def load_observations(path: Path) -> list[dict]:
    data = json.loads(path.read_text(encoding="utf-8"))
    return [obs for sensor in data.get("sensors", []) for obs in sensor.get("observations", [])]


def by_source_event(observations: list[dict]) -> dict[tuple[str, int], dict]:
    return {(str(obs["source_id"]), int(obs["event_index"])): obs for obs in observations}


def witness_anchor(left: dict, right: dict) -> np.ndarray:
    lx = float(left["clip_x"])
    ly = float(left["clip_y"])
    rx = float(right["clip_x"])
    ry = float(right["clip_y"])
    disparity = lx - rx
    vertical = ry - ly
    # Provisional Nightwing witness space: two fast/wide views become a stable
    # local scaffold until the global room solve owns metric scale.
    return np.asarray([
        (lx + rx) * 0.65,
        (ly + ry) * 0.45,
        float(np.clip(1.60 + disparity * 0.70 + vertical * 0.20, 0.65, 2.80)),
    ], dtype=np.float64)


def fit_projection(world: np.ndarray, clip: np.ndarray) -> np.ndarray:
    rows = []
    for point, uv in zip(world, clip):
        x, y, z = point
        u, v = uv
        rows.append([x, y, z, 1.0, 0.0, 0.0, 0.0, 0.0, -u * x, -u * y, -u * z, -u])
        rows.append([0.0, 0.0, 0.0, 0.0, x, y, z, 1.0, -v * x, -v * y, -v * z, -v])
    _, _, vt = np.linalg.svd(np.asarray(rows, dtype=np.float64))
    matrix = vt[-1].reshape(3, 4)
    scale = np.linalg.norm(matrix[2, :3])
    return matrix / scale if scale > 1.0e-9 else matrix


def project(matrix: np.ndarray, world: np.ndarray) -> np.ndarray:
    homogeneous = np.concatenate([world, np.ones((world.shape[0], 1), dtype=np.float64)], axis=1)
    raw = homogeneous @ matrix.T
    denom = np.where(np.abs(raw[:, 2]) < 1.0e-9, 1.0e-9, raw[:, 2])
    return raw[:, :2] / denom[:, None]


def clip_to_pixel(x: float, y: float, width: int, height: int) -> tuple[float, float]:
    return ((x + 1.0) * 0.5 * width, (1.0 - y) * 0.5 * height)


def make_preview(path: Path, target: str, matches: list[dict], projection: np.ndarray, preview: Path) -> None:
    frame_dir = path.parent / f"{target}-frames"
    frames = sorted(frame_dir.glob("frame_*.png"))
    if frames:
        image = Image.open(frames[len(frames) // 2]).convert("RGB")
    else:
        image = Image.new("RGB", (1280, 720), (4, 6, 8))
    draw = ImageDraw.Draw(image, "RGBA")
    width, height = image.size
    groups: dict[str, list[dict]] = defaultdict(list)
    for match in matches:
        groups[str(match.get("controller_id") or "unknown")].append(match)
    for controller, points in groups.items():
        color = COLORS.get(controller, (255, 255, 255))
        observed_pixels = [clip_to_pixel(float(p["target_clip"][0]), float(p["target_clip"][1]), width, height) for p in points]
        projected_pixels = [clip_to_pixel(float(p["projected_clip"][0]), float(p["projected_clip"][1]), width, height) for p in points]
        if len(observed_pixels) >= 2:
            draw.line(observed_pixels, fill=(*color, 210), width=4)
        if len(projected_pixels) >= 2:
            draw.line(projected_pixels, fill=(*color, 90), width=2)
        for observed, projected in zip(observed_pixels, projected_pixels):
            ox, oy = observed
            px, py = projected
            draw.ellipse((ox - 5, oy - 5, ox + 5, oy + 5), fill=(*color, 230))
            draw.line((px - 5, py, px + 5, py), fill=(255, 255, 255, 180), width=1)
            draw.line((px, py - 5, px, py + 5), fill=(255, 255, 255, 180), width=1)
    preview.parent.mkdir(parents=True, exist_ok=True)
    image.save(preview)


def main() -> int:
    args = parse_args()
    path = Path(args.observations_json)
    observations = load_observations(path)
    lookup = by_source_event(observations)
    event_ids = sorted({int(obs["event_index"]) for obs in observations})
    world_points = []
    target_points = []
    matches = []
    for event_id in event_ids:
        left = lookup.get((args.anchor_left, event_id))
        right = lookup.get((args.anchor_right, event_id))
        target = lookup.get((args.target_source, event_id))
        if not left or not right or not target:
            continue
        anchor = witness_anchor(left, right)
        target_clip = np.asarray([float(target["clip_x"]), float(target["clip_y"])], dtype=np.float64)
        world_points.append(anchor)
        target_points.append(target_clip)
        matches.append({
            "event_index": event_id,
            "controller_id": target.get("controller_id"),
            "symbol": target.get("symbol"),
            "world": anchor.tolist(),
            "target_clip": target_clip.tolist(),
            "observed_time_s": target.get("observed_time_s"),
        })
    if len(matches) < 6:
        raise SystemExit(f"Need at least 6 matched events for a projection solve, found {len(matches)}")

    world = np.vstack(world_points)
    target = np.vstack(target_points)
    matrix = fit_projection(world, target)
    projected = project(matrix, world)
    residuals = np.linalg.norm(projected - target, axis=1)
    for match, projected_clip, residual in zip(matches, projected, residuals):
        match["projected_clip"] = projected_clip.tolist()
        match["reprojection_error_clip"] = float(residual)

    mean_error = float(np.mean(residuals))
    p90_error = float(np.percentile(residuals, 90))
    confidence = float(np.clip(1.0 / (1.0 + mean_error * 8.0) * min(1.0, len(matches) / 40.0), 0.0, 1.0))
    document = {
        "kind": "mimir.stream_proof_frustum_solve.v1",
        "truth_status": "provisional_witness_space",
        "source_id": args.target_source,
        "anchor_sources": [args.anchor_left, args.anchor_right],
        "calibration_id": "stream-proof-kiyo-move-trails",
        "used_points": len(matches),
        "mean_reprojection_error_clip": mean_error,
        "p90_reprojection_error_clip": p90_error,
        "confidence": confidence,
        "projection_matrix_row_major": matrix.reshape(-1).tolist(),
        "notes": [
            "Nightwing observations define a provisional local witness space, not metric room truth.",
            "This receipt is sufficient for an AR trail demo and for feeding Starfire's global residual owner.",
        ],
        "trail_samples": matches,
    }
    out = Path(args.out) if args.out else path.with_name(f"{args.target_source}-stream-proof-frustum.json")
    out.write_text(json.dumps(document, indent=2, sort_keys=True), encoding="utf-8")
    preview = Path(args.preview) if args.preview else path.with_name(f"{args.target_source}-stream-proof-trails.png")
    make_preview(path, args.target_source, matches, matrix, preview)
    print(json.dumps({
        "out": str(out),
        "preview": str(preview),
        "used_points": len(matches),
        "mean_reprojection_error_clip": mean_error,
        "p90_reprojection_error_clip": p90_error,
        "confidence": confidence,
    }, indent=2))
    return 0 if confidence > 0.25 else 1


if __name__ == "__main__":
    raise SystemExit(main())
