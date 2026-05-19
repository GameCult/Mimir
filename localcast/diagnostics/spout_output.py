from __future__ import annotations

from dataclasses import dataclass
import ctypes
import hashlib
import json
import math
from pathlib import Path
import time

import numpy as np

from localcast.sensor_fusion.audio_overlay import AudioVisualSyncStatus, overlay_audio_events, with_remote_video_status
from localcast.sensor_fusion.media_artifacts import remote_video_artifact_for_present_time
from localcast.sensor_fusion.render_bridge import RenderFramePacket, RenderPointPacket
from localcast.diagnostics.render_frame_json import load_render_frame_json
from localcast.diagnostics.visual_cache import CultStreamStatus, get_live_render_frame, put_stream_status


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


class RenderFrameFileSource:
    def __init__(self, path: Path, demo_if_missing: bool = False, demo_point_count: int = 4096) -> None:
        self.path = path
        self.demo_if_missing = demo_if_missing
        self.demo_point_count = demo_point_count
        self._mtime_ns: int | None = None
        self._frame: RenderFramePacket | None = None

    def current_frame(self, now_s: float, config: SpoutOutputConfig) -> RenderFramePacket:
        if self.path.exists():
            mtime_ns = self.path.stat().st_mtime_ns
            if self._frame is None or mtime_ns != self._mtime_ns:
                self._frame = load_render_frame_json(self.path)
                self._mtime_ns = mtime_ns
            return self._frame
        if not self.demo_if_missing:
            raise FileNotFoundError(self.path)
        return demo_render_frame(now_s, config)


class CultCacheRenderFrameSource:
    def __init__(
        self,
        path: Path,
        demo_if_missing: bool = False,
        demo_point_count: int = 4096,
        audio_cache: Path | None = None,
        audio_events_cache: Path | None = None,
        audio_event_window_ns: int = 120_000_000,
        remote_video_name: str | None = None,
        remote_video_url: str | None = None,
        remote_video_latency_ns: int = 120_000_000,
        remote_video_tolerance_ns: int = 120_000_000,
    ) -> None:
        self.path = path
        self.demo_if_missing = demo_if_missing
        self.demo_point_count = demo_point_count
        self.audio_cache = audio_cache
        self.audio_events_cache = audio_events_cache
        self.audio_event_window_ns = int(audio_event_window_ns)
        self.remote_video_name = remote_video_name
        self.remote_video_url = remote_video_url
        self.remote_video_latency_ns = int(remote_video_latency_ns)
        self.remote_video_tolerance_ns = int(remote_video_tolerance_ns)
        self._mtime_ns: int | None = None
        self._frame: RenderFramePacket | None = None
        self._audio_mtime_ns: int | None = None
        self._audio_frame = None
        self._events_mtime_ns: int | None = None
        self._events = None
        self.last_sync_status: AudioVisualSyncStatus | None = None

    def current_frame(self, now_s: float, config: SpoutOutputConfig) -> RenderFramePacket:
        if self.path.exists():
            mtime_ns = self.path.stat().st_mtime_ns
            if self._frame is None or mtime_ns != self._mtime_ns:
                self._frame = get_live_render_frame(self.path)
                self._mtime_ns = mtime_ns
            if self._frame is not None:
                return self._with_audio_overlay(self._frame)
        if not self.demo_if_missing:
            raise FileNotFoundError(self.path)
        return self._with_audio_overlay(demo_render_frame(now_s, config))

    def _with_audio_overlay(self, frame: RenderFramePacket) -> RenderFramePacket:
        if self.audio_cache is None and self.audio_events_cache is None:
            self.last_sync_status = None
            return frame
        audio_frame = self._read_audio_frame()
        events = self._read_audio_events()
        augmented, status = overlay_audio_events(
            frame,
            events,
            audio_frame,
            window_ns=self.audio_event_window_ns,
        )
        remote_video = None
        if self.remote_video_name and self.remote_video_url:
            remote_video = remote_video_artifact_for_present_time(
                source_name=self.remote_video_name,
                url=self.remote_video_url,
                present_time_ns=frame.present_time_ns,
                observed_time_ns=frame.source_time_max_ns,
                expected_latency_ns=self.remote_video_latency_ns,
                tolerance_ns=self.remote_video_tolerance_ns,
            )
        status = with_remote_video_status(status, remote_video)
        self.last_sync_status = status
        return augmented

    def _read_audio_frame(self):
        if self.audio_cache is None or not self.audio_cache.exists():
            return None
        mtime_ns = self.audio_cache.stat().st_mtime_ns
        if self._audio_frame is None or mtime_ns != self._audio_mtime_ns:
            from audio_field.cultcache_audio import get_live_spatial_audio_frame

            self._audio_frame = get_live_spatial_audio_frame(self.audio_cache)
            self._audio_mtime_ns = mtime_ns
        return self._audio_frame

    def _read_audio_events(self):
        if self.audio_events_cache is None or not self.audio_events_cache.exists():
            return None
        mtime_ns = self.audio_events_cache.stat().st_mtime_ns
        if self._events is None or mtime_ns != self._events_mtime_ns:
            from audio_field.cultcache_audio import get_live_audio_source_events

            self._events = get_live_audio_source_events(self.audio_events_cache)
            self._events_mtime_ns = mtime_ns
        return self._events


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
    import cv2

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
    import cv2

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
    import cv2

    height, width = image.shape[:2]
    a = project_world_to_screen(start, view_proj, width, height)
    b = project_world_to_screen(end, view_proj, width, height)
    if a is None or b is None:
        return
    cv2.line(image, a[:2], b[:2], color, thickness=thickness, lineType=cv2.LINE_AA)


def write_status(
    path: Path,
    *,
    sender_name: str,
    frames_sent: int,
    point_count: int,
    frame_path: Path | None,
    last_error: str | None = None,
    point_prefix_counts: dict[str, int] | None = None,
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "sender_name": sender_name,
        "frames_sent": frames_sent,
        "point_count": point_count,
        "frame_path": None if frame_path is None else str(frame_path),
        "updated_monotonic_ns": time.monotonic_ns(),
        "last_error": last_error,
    }
    if point_prefix_counts is not None:
        payload["point_prefix_counts"] = dict(sorted(point_prefix_counts.items()))
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def write_cult_status(
    path: Path,
    *,
    sender_name: str,
    frames_sent: int,
    point_count: int,
    frame_source: str,
    last_error: str | None = None,
) -> None:
    put_stream_status(
        path,
        CultStreamStatus(
            sender_name=sender_name,
            frames_sent=frames_sent,
            point_count=point_count,
            frame_source=frame_source,
            updated_monotonic_ns=time.monotonic_ns(),
            last_error="" if last_error is None else last_error,
        ),
    )


def write_sync_status(path: Path, status: AudioVisualSyncStatus | None) -> None:
    if status is None:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(
            {
                "visual_frame_id": status.visual_frame_id,
                "visual_present_time_ns": status.visual_present_time_ns,
                "audio_frame_id": status.audio_frame_id,
                "audio_time_ns": status.audio_time_ns,
                "audio_delta_ns": status.audio_delta_ns,
                "source_event_count": status.source_event_count,
                "overlay_event_count": status.overlay_event_count,
                "remote_video": None
                if status.remote_video is None
                else {
                    "source_name": status.remote_video.source_name,
                    "url": status.remote_video.url,
                    "expected_latency_ns": status.remote_video.expected_latency_ns,
                    "present_time_ns": status.remote_video.present_time_ns,
                    "delta_ns": status.remote_video.delta_ns,
                    "synchronized": status.remote_video.synchronized,
                },
                "synchronized": status.synchronized,
                "updated_monotonic_ns": time.monotonic_ns(),
            },
            indent=2,
        ),
        encoding="utf-8",
    )


class OpenGLSpoutPointRenderer:
    def __init__(self, config: SpoutOutputConfig) -> None:
        self.config = config
        self.sender = None
        self.texture = None
        self.fbo = None
        self.program = None
        self.vbo = None
        self.vao = None
        self.frames_sent = 0
        self.last_render_point_count = 0
        self.last_render_prefix_counts: dict[str, int] = {}

    def __enter__(self) -> "OpenGLSpoutPointRenderer":
        self.open()
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()

    def open(self) -> None:
        import SpoutGL
        from OpenGL.GL import (
            GL_ARRAY_BUFFER,
            GL_BLEND,
            GL_COLOR_ATTACHMENT0,
            GL_FRAMEBUFFER,
            GL_RGBA,
            GL_RGBA8,
            GL_TEXTURE_2D,
            GL_UNSIGNED_BYTE,
            glBindBuffer,
            glBindFramebuffer,
            glBindTexture,
            glBlendFunc,
            glBufferData,
            glEnable,
            glEnableVertexAttribArray,
            glFramebufferTexture2D,
            glGenBuffers,
            glGenFramebuffers,
            glGenTextures,
            glGenVertexArrays,
            glBindVertexArray,
            glTexImage2D,
            glVertexAttribPointer,
            GL_DYNAMIC_DRAW,
            GL_FLOAT,
            GL_ONE,
            GL_ONE_MINUS_SRC_ALPHA,
            GL_PROGRAM_POINT_SIZE,
        )

        from OpenGL.GL.shaders import compileProgram, compileShader
        from OpenGL.GL import GL_FRAGMENT_SHADER, GL_VERTEX_SHADER

        self.sender = SpoutGL.SpoutSender()
        self.sender.setSenderName(self.config.sender_name)
        if not self.sender.createOpenGL():
            raise RuntimeError("SpoutGL could not create an OpenGL context")

        self.texture = glGenTextures(1)
        glBindTexture(GL_TEXTURE_2D, self.texture)
        glTexImage2D(
            GL_TEXTURE_2D,
            0,
            GL_RGBA8,
            self.config.width,
            self.config.height,
            0,
            GL_RGBA,
            GL_UNSIGNED_BYTE,
            None,
        )
        self.fbo = glGenFramebuffers(1)
        glBindFramebuffer(GL_FRAMEBUFFER, self.fbo)
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, self.texture, 0)

        self.program = compileProgram(
            compileShader(VERTEX_SHADER, GL_VERTEX_SHADER),
            compileShader(FRAGMENT_SHADER, GL_FRAGMENT_SHADER),
        )
        self.vao = glGenVertexArrays(1)
        self.vbo = glGenBuffers(1)
        glBindVertexArray(self.vao)
        glBindBuffer(GL_ARRAY_BUFFER, self.vbo)
        glBufferData(GL_ARRAY_BUFFER, 0, None, GL_DYNAMIC_DRAW)
        stride = 8 * 4
        glEnableVertexAttribArray(0)
        glVertexAttribPointer(0, 3, GL_FLOAT, False, stride, None)
        glEnableVertexAttribArray(1)
        glVertexAttribPointer(1, 4, GL_FLOAT, False, stride, ctypes.c_void_p(12))
        glEnableVertexAttribArray(2)
        glVertexAttribPointer(2, 1, GL_FLOAT, False, stride, ctypes.c_void_p(28))
        glEnable(GL_BLEND)
        glEnable(GL_PROGRAM_POINT_SIZE)
        glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)

    def render(self, frame: RenderFramePacket) -> bool:
        from OpenGL.GL import (
            GL_ARRAY_BUFFER,
            GL_COLOR_BUFFER_BIT,
            GL_DEPTH_BUFFER_BIT,
            GL_DYNAMIC_DRAW,
            GL_FRAMEBUFFER,
            GL_POINTS,
            GL_RGBA,
            GL_TEXTURE_2D,
            GL_TRUE,
            GL_UNSIGNED_BYTE,
            glBindBuffer,
            glBindFramebuffer,
            glBindTexture,
            glBindVertexArray,
            glBufferData,
            glClear,
            glClearColor,
            glDrawArrays,
            glFlush,
            glGetUniformLocation,
            glUniform2f,
            glUniformMatrix4fv,
            glTexSubImage2D,
            glUseProgram,
            glViewport,
        )

        assert self.sender is not None
        assert self.texture is not None
        assert self.fbo is not None
        assert self.program is not None
        assert self.vbo is not None
        assert self.vao is not None

        render_frame = frame_with_point_budget(frame, self.config.max_render_points, self.config.camera_preset)
        self.last_render_point_count = len(render_frame.points)
        self.last_render_prefix_counts = point_prefix_counts(render_frame.points)
        vertices = frame_to_vertex_array(render_frame, self.config.point_scale)
        glBindFramebuffer(GL_FRAMEBUFFER, self.fbo)
        glViewport(0, 0, self.config.width, self.config.height)
        glClearColor(0.025, 0.035, 0.048, 1.0)
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT)
        glUseProgram(self.program)
        camera = camera_preset(self.config.camera_preset, time.monotonic())
        view_proj = camera_matrix(time.monotonic(), self.config.width / max(1, self.config.height), camera)
        glUniformMatrix4fv(glGetUniformLocation(self.program, "u_view_proj"), 1, GL_TRUE, view_proj.astype(np.float32))
        glUniform2f(glGetUniformLocation(self.program, "u_viewport"), float(self.config.width), float(self.config.height))
        glBindVertexArray(self.vao)
        glBindBuffer(GL_ARRAY_BUFFER, self.vbo)
        glBufferData(GL_ARRAY_BUFFER, vertices.nbytes, vertices, GL_DYNAMIC_DRAW)
        glDrawArrays(GL_POINTS, 0, len(vertices))
        image = rasterize_frame_rgba(render_frame, self.config.width, self.config.height, self.config.point_scale, camera)
        glBindTexture(GL_TEXTURE_2D, self.texture)
        glTexSubImage2D(
            GL_TEXTURE_2D,
            0,
            0,
            0,
            self.config.width,
            self.config.height,
            GL_RGBA,
            GL_UNSIGNED_BYTE,
            image,
        )
        glFlush()
        ok = bool(self.sender.sendTexture(self.texture, GL_TEXTURE_2D, self.config.width, self.config.height, False, self.fbo))
        if ok:
            self.frames_sent += 1
        return ok

    def close(self) -> None:
        if self.sender is not None:
            self.sender.releaseSender()
            self.sender.closeOpenGL()
            self.sender = None


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


VERTEX_SHADER = """
#version 330 core
layout(location = 0) in vec3 in_pos;
layout(location = 1) in vec4 in_color;
layout(location = 2) in float in_radius;
uniform mat4 u_view_proj;
uniform vec2 u_viewport;
out vec4 v_color;

void main() {
    vec2 projected = vec2(in_pos.x * 0.58, (in_pos.z - 1.18) * 1.15 + in_pos.y * 0.16);
    gl_Position = vec4(projected, 0.0, 1.0);
    float radius_px = max(5.0, in_radius * u_viewport.y * 2.8);
    gl_PointSize = radius_px;
    v_color = in_color;
}
"""


FRAGMENT_SHADER = """
#version 330 core
in vec4 v_color;
out vec4 out_color;

void main() {
    vec2 delta = gl_PointCoord * 2.0 - 1.0;
    float radius2 = dot(delta, delta);
    if (radius2 > 1.0) {
        discard;
    }
    float alpha = (1.0 - smoothstep(0.45, 1.0, radius2)) * v_color.a;
    out_color = vec4(v_color.rgb * alpha, alpha);
}
"""
