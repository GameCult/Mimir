from __future__ import annotations

from dataclasses import dataclass
import hashlib
import math
import time

import numpy as np

from localcast.sensor_fusion.render_bridge import RenderFramePacket, RenderPointPacket


@dataclass(frozen=True)
class SpoutOutputConfig:
    sender_name: str = "LocalCastBridge Point Cloud"
    width: int = 1920
    height: int = 1080
    fps: float = 60.0
    point_scale: float = 1.0
    demo_point_count: int = 4096
    camera_preset: str = "kiyo-mid-deru"
    max_render_points: int = 5000


@dataclass(frozen=True)
class VirtualCamera:
    eye: tuple[float, float, float]
    target: tuple[float, float, float]
    up: tuple[float, float, float] = (0.0, 0.0, 1.0)
    fov_degrees: float = 62.0
    near_m: float = 0.05
    far_m: float = 50.0

    @property
    def eye_np(self) -> np.ndarray:
        return np.asarray(self.eye, dtype=np.float64)

    @property
    def target_np(self) -> np.ndarray:
        return np.asarray(self.target, dtype=np.float64)

    @property
    def up_np(self) -> np.ndarray:
        return np.asarray(self.up, dtype=np.float64)


@dataclass(frozen=True)
class ScreenBrushPacket:
    center_px: tuple[int, int]
    radii_px: tuple[int, int]
    rotation_deg: float
    color_rgba: tuple[int, int, int, int]
    depth: float = 0.0


def demo_render_frame(now_s: float, config: SpoutOutputConfig) -> RenderFramePacket:
    count = max(1, int(config.demo_point_count))
    points: list[RenderPointPacket] = []
    for i in range(count):
        u = i / count
        ring = 0.15 + 1.15 * math.sqrt(u)
        theta = i * 2.399963229728653 + now_s * 0.45
        z = 1.2 + 0.55 * math.sin(i * 0.031 + now_s * 0.9)
        confidence = 0.35 + 0.65 * ((math.sin(i * 0.017 + now_s) + 1.0) * 0.5)
        color = (
            0.15 + 0.85 * confidence,
            0.35 + 0.35 * math.sin(theta + 1.7),
            0.70 + 0.25 * math.cos(theta),
            0.85,
        )
        points.append(
            RenderPointPacket(
                stable_key=f"demo-{i}",
                xyz=np.array([ring * math.cos(theta), ring * math.sin(theta), z], dtype=np.float64),
                radius_m=0.018 + 0.035 * confidence,
                color_rgba=color,
                confidence=confidence,
                source_timestamp_ns=time.monotonic_ns(),
            )
        )
    now_ns = time.monotonic_ns()
    return RenderFramePacket(
        schema="localcast.sensor_fusion.render_frame.v1",
        frame_id=int(now_s * config.fps),
        created_monotonic_ns=now_ns,
        source_time_min_ns=now_ns,
        source_time_max_ns=now_ns,
        present_time_ns=now_ns,
        audio_alignment_time_ns=now_ns,
        spout_sender_name=config.sender_name,
        target_width=config.width,
        target_height=config.height,
        points=tuple(points),
    )


def frame_to_vertex_array(frame: RenderFramePacket, point_scale: float = 1.0) -> np.ndarray:
    vertices = np.zeros((len(frame.points), 8), dtype=np.float32)
    for index, point in enumerate(frame.points):
        vertices[index, 0:3] = point.xyz.astype(np.float32)
        rgba = np.asarray(point.color_rgba, dtype=np.float32)
        confidence = max(0.0, min(1.0, float(point.confidence)))
        vertices[index, 3:7] = np.clip(rgba, 0.0, 1.0)
        vertices[index, 6] *= confidence
        vertices[index, 7] = max(0.002, float(point.radius_m) * point_scale)
    return vertices


def frame_with_point_budget(
    frame: RenderFramePacket,
    max_points: int,
    camera_preset_name: str = "kiyo-mid-deru",
) -> RenderFramePacket:
    if max_points <= 0 or len(frame.points) <= max_points:
        return frame
    pinned = sorted(
        (point for point in frame.points if is_pinned_render_point(point)),
        key=lambda point: (-render_priority(point, camera_preset_name), point.stable_key),
    )[:max_points]
    pinned_keys = {point.stable_key for point in pinned}
    remaining_budget = max(0, max_points - len(pinned))
    if remaining_budget <= 0:
        points = pinned
        return _frame_with_points(frame, points)
    priority_count = max(1, int(remaining_budget * 0.55))
    temporal_count = max(0, remaining_budget - priority_count)
    priority = sorted(
        (point for point in frame.points if point.stable_key not in pinned_keys),
        key=lambda point: (-render_priority(point, camera_preset_name), stable_hash(point.stable_key, 0)),
    )[:priority_count]
    selected_keys = pinned_keys | {point.stable_key for point in priority}
    remainder = [point for point in frame.points if point.stable_key not in selected_keys]
    temporal = sorted(
        remainder,
        key=lambda point: stable_hash(point.stable_key, frame.frame_id) / max(0.05, render_priority(point, camera_preset_name)),
    )[:temporal_count]
    points = pinned + priority + temporal
    points = sorted(points, key=lambda point: point.stable_key)
    return _frame_with_points(frame, points)


def _frame_with_points(frame: RenderFramePacket, points: list[RenderPointPacket]) -> RenderFramePacket:
    return RenderFramePacket(
        schema=frame.schema,
        frame_id=frame.frame_id,
        created_monotonic_ns=frame.created_monotonic_ns,
        source_time_min_ns=frame.source_time_min_ns,
        source_time_max_ns=frame.source_time_max_ns,
        present_time_ns=frame.present_time_ns,
        audio_alignment_time_ns=frame.audio_alignment_time_ns,
        spout_sender_name=frame.spout_sender_name,
        target_width=frame.target_width,
        target_height=frame.target_height,
        points=tuple(points),
    )


def is_pinned_render_point(point: RenderPointPacket) -> bool:
    return point.stable_key.startswith(
        (
            "camera-chirp:",
            "camera-pose-correction:",
            "camera-sync:",
            "clap-calibration:",
            "clap-ray:",
            "clap-timing:",
            "audio-event-",
        )
    )


def point_prefix_counts(points: tuple[RenderPointPacket, ...] | list[RenderPointPacket]) -> dict[str, int]:
    counts: dict[str, int] = {}
    for point in points:
        prefix = point_prefix(point.stable_key)
        counts[prefix] = counts.get(prefix, 0) + 1
    return dict(sorted(counts.items()))


def point_prefix(stable_key: str) -> str:
    if ":" in stable_key:
        return stable_key.split(":", 1)[0]
    if stable_key.startswith("audio-event-"):
        return "audio-event"
    return stable_key


def stable_hash(key: str, salt: int) -> int:
    digest = hashlib.blake2s(f"{salt}:{key}".encode("utf-8"), digest_size=8).digest()
    return int.from_bytes(digest, "little")


def render_priority(point: RenderPointPacket, camera_preset_name: str) -> float:
    key = point.stable_key
    confidence = max(0.0, min(1.0, float(point.confidence)))
    semantic = 1.0
    if key.startswith("clap-calibration:"):
        semantic = 3.2
    elif key.startswith(("camera-sync:", "camera-chirp:", "camera-pose-correction:")):
        semantic = 2.6
    elif key.startswith(("clap-ray:", "clap-timing:")):
        semantic = 2.2
    elif key.startswith("dense-rgb:"):
        semantic = 2.0
    elif key.startswith("leap-motion:"):
        semantic = 1.45
    elif key.startswith("room-rgb:"):
        semantic = 1.15
    elif key.startswith("stochastic:"):
        semantic = 0.36
    elif key.startswith("audio-event-"):
        semantic = 0.24
    elif key.startswith(("sensor:", "frustum:")):
        semantic = 0.12
    focus = render_focus_weight(point, camera_preset_name)
    return max(0.001, confidence * semantic * focus)


def render_focus_weight(point: RenderPointPacket, camera_preset_name: str) -> float:
    if camera_preset_name != "kiyo-mid-deru":
        return 1.0
    xyz = np.asarray(point.xyz, dtype=np.float64)
    deru = np.array([0.48, 0.28, 1.24], dtype=np.float64)
    distance = float(np.linalg.norm((xyz - deru) * np.array([1.0, 1.0, 0.75], dtype=np.float64)))
    return 0.22 + 1.78 / (1.0 + distance * 1.85)


def lower_frame_to_screen_brushes(
    frame: RenderFramePacket,
    width: int,
    height: int,
    point_scale: float = 1.0,
    camera: VirtualCamera | None = None,
) -> tuple[ScreenBrushPacket, ...]:
    brushes: list[ScreenBrushPacket] = []
    view_proj = camera_matrix(0.0, width / max(1, height), camera)
    for point in frame.points:
        projected = project_world_to_screen(point.xyz, view_proj, width, height)
        if projected is None:
            continue
        px, py, depth, clip_w = projected
        radius_scale = semantic_radius_scale(point.stable_key)
        radius = max(2, int(round(float(point.radius_m) * point_scale * height * 1.45 * radius_scale / max(0.42, clip_w))))
        if px < -radius * 3 or px >= width + radius * 3 or py < -radius * 3 or py >= height + radius * 3:
            continue
        stable = hashlib.blake2s(point.stable_key.encode("utf-8"), digest_size=4).digest()
        angle = int.from_bytes(stable[:2], "little") / 65535.0 * 180.0
        semantic_stretch = 2.8 if point.stable_key.startswith(("frustum:", "ray:", "floor:", "wall:")) else 1.0
        stretch = semantic_stretch * (1.0 + (stable[2] / 255.0) * 1.45)
        radii = (max(2, int(round(radius * stretch))), radius)
        rgba = np.clip(np.asarray(point.color_rgba, dtype=np.float32), 0.0, 1.0)
        rgba[3] *= max(0.0, min(1.0, float(point.confidence)))
        rgba[3] *= semantic_alpha_scale(point.stable_key)
        color = tuple(int(v) for v in (rgba * 255.0)[:4])
        brushes.append(ScreenBrushPacket((px, py), radii, angle, color, depth))
    return tuple(sorted(brushes, key=lambda brush: brush.depth, reverse=True))


def semantic_radius_scale(stable_key: str) -> float:
    if stable_key.startswith("audio-event-"):
        return 0.42
    if stable_key.startswith("stochastic:"):
        return 0.50
    return 1.0


def semantic_alpha_scale(stable_key: str) -> float:
    if stable_key.startswith("audio-event-"):
        return 0.36
    if stable_key.startswith("stochastic:"):
        return 0.46
    return 1.0


def rasterize_frame_rgba(
    frame: RenderFramePacket,
    width: int,
    height: int,
    point_scale: float = 1.0,
    camera: VirtualCamera | None = None,
) -> np.ndarray:
    image = np.zeros((height, width, 4), dtype=np.uint8)
    image[:, :, 0] = 7
    image[:, :, 1] = 10
    image[:, :, 2] = 15
    image[:, :, 3] = 255
    draw_reconstruction_guides(image, camera)
    draw_floor_shadows(image, frame, point_scale, camera)
    canvas = image.astype(np.float32) / 255.0
    for brush in lower_frame_to_screen_brushes(frame, width, height, point_scale, camera):
        composite_gaussian_brush(canvas, brush)
    return np.clip(canvas * 255.0, 0.0, 255.0).astype(np.uint8)


def composite_gaussian_brush(canvas: np.ndarray, brush: ScreenBrushPacket) -> None:
    height, width = canvas.shape[:2]
    cx, cy = brush.center_px
    rx, ry = max(1, brush.radii_px[0]), max(1, brush.radii_px[1])
    bound = int(math.ceil(max(rx, ry) * 1.35))
    x0 = max(0, cx - bound)
    x1 = min(width, cx + bound + 1)
    y0 = max(0, cy - bound)
    y1 = min(height, cy + bound + 1)
    if x0 >= x1 or y0 >= y1:
        return

    yy, xx = np.mgrid[y0:y1, x0:x1].astype(np.float32)
    dx = xx - float(cx)
    dy = yy - float(cy)
    angle = math.radians(float(brush.rotation_deg))
    ca = math.cos(angle)
    sa = math.sin(angle)
    local_x = dx * ca + dy * sa
    local_y = -dx * sa + dy * ca
    q = (local_x / float(rx)) ** 2 + (local_y / float(ry)) ** 2
    mask = q < 1.0
    if not np.any(mask):
        return

    # Compact Gaussian envelope: smooth center, finite edge, no solid paint bucket.
    falloff = 4.4
    edge = math.exp(-falloff)
    gaussian = np.exp(-falloff * q)
    envelope = np.clip((gaussian - edge) / max(1.0 - edge, 1e-6), 0.0, 1.0) ** 1.35
    envelope *= mask

    src = np.asarray(brush.color_rgba, dtype=np.float32) / 255.0
    alpha = np.clip(envelope * src[3], 0.0, 0.96)
    if float(alpha.max()) <= 0.0:
        return
    target = canvas[y0:y1, x0:x1, :]
    target[:, :, :3] = src[:3] * alpha[:, :, None] + target[:, :, :3] * (1.0 - alpha[:, :, None])
    target[:, :, 3] = np.maximum(target[:, :, 3], alpha)


def project_world_to_screen(
    xyz: np.ndarray,
    view_proj: np.ndarray,
    width: int,
    height: int,
) -> tuple[int, int, float, float] | None:
    point = np.ones(4, dtype=np.float64)
    point[:3] = np.asarray(xyz, dtype=np.float64)
    clip = view_proj @ point
    if clip[3] <= 0.001:
        return None
    ndc = clip[:3] / clip[3]
    if ndc[0] < -1.35 or ndc[0] > 1.35 or ndc[1] < -1.35 or ndc[1] > 1.35:
        return None
    px = int(round((ndc[0] * 0.5 + 0.5) * width))
    py = int(round((0.5 - ndc[1] * 0.5) * height))
    return px, py, float(ndc[2]), float(clip[3])


def draw_reconstruction_guides(image: np.ndarray, camera: VirtualCamera | None = None) -> None:
    try:
        import cv2
    except ModuleNotFoundError:
        return

    height, width = image.shape[:2]
    view_proj = camera_matrix(0.0, width / max(1, height), camera)
    grid_color = (36, 50, 62, 255)
    wall_color = (24, 36, 50, 255)
    for x in np.linspace(-1.8, 1.8, 13):
        draw_world_line(image, view_proj, np.array([x, -1.0, 0.0]), np.array([x, 1.15, 0.0]), grid_color, 1)
    for y in np.linspace(-1.0, 1.15, 9):
        draw_world_line(image, view_proj, np.array([-1.8, y, 0.0]), np.array([1.8, y, 0.0]), grid_color, 1)
    for x in np.linspace(-1.8, 1.8, 9):
        draw_world_line(image, view_proj, np.array([x, 1.15, 0.0]), np.array([x, 1.15, 2.2]), wall_color, 1)
    for z in np.linspace(0.35, 2.1, 6):
        draw_world_line(image, view_proj, np.array([-1.8, 1.15, z]), np.array([1.8, 1.15, z]), wall_color, 1)

def draw_floor_shadows(
    image: np.ndarray,
    frame: RenderFramePacket,
    point_scale: float,
    camera: VirtualCamera | None = None,
) -> None:
    try:
        import cv2
    except ModuleNotFoundError:
        return

    height, width = image.shape[:2]
    view_proj = camera_matrix(0.0, width / max(1, height), camera)
    for point in frame.points:
        xyz = np.asarray(point.xyz, dtype=np.float64)
        if xyz[2] <= 0.10 or point.stable_key.startswith(("sensor:", "frustum:")):
            continue
        shadow = xyz.copy()
        shadow[2] = 0.012
        projected = project_world_to_screen(shadow, view_proj, width, height)
        if projected is None:
            continue
        px, py, _, clip_w = projected
        radius = max(2, int(round(float(point.radius_m) * point_scale * height * 0.9 / max(0.42, clip_w))))
        shade = int(18 + 18 * max(0.0, min(1.0, float(point.confidence))))
        cv2.ellipse(image, (px, py), (radius * 2, max(1, radius // 2)), 0.0, 0.0, 360.0, (shade // 3, shade // 2, shade, 255), -1, cv2.LINE_AA)


def draw_world_line(
    image: np.ndarray,
    view_proj: np.ndarray,
    start: np.ndarray,
    end: np.ndarray,
    color: tuple[int, int, int, int],
    thickness: int,
) -> None:
    try:
        import cv2
    except ModuleNotFoundError:
        return

    height, width = image.shape[:2]
    a = project_world_to_screen(start, view_proj, width, height)
    b = project_world_to_screen(end, view_proj, width, height)
    if a is None or b is None:
        return
    cv2.line(image, a[:2], b[:2], color, thickness=thickness, lineType=cv2.LINE_AA)



def camera_preset(name: str, now_s: float = 0.0) -> VirtualCamera:
    if name == "orbit":
        return VirtualCamera(
            eye=(2.4 * math.sin(now_s * 0.08), -3.4, 1.85 + 0.2 * math.cos(now_s * 0.11)),
            target=(0.0, 0.0, 1.15),
            fov_degrees=58.0,
        )
    if name == "kiyo-mid-deru":
        return VirtualCamera(
            eye=(0.05, -1.035, 1.64),
            target=(0.48, 0.28, 1.24),
            fov_degrees=64.0,
        )
    raise ValueError(f"unknown camera preset: {name}")


def camera_matrix(now_s: float, aspect: float, camera: VirtualCamera | None = None) -> np.ndarray:
    view = camera_preset("orbit", now_s) if camera is None else camera
    return perspective(math.radians(view.fov_degrees), aspect, view.near_m, view.far_m) @ look_at(
        view.eye_np,
        view.target_np,
        view.up_np,
    )


def look_at(eye: np.ndarray, target: np.ndarray, up: np.ndarray) -> np.ndarray:
    forward = target - eye
    forward = forward / np.linalg.norm(forward)
    side = np.cross(forward, up)
    side = side / np.linalg.norm(side)
    true_up = np.cross(side, forward)
    matrix = np.eye(4, dtype=np.float64)
    matrix[0, :3] = side
    matrix[1, :3] = true_up
    matrix[2, :3] = -forward
    translate = np.eye(4, dtype=np.float64)
    translate[:3, 3] = -eye
    return matrix @ translate


def perspective(fov_y: float, aspect: float, near: float, far: float) -> np.ndarray:
    f = 1.0 / math.tan(fov_y * 0.5)
    matrix = np.zeros((4, 4), dtype=np.float64)
    matrix[0, 0] = f / aspect
    matrix[1, 1] = f
    matrix[2, 2] = (far + near) / (near - far)
    matrix[2, 3] = (2.0 * far * near) / (near - far)
    matrix[3, 2] = -1.0
    return matrix


