from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .core import CameraModel, triangulate_dlt
from .render_bridge import RenderPointPacket


@dataclass(frozen=True)
class DenseStereoConfig:
    sample_step: int = 4
    block_radius: int = 3
    max_disparity_px: int = 96
    min_texture: float = 0.020
    min_ncc: float = 0.72
    max_reprojection_error_px: float = 6.0
    radius_m: float = 0.006


def dense_stereo_points(
    *,
    prefix: str,
    left_frame_bgr: np.ndarray,
    right_frame_bgr: np.ndarray,
    left_camera: CameraModel,
    right_camera: CameraModel,
    timestamp_ns: int,
    config: DenseStereoConfig | None = None,
) -> tuple[RenderPointPacket, ...]:
    """Match textured pixels in a calibrated pair and emit RGB surface claims."""

    cfg = config or DenseStereoConfig()
    left = _luma(left_frame_bgr)
    right = _luma(right_frame_bgr)
    height = min(left.shape[0], right.shape[0], left_camera.height, right_camera.height)
    width = min(left.shape[1], right.shape[1], left_camera.width, right_camera.width)
    radius = max(1, int(cfg.block_radius))
    step = max(1, int(cfg.sample_step))
    points: list[RenderPointPacket] = []

    for row in range(radius, height - radius, step):
        for col in range(radius + 1, width - radius, step):
            patch = _patch(left, row, col, radius)
            texture = float(np.std(patch))
            if texture < cfg.min_texture:
                continue
            best_col, score = _best_horizontal_match(
                patch,
                right,
                row,
                col,
                radius,
                max_disparity_px=cfg.max_disparity_px,
            )
            if best_col is None or score < cfg.min_ncc:
                continue
            uv_left = np.array([float(col), float(row)], dtype=np.float64)
            uv_right = np.array([float(best_col), float(row)], dtype=np.float64)
            try:
                xyz = triangulate_dlt(left_camera.projection_matrix, uv_left, right_camera.projection_matrix, uv_right)
            except ValueError:
                continue
            if not np.all(np.isfinite(xyz)):
                continue
            error = 0.5 * (
                left_camera.reprojection_error(xyz, uv_left) + right_camera.reprojection_error(xyz, uv_right)
            )
            if error > cfg.max_reprojection_error_px:
                continue
            b, g, r = (left_frame_bgr[row, col].astype(np.float32) / 255.0).clip(0.0, 1.0)
            confidence = max(0.0, min(1.0, (score - cfg.min_ncc) / max(1e-6, 1.0 - cfg.min_ncc)))
            confidence = max(0.20, min(1.0, 0.35 + confidence * 0.55 + min(0.25, texture)))
            points.append(
                RenderPointPacket(
                    stable_key=f"{prefix}:{row}:{col}:{best_col}",
                    xyz=xyz.astype(np.float64),
                    radius_m=cfg.radius_m,
                    color_rgba=(float(r), float(g), float(b), 0.38 + 0.48 * confidence),
                    confidence=confidence,
                    source_timestamp_ns=timestamp_ns,
                )
            )
    return tuple(points)


def _best_horizontal_match(
    left_patch: np.ndarray,
    right: np.ndarray,
    row: int,
    col: int,
    radius: int,
    *,
    max_disparity_px: int,
) -> tuple[int | None, float]:
    best_col: int | None = None
    best_score = -1.0
    min_col = max(radius, col - max_disparity_px)
    max_col = min(right.shape[1] - radius - 1, col + max_disparity_px // 8)
    for candidate_col in range(min_col, max_col + 1):
        score = _ncc(left_patch, _patch(right, row, candidate_col, radius))
        if score > best_score:
            best_col = candidate_col
            best_score = score
    return best_col, best_score


def _luma(frame_bgr: np.ndarray) -> np.ndarray:
    if frame_bgr.ndim != 3 or frame_bgr.shape[2] != 3:
        raise ValueError("frame_bgr must have shape HxWx3")
    frame = frame_bgr.astype(np.float32) / 255.0
    return 0.0722 * frame[:, :, 0] + 0.7152 * frame[:, :, 1] + 0.2126 * frame[:, :, 2]


def _patch(image: np.ndarray, row: int, col: int, radius: int) -> np.ndarray:
    return image[row - radius : row + radius + 1, col - radius : col + radius + 1]


def _ncc(a: np.ndarray, b: np.ndarray) -> float:
    aa = a.astype(np.float32) - float(np.mean(a))
    bb = b.astype(np.float32) - float(np.mean(b))
    denom = float(np.linalg.norm(aa) * np.linalg.norm(bb))
    if denom < 1e-9:
        return -1.0
    return float(np.sum(aa * bb) / denom)
