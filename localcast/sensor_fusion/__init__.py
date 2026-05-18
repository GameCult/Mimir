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
from .camera_control import (
    AdaptiveCameraController,
    CameraControlCommand,
    CameraControlState,
    CameraQualityTarget,
    FrameQuality,
    measure_frame_quality,
)
from .dense_stereo import DenseStereoConfig, dense_stereo_points
from .calibration_space import (
    BoardObservation,
    BoardSpec,
    CameraIntrinsics,
    CameraPoseSolve,
    camera_models_to_config,
    solve_camera_pose_from_fixed_board,
    solve_common_space_from_fixed_board,
)
from .surface_features import (
    SurfaceFeatureObservation,
    SurfaceFeatureTrack,
    match_surface_features,
    orb_surface_observations,
    triangulate_surface_tracks,
)
from .audio_overlay import AudioVisualSyncStatus, overlay_audio_events
from .media_artifacts import RemoteVideoArtifact, remote_video_artifact_for_present_time
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
    "CameraIntrinsics",
    "BoardObservation",
    "BoardSpec",
    "CameraPoseSolve",
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
    "AdaptiveCameraController",
    "CameraControlCommand",
    "CameraControlState",
    "CameraQualityTarget",
    "DenseStereoConfig",
    "FrameQuality",
    "SurfaceFeatureObservation",
    "SurfaceFeatureTrack",
    "AudioVisualSyncStatus",
    "RemoteVideoArtifact",
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
    "dense_stereo_points",
    "camera_models_to_config",
    "match_surface_features",
    "measure_frame_quality",
    "orb_surface_observations",
    "overlay_audio_events",
    "remote_video_artifact_for_present_time",
    "put_live_render_frame",
    "solve_camera_pose_from_fixed_board",
    "solve_common_space_from_fixed_board",
    "triangulate_surface_tracks",
]
