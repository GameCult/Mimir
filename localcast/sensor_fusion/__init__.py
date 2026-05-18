"""Sensor fusion spine for the LocalCastBridge spatial rig."""

from .core import (
    CameraModel,
    FusionConfig,
    FusionResult,
    Observation2D,
    PointCloud,
    SensorRig,
    TrackCache,
    TriangulatedPoint,
    load_fusion_config,
)
from .render_bridge import RenderBridgeConfig, RenderFramePacket, RenderPointPacket, lower_points_to_render_frame

__all__ = [
    "CameraModel",
    "FusionConfig",
    "FusionResult",
    "Observation2D",
    "PointCloud",
    "SensorRig",
    "TrackCache",
    "TriangulatedPoint",
    "load_fusion_config",
    "RenderBridgeConfig",
    "RenderFramePacket",
    "RenderPointPacket",
    "lower_points_to_render_frame",
]
