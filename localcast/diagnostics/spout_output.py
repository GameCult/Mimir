from __future__ import annotations

import ctypes
import json
from pathlib import Path
import time

import numpy as np

from localcast.sensor_fusion.audio_overlay import AudioVisualSyncStatus, overlay_audio_events, with_remote_video_status
from localcast.sensor_fusion.media_artifacts import remote_video_artifact_for_present_time
from localcast.sensor_fusion.render_bridge import RenderFramePacket, RenderPointPacket
from localcast.diagnostics.render_frame_json import load_render_frame_json
from localcast.diagnostics.render_math import (
    SpoutOutputConfig,
    camera_matrix,
    camera_preset,
    demo_render_frame,
    frame_to_vertex_array,
    frame_with_point_budget,
    point_prefix_counts,
    rasterize_frame_rgba,
)
from localcast.diagnostics.visual_cache import CultStreamStatus, get_live_render_frame, put_stream_status


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
