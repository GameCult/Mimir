"""CPU reference RGB surface lowering used by diagnostics and tests."""

import numpy as np

from .core import CameraModel
from .dense_stereo import DenseStereoConfig, dense_stereo_points
from .render_bridge import RenderPointPacket


def rgb_dense_stereo_splats(
    primary_bgr: np.ndarray,
    secondary_bgr: np.ndarray,
    timestamp_ns: int,
    *,
    step: int,
) -> tuple[RenderPointPacket, ...]:
    height, width = primary_bgr.shape[:2]
    secondary_height, secondary_width = secondary_bgr.shape[:2]
    width = min(width, secondary_width)
    height = min(height, secondary_height)
    left = rgb_dense_camera("kiyo-primary", -0.18, width, height)
    right = rgb_dense_camera("kiyo-secondary", 0.18, width, height)
    return dense_stereo_points(
        prefix="dense-rgb",
        left_frame_bgr=primary_bgr[:height, :width],
        right_frame_bgr=secondary_bgr[:height, :width],
        left_camera=left,
        right_camera=right,
        timestamp_ns=timestamp_ns,
        config=DenseStereoConfig(sample_step=step, block_radius=3, max_disparity_px=max(24, width // 3), radius_m=0.0045),
    )


def rgb_dense_camera(sensor_id: str, x: float, width: int, height: int) -> CameraModel:
    focal = 0.82 * float(width)
    return CameraModel(
        sensor_id=sensor_id,
        camera_matrix=np.array(
            [
                [focal, 0.0, width * 0.5],
                [0.0, focal, height * 0.5],
                [0.0, 0.0, 1.0],
            ],
            dtype=np.float64,
        ),
        dist_coeffs=np.zeros(5),
        world_from_sensor=np.array(
            [
                [1.0, 0.0, 0.0, x],
                [0.0, 1.0, 0.0, -0.72],
                [0.0, 0.0, 1.0, 1.28],
                [0.0, 0.0, 0.0, 1.0],
            ],
            dtype=np.float64,
        ),
        width=width,
        height=height,
        role="rgb_dense_stereo",
        latency_ms=45.0,
    )


def rgb_body_splats(
    prefix: str,
    frame_bgr: np.ndarray,
    origin: np.ndarray,
    now_s: float,
    timestamp_ns: int,
    *,
    side: float,
    step: int,
) -> list[RenderPointPacket]:
    height, width = frame_bgr.shape[:2]
    y0, y1, x0, x1 = person_crop(frame_bgr, side)
    crop_pixels = frame_bgr[y0:y1, x0:x1].astype(np.float32) / 255.0
    exposure = max(0.18, float(np.percentile(crop_pixels, 92))) if crop_pixels.size else 1.0
    points: list[RenderPointPacket] = []
    for row in range(y0, y1, step):
        for col in range(x0, x1, step):
            pixel = frame_bgr[row, col].astype(np.float32) / 255.0
            pixel = np.clip(np.power(np.clip(pixel / exposure, 0.0, 2.2), 0.72), 0.0, 1.0)
            luminance = float(0.0722 * pixel[0] + 0.7152 * pixel[1] + 0.2126 * pixel[2])
            saturation = float(np.max(pixel) - np.min(pixel))
            if luminance < 0.045 and saturation < 0.08:
                continue
            u = (col - x0) / max(1, x1 - x0 - 1)
            v = (row - y0) / max(1, y1 - y0 - 1)
            body = body_surface_point(u, v, side, now_s)
            thickness = 0.045 * np.sin((u * 11.0) + (v * 7.0) + now_s) + 0.030 * (luminance - 0.35)
            xyz = origin + body + np.array([0.0, thickness, 0.0])
            b, g, r = pixel
            alpha = max(0.34, min(0.92, 0.28 + luminance * 0.72 + saturation * 0.22))
            confidence = max(0.45, min(1.0, 0.55 + luminance * 0.35 + saturation * 0.25))
            radius = 0.014 + 0.020 * max(0.0, 1.0 - abs(v - 0.48) * 1.4)
            points.append(_render_point(f"{prefix}:{row}:{col}", xyz, radius, (float(r), float(g), float(b), alpha), confidence, timestamp_ns))
    return points


def rgb_room_splats(
    prefix: str,
    frame_bgr: np.ndarray,
    camera_origin: np.ndarray,
    timestamp_ns: int,
    *,
    step: int,
) -> list[RenderPointPacket]:
    height, width = frame_bgr.shape[:2]
    points: list[RenderPointPacket] = []
    body_crop = person_crop(frame_bgr, -1.0)
    for row in range(0, height, step):
        for col in range(0, width, step):
            if inside_person_crop(row, col, body_crop):
                continue
            pixel = frame_bgr[row, col].astype(np.float32) / 255.0
            luminance = float(0.0722 * pixel[0] + 0.7152 * pixel[1] + 0.2126 * pixel[2])
            saturation = float(np.max(pixel) - np.min(pixel))
            if luminance < 0.030 and saturation < 0.06:
                continue
            u = col / max(1, width - 1)
            v = row / max(1, height - 1)
            xyz = room_surface_point(u, v, camera_origin)
            b, g, r = np.clip(np.power(pixel, 0.78), 0.0, 1.0)
            alpha = max(0.16, min(0.58, 0.12 + luminance * 0.48 + saturation * 0.16))
            confidence = max(0.30, min(0.82, 0.38 + luminance * 0.32 + saturation * 0.18))
            points.append(_render_point(f"{prefix}:{row}:{col}", xyz, 0.024 + 0.018 * confidence, (float(r), float(g), float(b), alpha), confidence, timestamp_ns))
    return points


def inside_person_crop(row: int, col: int, crop: tuple[int, int, int, int]) -> bool:
    y0, y1, x0, x1 = crop
    pad_y = max(6, (y1 - y0) // 18)
    pad_x = max(6, (x1 - x0) // 18)
    return y0 - pad_y <= row <= y1 + pad_y and x0 - pad_x <= col <= x1 + pad_x


def room_surface_point(u: float, v: float, camera_origin: np.ndarray) -> np.ndarray:
    x = -1.75 + 3.5 * u
    if v > 0.70:
        floor_v = (v - 0.70) / 0.30
        y = -0.95 + 1.85 * (1.0 - floor_v)
        z = 0.018 + 0.05 * (1.0 - floor_v)
    elif v < 0.26:
        wall_v = v / 0.26
        y = 1.12
        z = 1.15 + 0.90 * (1.0 - wall_v)
    else:
        mid_v = (v - 0.26) / 0.44
        y = 1.05 - 0.55 * mid_v
        z = 1.12 - 0.55 * mid_v
    parallax = 0.10 * np.array([camera_origin[0], camera_origin[1], 0.0], dtype=np.float64)
    return np.array([x, y, z], dtype=np.float64) + parallax


def person_crop(frame_bgr: np.ndarray, side: float) -> tuple[int, int, int, int]:
    height, width = frame_bgr.shape[:2]
    gray = np.max(frame_bgr, axis=2)
    threshold = max(18.0, float(np.percentile(gray, 63)))
    mask = gray > threshold
    if mask.any():
        ys, xs = np.nonzero(mask)
        x0 = max(0, int(np.percentile(xs, 8)) - width // 20)
        x1 = min(width, int(np.percentile(xs, 94)) + width // 20)
        y0 = max(0, int(np.percentile(ys, 4)) - height // 24)
        y1 = min(height, int(np.percentile(ys, 96)) + height // 24)
    else:
        x0, x1 = int(width * 0.18), int(width * 0.82)
        y0, y1 = int(height * 0.05), int(height * 0.95)
    if x1 - x0 < width * 0.25:
        center = int(width * (0.42 if side < 0 else 0.58))
        half = int(width * 0.24)
        x0, x1 = max(0, center - half), min(width, center + half)
    return y0, y1, x0, x1


def body_surface_point(u: float, v: float, side: float, now_s: float) -> np.ndarray:
    theta = (u - 0.5) * np.pi * 1.15
    if v < 0.24:
        vv = v / 0.24
        radius_x = 0.18 * np.sin(max(0.03, vv) * np.pi)
        z = 1.42 + vv * 0.34
        x = radius_x * np.sin(theta) + 0.025 * np.sin(now_s)
        y = 0.02 * np.cos(theta)
    elif v < 0.78:
        vv = (v - 0.24) / 0.54
        width = 0.24 * (1.0 - abs(vv - 0.48) * 0.42)
        z = 0.74 + (1.0 - vv) * 0.68
        x = width * np.sin(theta)
        y = 0.055 * np.cos(theta)
    else:
        vv = (v - 0.78) / 0.22
        arm = side * (0.20 + 0.26 * abs(u - 0.5) * 2.0)
        x = arm * vv + 0.06 * np.sin(theta)
        y = 0.045 * np.cos(theta)
        z = 1.02 - 0.34 * vv
    return np.array([x, y, z], dtype=np.float64)


def _render_point(
    key: str,
    xyz: np.ndarray,
    radius_m: float,
    color: tuple[float, float, float, float],
    confidence: float,
    timestamp_ns: int,
) -> RenderPointPacket:
    return RenderPointPacket(
        stable_key=key,
        xyz=np.asarray(xyz, dtype=np.float64),
        radius_m=radius_m,
        color_rgba=color,
        confidence=confidence,
        source_timestamp_ns=timestamp_ns,
    )
