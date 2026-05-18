import argparse
from pathlib import Path
import sys
import time
from typing import BinaryIO

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.sensor_fusion import (
    CameraModel,
    FusionConfig,
    Observation2D,
    RenderBridgeConfig,
    RenderFramePacket,
    RenderPointPacket,
    SensorRig,
    put_live_render_frame,
    lower_points_to_render_frame,
)


def acquire_runtime_lock(path: Path) -> BinaryIO | None:
    import msvcrt

    path.parent.mkdir(parents=True, exist_ok=True)
    handle = path.open("a+b")
    handle.seek(0)
    handle.write(b"\0")
    handle.flush()
    handle.seek(0)
    try:
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        handle.close()
        return None
    return handle


def camera(sensor_id: str, x: float) -> CameraModel:
    return CameraModel(
        sensor_id=sensor_id,
        camera_matrix=np.array([[300.0, 0.0, 160.0], [0.0, 300.0, 120.0], [0.0, 0.0, 1.0]]),
        dist_coeffs=np.zeros(5),
        world_from_sensor=np.array(
            [
                [1.0, 0.0, 0.0, x],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ]
        ),
        width=320,
        height=240,
    )


def synthetic_observations(rig: SensorRig, now_s: float, count: int) -> list[Observation2D]:
    left = rig.cameras["ps3eye_left"]
    right = rig.cameras["ps3eye_right"]
    timestamp = time.monotonic_ns()
    observations: list[Observation2D] = []
    targets = reconstruction_targets(now_s, count)
    for index, point in enumerate(targets):
        marker = f"synthetic-fused-{index}"
        observations.append(Observation2D("ps3eye_left", marker, timestamp, left.project_world(point), 0.95))
        observations.append(Observation2D("ps3eye_right", marker, timestamp, right.project_world(point), 0.92))
    return observations


def reconstruction_targets(now_s: float, count: int) -> list[np.ndarray]:
    """Deadline stand-in for real detections: two people plus tracked room support."""
    body_count = max(32, int(count * 0.55))
    room_count = max(24, int(count * 0.25))
    marker_count = max(8, count - body_count - room_count)
    targets: list[np.ndarray] = []
    targets.extend(body_points("host", np.array([-0.42, 0.05, 0.0]), now_s, body_count // 2))
    targets.extend(body_points("deru", np.array([0.48, 0.28, 0.0]), now_s + 0.7, body_count - body_count // 2))
    targets.extend(room_support_points(now_s, room_count))
    targets.extend(marker_points(now_s, marker_count))
    return targets[: max(1, count)]


def body_points(name: str, origin: np.ndarray, now_s: float, count: int) -> list[np.ndarray]:
    points: list[np.ndarray] = []
    golden = 2.399963229728653
    for index in range(max(1, count)):
        u = (index + 0.5) / max(1, count)
        theta = index * golden
        if u < 0.22:
            radius = 0.17 * np.sqrt(u / 0.22)
            z = 1.55 + 0.16 * np.sin(theta)
            local = np.array([radius * np.cos(theta), radius * np.sin(theta) * 0.72, z])
        elif u < 0.72:
            v = (u - 0.22) / 0.50
            radius = 0.23 * (1.0 - abs(v - 0.52) * 0.55)
            z = 0.72 + v * 0.72
            sway = 0.035 * np.sin(now_s * 1.4 + (0.0 if name == "host" else 1.2))
            local = np.array([radius * np.cos(theta) + sway, radius * np.sin(theta) * 0.52, z])
        else:
            v = (u - 0.72) / 0.28
            side = -1.0 if index % 2 == 0 else 1.0
            reach = 0.28 + 0.18 * np.sin(now_s * 1.7 + side)
            z = 1.16 - 0.25 * v
            local = np.array([side * reach * v, 0.06 * np.sin(theta), z])
        points.append(origin + local)
    return points


def room_support_points(now_s: float, count: int) -> list[np.ndarray]:
    points: list[np.ndarray] = []
    for index in range(max(1, count)):
        u = (index + 0.5) / max(1, count)
        if index % 3 == 0:
            x = -1.65 + 3.3 * u
            y = -0.85 + 1.75 * ((index * 7) % max(1, count)) / max(1, count)
            z = 0.02
        elif index % 3 == 1:
            x = -1.65 + 3.3 * u
            y = 1.05
            z = 0.28 + 1.72 * ((index * 5) % max(1, count)) / max(1, count)
        else:
            x = -1.05 + 2.1 * ((index * 11) % max(1, count)) / max(1, count)
            y = -0.65 + 0.12 * np.sin(now_s + index)
            z = 0.85 + 0.95 * u
        points.append(np.array([x, y, z], dtype=np.float64))
    return points


def marker_points(now_s: float, count: int) -> list[np.ndarray]:
    points: list[np.ndarray] = []
    for index in range(max(1, count)):
        theta = now_s * 0.9 + index * 2.399963229728653
        ring = 0.28 + 0.52 * ((index % 5) / 4.0)
        points.append(
            np.array(
                [
                    ring * np.cos(theta),
                    0.12 + ring * 0.42 * np.sin(theta * 0.7),
                    1.08 + 0.42 * np.sin(theta + index * 0.37),
                ],
                dtype=np.float64,
            )
        )
    return points


def reconstruction_context_points(now_s: float, timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
    points: list[RenderPointPacket] = []
    cameras = [
        ("ps3eye-left", (-0.78, -0.82, 1.34), (0.10, 0.72, 1.05), (0.2, 0.7, 1.0, 0.95)),
        ("ps3eye-right", (0.78, -0.82, 1.34), (-0.10, 0.72, 1.05), (0.2, 0.7, 1.0, 0.95)),
        ("kiyo", (-0.28, -1.05, 1.62), (-0.15, 0.45, 1.15), (1.0, 0.62, 0.25, 0.95)),
        ("kiyo-pro", (0.38, -1.02, 1.66), (0.20, 0.48, 1.18), (1.0, 0.62, 0.25, 0.95)),
        ("leap", (0.0, -0.18, 0.76), (0.0, 0.20, 1.05), (0.55, 1.0, 0.72, 0.95)),
    ]
    for sensor, origin, target, color in cameras:
        origin_np = np.asarray(origin, dtype=np.float64)
        target_np = np.asarray(target, dtype=np.float64)
        points.append(render_point(f"sensor:{sensor}", origin_np, 0.055, color, 1.0, timestamp_ns))
        for ray_index, t in enumerate((0.28, 0.48, 0.68, 0.88)):
            p = origin_np + (target_np - origin_np) * t
            jitter = np.array([0.04 * np.sin(now_s + ray_index), 0.0, 0.035 * np.cos(now_s * 0.7 + ray_index)])
            points.append(render_point(f"frustum:{sensor}:{ray_index}", p + jitter, 0.014, color, 0.72, timestamp_ns))
    return tuple(points)


def render_point(
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


def dim_fusion_points(points: tuple[RenderPointPacket, ...]) -> tuple[RenderPointPacket, ...]:
    muted: list[RenderPointPacket] = []
    for point in points:
        confidence = max(0.0, min(1.0, float(point.confidence)))
        muted.append(
            RenderPointPacket(
                stable_key=point.stable_key,
                xyz=point.xyz,
                radius_m=point.radius_m * 0.72,
                color_rgba=(0.46, 0.58, 0.72, 0.18 * confidence),
                confidence=confidence * 0.72,
                source_timestamp_ns=point.source_timestamp_ns,
            )
        )
    return tuple(muted)


class RgbSplatSampler:
    def __init__(
        self,
        *,
        api: str,
        primary_index: int,
        secondary_index: int,
        width: int,
        height: int,
        sample_step: int,
        room_step: int,
        fallback_dir: Path,
    ) -> None:
        self.api = api
        self.primary_index = primary_index
        self.secondary_index = secondary_index
        self.width = width
        self.height = height
        self.sample_step = max(4, int(sample_step))
        self.room_step = max(self.sample_step, int(room_step))
        self.fallback_dir = fallback_dir
        self._captures: dict[int, object] = {}
        self._frames: dict[int, np.ndarray] = {}

    def close(self) -> None:
        for capture in self._captures.values():
            try:
                capture.release()
            except Exception:
                pass
        self._captures.clear()

    def splats(self, now_s: float, timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
        primary = self._read_frame(self.primary_index)
        secondary = self._read_frame(self.secondary_index)
        points: list[RenderPointPacket] = []
        if primary is not None:
            points.extend(rgb_room_splats("room-rgb:primary", primary, np.array([-0.42, 0.05, 0.0]), timestamp_ns, step=self.room_step))
            points.extend(rgb_body_splats("host-rgb", primary, np.array([-0.42, 0.05, 0.0]), now_s, timestamp_ns, side=-1.0, step=self.sample_step))
        if secondary is not None:
            points.extend(rgb_room_splats("room-rgb:secondary", secondary, np.array([0.48, 0.28, 0.0]), timestamp_ns, step=self.room_step))
            points.extend(rgb_body_splats("deru-rgb", secondary, np.array([0.48, 0.28, 0.0]), now_s + 0.6, timestamp_ns, side=1.0, step=self.sample_step))
        return tuple(points)

    def _read_frame(self, index: int) -> np.ndarray | None:
        import cv2

        capture = self._captures.get(index)
        if capture is None:
            capture = cv2.VideoCapture(index, cv2_api(self.api))
            if capture.isOpened():
                capture.set(cv2.CAP_PROP_FRAME_WIDTH, self.width)
                capture.set(cv2.CAP_PROP_FRAME_HEIGHT, self.height)
                capture.set(cv2.CAP_PROP_BUFFERSIZE, 1)
                self._captures[index] = capture
            else:
                try:
                    capture.release()
                except Exception:
                    pass
                return self._fallback_frame(index)
        ok = False
        frame = None
        for _ in range(2):
            ok, frame = capture.read()
            if ok and frame is not None:
                self._frames[index] = frame
                return frame
        cached = self._frames.get(index)
        if cached is not None:
            return cached
        return self._fallback_frame(index)

    def _fallback_frame(self, index: int) -> np.ndarray | None:
        import cv2

        candidates = [
            self.fallback_dir / f"rgb-probe-{self.api}-{index}.png",
            self.fallback_dir / f"rgb-probe-dshow-{index}.png",
            self.fallback_dir / f"rgb-probe-any-{index}.png",
            self.fallback_dir / f"rgb-probe-msmf-{index}.png",
        ]
        for path in candidates:
            if path.exists():
                frame = cv2.imread(str(path))
                if frame is not None:
                    return frame
        return None


def cv2_api(name: str) -> int:
    import cv2

    return {
        "any": 0,
        "dshow": cv2.CAP_DSHOW,
        "msmf": cv2.CAP_MSMF,
    }[name]


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
    crop = person_crop(frame_bgr, side)
    y0, y1, x0, x1 = crop
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
            points.append(
                render_point(
                    f"{prefix}:{row}:{col}",
                    xyz,
                    radius,
                    (float(r), float(g), float(b), alpha),
                    confidence,
                    timestamp_ns,
                )
            )
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
            points.append(
                render_point(
                    f"{prefix}:{row}:{col}",
                    xyz,
                    0.024 + 0.018 * confidence,
                    (float(r), float(g), float(b), alpha),
                    confidence,
                    timestamp_ns,
                )
            )
    return points


def inside_person_crop(row: int, col: int, crop: tuple[int, int, int, int]) -> bool:
    y0, y1, x0, x1 = crop
    pad_y = max(6, (y1 - y0) // 18)
    pad_x = max(6, (x1 - x0) // 18)
    return y0 - pad_y <= row <= y1 + pad_y and x0 - pad_x <= col <= x1 + pad_x


def room_surface_point(u: float, v: float, camera_origin: np.ndarray) -> np.ndarray:
    # Coarse calibrated-room proxy until real depth/SLAM owns this surface.
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


class LeapPackedMotionSampler:
    def __init__(
        self,
        *,
        api: str,
        index: int,
        width: int,
        height: int,
        fps: float,
        step: int,
        fallback_dir: Path,
    ) -> None:
        self.api = api
        self.index = index
        self.width = width
        self.height = height
        self.fps = fps
        self.step = max(4, int(step))
        self.fallback_dir = fallback_dir
        self._capture = None
        self._previous_channels: dict[str, np.ndarray] = {}
        self._last_timestamp_ns: int | None = None

    @property
    def last_timestamp_ns(self) -> int | None:
        return self._last_timestamp_ns

    def close(self) -> None:
        if self._capture is not None:
            try:
                self._capture.release()
            except Exception:
                pass
            self._capture = None

    def motion_splats(self, timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
        frame = self._read_frame()
        if frame is None:
            return ()
        self._last_timestamp_ns = time.monotonic_ns()
        channels = unpack_leap_packed_channels(frame)
        points: list[RenderPointPacket] = []
        for channel_name, channel in channels.items():
            previous = self._previous_channels.get(channel_name)
            self._previous_channels[channel_name] = channel
            if previous is None or previous.shape != channel.shape:
                continue
            points.extend(leap_channel_motion_points(channel_name, channel, previous, self._last_timestamp_ns or timestamp_ns, step=self.step))
        return tuple(points)

    def _read_frame(self) -> np.ndarray | None:
        import cv2

        if self._capture is None:
            capture = cv2.VideoCapture(self.index, cv2_api(self.api))
            if capture.isOpened():
                capture.set(cv2.CAP_PROP_FRAME_WIDTH, self.width)
                capture.set(cv2.CAP_PROP_FRAME_HEIGHT, self.height)
                capture.set(cv2.CAP_PROP_FPS, self.fps)
                capture.set(cv2.CAP_PROP_BUFFERSIZE, 1)
                self._capture = capture
            else:
                try:
                    capture.release()
                except Exception:
                    pass
                return self._fallback_frame()
        ok, frame = self._capture.read()
        if ok and frame is not None:
            return frame
        return self._fallback_frame()

    def _fallback_frame(self) -> np.ndarray | None:
        import cv2

        for path in (
            self.fallback_dir / f"rgb-probe-{self.api}-{self.index}.png",
            self.fallback_dir / f"rgb-probe-dshow-{self.index}.png",
            self.fallback_dir / f"rgb-probe-msmf-{self.index}.png",
            self.fallback_dir / "leap-probe.png",
        ):
            if path.exists():
                frame = cv2.imread(str(path))
                if frame is not None:
                    return frame
        return None


def unpack_leap_packed_channels(frame_bgr: np.ndarray) -> dict[str, np.ndarray]:
    frame = frame_bgr.astype(np.float32) / 255.0
    blue = frame[:, :, 0]
    green = frame[:, :, 1]
    red = frame[:, :, 2]
    magenta = np.maximum(red, blue)
    return {
        "green": green,
        "magenta": magenta,
        "red": red,
        "blue": blue,
    }


def leap_channel_motion_points(
    channel_name: str,
    current: np.ndarray,
    previous: np.ndarray,
    timestamp_ns: int,
    *,
    step: int,
) -> list[RenderPointPacket]:
    height, width = current.shape[:2]
    motion = np.abs(current - previous)
    threshold = max(0.035, float(np.percentile(motion, 94)))
    points: list[RenderPointPacket] = []
    for row in range(0, height, step):
        for col in range(0, width, step):
            value = float(motion[row, col])
            intensity = float(current[row, col])
            if value < threshold and intensity < 0.10:
                continue
            u = col / max(1, width - 1)
            v = row / max(1, height - 1)
            x = -0.46 + 0.92 * u
            y = -0.16 + 0.68 * (1.0 - v)
            z = 0.74 + 0.58 * (1.0 - v) + 0.12 * intensity
            confidence = max(0.28, min(1.0, value * 3.8 + intensity * 0.42))
            if channel_name == "green":
                color = (0.28, 1.0, 0.62, 0.86)
            elif channel_name == "magenta":
                color = (1.0, 0.26, 0.92, 0.80)
            elif channel_name == "red":
                color = (1.0, 0.22, 0.18, 0.42)
            else:
                color = (0.20, 0.42, 1.0, 0.42)
            points.append(
                render_point(
                    f"leap-motion:{channel_name}:{row}:{col}",
                    np.array([x, y, z], dtype=np.float64),
                    0.012 + 0.026 * confidence,
                    color,
                    confidence,
                    timestamp_ns,
                )
            )
    return points


def main() -> None:
    parser = argparse.ArgumentParser(description="Write live sensor-fusion render frames into typed CultCache state.")
    parser.add_argument("--cache", default=str(ROOT / "calibration" / "runs" / "visual-state.msgpack"))
    parser.add_argument("--fps", type=float, default=30.0)
    parser.add_argument("--points", type=int, default=256)
    parser.add_argument("--duration", type=float)
    parser.add_argument("--no-rgb", action="store_true")
    parser.add_argument("--rgb-api", choices=["any", "dshow", "msmf"], default="dshow")
    parser.add_argument("--rgb-primary-index", type=int, default=1)
    parser.add_argument("--rgb-secondary-index", type=int, default=3)
    parser.add_argument("--rgb-width", type=int, default=640)
    parser.add_argument("--rgb-height", type=int, default=480)
    parser.add_argument("--rgb-sample-step", type=int, default=10)
    parser.add_argument("--rgb-room-step", type=int, default=28)
    parser.add_argument("--no-leap", action="store_true")
    parser.add_argument("--leap-api", choices=["any", "dshow", "msmf"], default="msmf")
    parser.add_argument("--leap-index", type=int, default=0)
    parser.add_argument("--leap-width", type=int, default=320)
    parser.add_argument("--leap-height", type=int, default=240)
    parser.add_argument("--leap-fps", type=float, default=120.0)
    parser.add_argument("--leap-step", type=int, default=12)
    args = parser.parse_args()

    left = camera("ps3eye_left", -0.25)
    right = camera("ps3eye_right", 0.25)
    rig = SensorRig(
        cameras={left.sensor_id: left, right.sensor_id: right},
        config=FusionConfig(max_pair_dt_ns=30_000_000, max_reprojection_error_px=0.01, cache_ttl_ns=500_000_000),
    )
    render_config = RenderBridgeConfig(default_point_radius_m=0.035)
    interval = 1.0 / max(1.0, args.fps)
    start = time.monotonic()
    frame_id = 0
    cache_path = Path(args.cache)
    lock = acquire_runtime_lock(cache_path.with_suffix(".producer.lock"))
    if lock is None:
        return
    rgb_sampler = None if args.no_rgb else RgbSplatSampler(
        api=args.rgb_api,
        primary_index=args.rgb_primary_index,
        secondary_index=args.rgb_secondary_index,
        width=args.rgb_width,
        height=args.rgb_height,
        sample_step=args.rgb_sample_step,
        room_step=args.rgb_room_step,
        fallback_dir=ROOT / "calibration" / "runs",
    )
    leap_sampler = None if args.no_leap else LeapPackedMotionSampler(
        api=args.leap_api,
        index=args.leap_index,
        width=args.leap_width,
        height=args.leap_height,
        fps=args.leap_fps,
        step=args.leap_step,
        fallback_dir=ROOT / "calibration" / "runs",
    )
    try:
        while True:
            now = time.monotonic()
            if args.duration is not None and now - start >= args.duration:
                break
            observations = synthetic_observations(rig, now, args.points)
            result = rig.fuse(observations)
            frame = lower_points_to_render_frame(
                result.points,
                render_config,
                frame_id=frame_id,
                created_monotonic_ns=time.monotonic_ns(),
            )
            timestamp_ns = frame.source_time_max_ns
            rgb_points = () if rgb_sampler is None else rgb_sampler.splats(now, timestamp_ns)
            leap_points = () if leap_sampler is None else leap_sampler.motion_splats(timestamp_ns)
            if leap_sampler is not None and leap_sampler.last_timestamp_ns is not None:
                timestamp_ns = max(timestamp_ns, leap_sampler.last_timestamp_ns)
            support_points = dim_fusion_points(tuple(frame.points)) if not rgb_points else ()
            frame = RenderFramePacket(
                schema=frame.schema,
                frame_id=frame.frame_id,
                created_monotonic_ns=frame.created_monotonic_ns,
                source_time_min_ns=frame.source_time_min_ns,
                source_time_max_ns=timestamp_ns,
                present_time_ns=timestamp_ns + render_config.visual_delay_ns,
                audio_alignment_time_ns=timestamp_ns + render_config.audio_alignment_delay_ns,
                spout_sender_name=frame.spout_sender_name,
                target_width=frame.target_width,
                target_height=frame.target_height,
                points=leap_points + rgb_points + support_points,
            )
            put_live_render_frame(cache_path, frame)
            frame_id += 1
            sleep_for = interval - (time.monotonic() - now)
            if sleep_for > 0:
                time.sleep(sleep_for)
    finally:
        if rgb_sampler is not None:
            rgb_sampler.close()
        if leap_sampler is not None:
            leap_sampler.close()


if __name__ == "__main__":
    main()
