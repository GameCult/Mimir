from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .core import TriangulatedPoint


@dataclass(frozen=True)
class RenderBridgeConfig:
    spout_sender_name: str = "LocalCastBridge Point Cloud"
    target_width: int = 1920
    target_height: int = 1080
    visual_delay_ns: int = 250_000_000
    audio_alignment_delay_ns: int = 250_000_000
    default_point_radius_m: float = 0.025

    @staticmethod
    def from_dict(data: dict) -> "RenderBridgeConfig":
        return RenderBridgeConfig(
            spout_sender_name=data.get("spout_sender_name", "LocalCastBridge Point Cloud"),
            target_width=int(data.get("target_width", 1920)),
            target_height=int(data.get("target_height", 1080)),
            visual_delay_ns=int(data.get("visual_delay_ns", 250_000_000)),
            audio_alignment_delay_ns=int(data.get("audio_alignment_delay_ns", 250_000_000)),
            default_point_radius_m=float(data.get("default_point_radius_m", 0.025)),
        )


@dataclass(frozen=True)
class RenderPointPacket:
    stable_key: str
    xyz: np.ndarray
    radius_m: float
    color_rgba: tuple[float, float, float, float]
    confidence: float
    source_timestamp_ns: int

    def to_dict(self) -> dict:
        return {
            "stable_key": self.stable_key,
            "xyz": [float(v) for v in self.xyz],
            "radius_m": float(self.radius_m),
            "color_rgba": [float(v) for v in self.color_rgba],
            "confidence": float(self.confidence),
            "source_timestamp_ns": int(self.source_timestamp_ns),
        }

    @staticmethod
    def from_dict(data: dict) -> "RenderPointPacket":
        return RenderPointPacket(
            stable_key=str(data.get("stable_key", "")),
            xyz=np.asarray(data["xyz"], dtype=np.float64),
            radius_m=float(data.get("radius_m", 0.025)),
            color_rgba=tuple(float(v) for v in data.get("color_rgba", [1.0, 1.0, 1.0, 1.0])),
            confidence=float(data.get("confidence", 1.0)),
            source_timestamp_ns=int(data.get("source_timestamp_ns", 0)),
        )


@dataclass(frozen=True)
class RenderFramePacket:
    schema: str
    frame_id: int
    created_monotonic_ns: int
    source_time_min_ns: int
    source_time_max_ns: int
    present_time_ns: int
    audio_alignment_time_ns: int
    spout_sender_name: str
    target_width: int
    target_height: int
    points: tuple[RenderPointPacket, ...]

    def to_dict(self) -> dict:
        return {
            "schema": self.schema,
            "frame_id": self.frame_id,
            "created_monotonic_ns": self.created_monotonic_ns,
            "source_time_min_ns": self.source_time_min_ns,
            "source_time_max_ns": self.source_time_max_ns,
            "present_time_ns": self.present_time_ns,
            "audio_alignment_time_ns": self.audio_alignment_time_ns,
            "spout_sender_name": self.spout_sender_name,
            "target_width": self.target_width,
            "target_height": self.target_height,
            "points": [point.to_dict() for point in self.points],
        }

    @staticmethod
    def from_dict(data: dict) -> "RenderFramePacket":
        return RenderFramePacket(
            schema=str(data.get("schema", "")),
            frame_id=int(data.get("frame_id", 0)),
            created_monotonic_ns=int(data.get("created_monotonic_ns", 0)),
            source_time_min_ns=int(data.get("source_time_min_ns", 0)),
            source_time_max_ns=int(data.get("source_time_max_ns", 0)),
            present_time_ns=int(data.get("present_time_ns", 0)),
            audio_alignment_time_ns=int(data.get("audio_alignment_time_ns", 0)),
            spout_sender_name=str(data.get("spout_sender_name", "LocalCastBridge Point Cloud")),
            target_width=int(data.get("target_width", 1920)),
            target_height=int(data.get("target_height", 1080)),
            points=tuple(RenderPointPacket.from_dict(item) for item in data.get("points", [])),
        )


def lower_points_to_render_frame(
    points: tuple[TriangulatedPoint, ...],
    config: RenderBridgeConfig,
    frame_id: int,
    created_monotonic_ns: int,
) -> RenderFramePacket:
    if points:
        source_min = min(point.timestamp_ns for point in points)
        source_max = max(point.timestamp_ns for point in points)
    else:
        source_min = created_monotonic_ns
        source_max = created_monotonic_ns
    render_points = tuple(point_to_render_packet(point, config) for point in points)
    present_time = source_max + config.visual_delay_ns
    return RenderFramePacket(
        schema="localcast.sensor_fusion.render_frame.v1",
        frame_id=frame_id,
        created_monotonic_ns=created_monotonic_ns,
        source_time_min_ns=source_min,
        source_time_max_ns=source_max,
        present_time_ns=present_time,
        audio_alignment_time_ns=source_max + config.audio_alignment_delay_ns,
        spout_sender_name=config.spout_sender_name,
        target_width=config.target_width,
        target_height=config.target_height,
        points=render_points,
    )


def point_to_render_packet(point: TriangulatedPoint, config: RenderBridgeConfig) -> RenderPointPacket:
    confidence = max(0.0, min(1.0, float(point.confidence)))
    return RenderPointPacket(
        stable_key=point.marker_id,
        xyz=point.xyz.astype(np.float64),
        radius_m=config.default_point_radius_m,
        color_rgba=(0.25 + 0.75 * confidence, 0.65, 1.0, confidence),
        confidence=confidence,
        source_timestamp_ns=point.timestamp_ns,
    )
