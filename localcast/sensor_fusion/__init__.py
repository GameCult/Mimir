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
from .cultcache_docs import (
    LIVE_RENDER_FRAME_KEY,
    CultRenderFrame,
    CultRenderPoint,
    CultStreamStatus,
    get_live_render_frame,
    put_live_render_frame,
)
from .spout_output import ScreenBrushPacket, SpoutOutputConfig, frame_to_vertex_array, lower_frame_to_screen_brushes

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
    "LIVE_RENDER_FRAME_KEY",
    "CultRenderFrame",
    "CultRenderPoint",
    "CultStreamStatus",
    "ScreenBrushPacket",
    "SpoutOutputConfig",
    "frame_to_vertex_array",
    "get_live_render_frame",
    "lower_frame_to_screen_brushes",
    "lower_points_to_render_frame",
    "put_live_render_frame",
]
