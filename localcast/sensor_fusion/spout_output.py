from __future__ import annotations

from dataclasses import dataclass
import ctypes
import hashlib
import json
import math
from pathlib import Path
import time

import numpy as np

from .render_bridge import RenderFramePacket, RenderPointPacket, load_render_frame
from .cultcache_docs import CultStreamStatus, get_live_render_frame, put_stream_status


@dataclass(frozen=True)
class SpoutOutputConfig:
    sender_name: str = "LocalCastBridge Point Cloud"
    width: int = 1920
    height: int = 1080
    fps: float = 60.0
    point_scale: float = 1.0
    demo_point_count: int = 4096


@dataclass(frozen=True)
class ScreenBrushPacket:
    center_px: tuple[int, int]
    radii_px: tuple[int, int]
    rotation_deg: float
    color_rgba: tuple[int, int, int, int]


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
                self._frame = load_render_frame(self.path)
                self._mtime_ns = mtime_ns
            return self._frame
        if not self.demo_if_missing:
            raise FileNotFoundError(self.path)
        return demo_render_frame(now_s, config)


class CultCacheRenderFrameSource:
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
                self._frame = get_live_render_frame(self.path)
                self._mtime_ns = mtime_ns
            if self._frame is not None:
                return self._frame
        if not self.demo_if_missing:
            raise FileNotFoundError(self.path)
        return demo_render_frame(now_s, config)


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


def lower_frame_to_screen_brushes(
    frame: RenderFramePacket,
    width: int,
    height: int,
    point_scale: float = 1.0,
) -> tuple[ScreenBrushPacket, ...]:
    brushes: list[ScreenBrushPacket] = []
    for point in frame.points:
        x, y, z = point.xyz
        px = int(round((0.5 + float(x) * 0.29) * width))
        py = int(round((0.5 - ((float(z) - 1.18) * 0.575 + float(y) * 0.08)) * height))
        radius = max(2, int(round(float(point.radius_m) * point_scale * height * 0.75)))
        if px < -radius * 3 or px >= width + radius * 3 or py < -radius * 3 or py >= height + radius * 3:
            continue
        stable = hashlib.blake2s(point.stable_key.encode("utf-8"), digest_size=4).digest()
        angle = int.from_bytes(stable[:2], "little") / 65535.0 * 180.0
        stretch = 1.0 + (stable[2] / 255.0) * 1.8
        radii = (max(2, int(round(radius * stretch))), radius)
        rgba = np.clip(np.asarray(point.color_rgba, dtype=np.float32), 0.0, 1.0)
        rgba[3] *= max(0.0, min(1.0, float(point.confidence)))
        color = tuple(int(v) for v in (rgba * 255.0)[:4])
        brushes.append(ScreenBrushPacket((px, py), radii, angle, color))
    return tuple(brushes)


def rasterize_frame_rgba(frame: RenderFramePacket, width: int, height: int, point_scale: float = 1.0) -> np.ndarray:
    import cv2

    image = np.zeros((height, width, 4), dtype=np.uint8)
    image[:, :, 0] = 10
    image[:, :, 1] = 14
    image[:, :, 2] = 20
    image[:, :, 3] = 255
    for brush in lower_frame_to_screen_brushes(frame, width, height, point_scale):
        cv2.ellipse(
            image,
            brush.center_px,
            brush.radii_px,
            brush.rotation_deg,
            0.0,
            360.0,
            brush.color_rgba,
            thickness=-1,
            lineType=cv2.LINE_AA,
        )
    return image


def write_status(
    path: Path,
    *,
    sender_name: str,
    frames_sent: int,
    point_count: int,
    frame_path: Path | None,
    last_error: str | None = None,
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

        vertices = frame_to_vertex_array(frame, self.config.point_scale)
        glBindFramebuffer(GL_FRAMEBUFFER, self.fbo)
        glViewport(0, 0, self.config.width, self.config.height)
        glClearColor(0.025, 0.035, 0.048, 1.0)
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT)
        glUseProgram(self.program)
        view_proj = camera_matrix(time.monotonic(), self.config.width / max(1, self.config.height))
        glUniformMatrix4fv(glGetUniformLocation(self.program, "u_view_proj"), 1, GL_TRUE, view_proj.astype(np.float32))
        glUniform2f(glGetUniformLocation(self.program, "u_viewport"), float(self.config.width), float(self.config.height))
        glBindVertexArray(self.vao)
        glBindBuffer(GL_ARRAY_BUFFER, self.vbo)
        glBufferData(GL_ARRAY_BUFFER, vertices.nbytes, vertices, GL_DYNAMIC_DRAW)
        glDrawArrays(GL_POINTS, 0, len(vertices))
        image = rasterize_frame_rgba(frame, self.config.width, self.config.height, self.config.point_scale)
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


def camera_matrix(now_s: float, aspect: float) -> np.ndarray:
    eye = np.array([2.4 * math.sin(now_s * 0.08), -3.4, 1.85 + 0.2 * math.cos(now_s * 0.11)], dtype=np.float64)
    target = np.array([0.0, 0.0, 1.15], dtype=np.float64)
    up = np.array([0.0, 0.0, 1.0], dtype=np.float64)
    return perspective(math.radians(58.0), aspect, 0.05, 50.0) @ look_at(eye, target, up)


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
