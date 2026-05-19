"""Diagnostic live clap calibration against typed CultCache audio frames."""

from pathlib import Path

import numpy as np

from audio_field.cultcache_audio import frame_to_numpy, get_live_spatial_audio_frame
from localcast.diagnostics.visual_cache import get_live_clap_events, make_clap_events_frame, put_live_clap_events
from localcast.sensor_fusion import (
    CameraClockSyncModel,
    CameraMotionPeak,
    ClapCalibrationEvent,
    ClapDetectorConfig,
    FusionConfig,
    SensorRig,
    TimestampedFrame,
    TimestampedFrameHistory,
    detect_clap_events,
)
from localcast.sensor_fusion.core import CameraModel
from localcast.sensor_fusion.render_bridge import RenderPointPacket
from localcast.sensor_fusion.reservoir_window import DEFAULT_RESERVOIR_NS
from localcast.sensor_fusion.rgb_reference import rgb_dense_camera


class LiveClapCalibrator:
    def __init__(
        self,
        *,
        audio_cache: Path,
        clap_cache: Path,
        camera_width: int,
        camera_height: int,
        max_frames_per_sensor: int = 720,
        reservoir_ns: int = DEFAULT_RESERVOIR_NS,
    ) -> None:
        self.audio_cache = audio_cache
        self.clap_cache = clap_cache
        self.max_frames_per_sensor = max(2, int(max_frames_per_sensor))
        self.history = TimestampedFrameHistory(max_frames=self.max_frames_per_sensor, max_age_ns=reservoir_ns)
        self.sync_model = CameraClockSyncModel()
        self.last_audio_frame_id: int | None = None
        self.last_audio_time_ns: int | None = None
        self.last_event_key: str | None = None
        left = rgb_dense_camera("kiyo-primary", -0.18, camera_width, camera_height)
        right = rgb_dense_camera("kiyo-secondary", 0.18, camera_width, camera_height)
        self.rig = SensorRig(
            cameras={left.sensor_id: left, right.sensor_id: right},
            config=FusionConfig(max_pair_dt_ns=80_000_000, max_reprojection_error_px=80.0, cache_ttl_ns=reservoir_ns),
        )
        self.config = ClapDetectorConfig(
            audio_window_samples=96,
            audio_hop_samples=24,
            audio_onset_ratio=2.0,
            min_audio_energy=1.0e-8,
            min_camera_motion_score=0.010,
            camera_window_ns=2_500_000_000,
            min_camera_count=2,
            min_event_spacing_ns=220_000_000,
        )
        self.events = ()
        self._restore_existing_events()

    def _restore_existing_events(self) -> None:
        if not self.clap_cache.exists():
            return
        frame = get_live_clap_events(self.clap_cache)
        if frame is None or not frame.events:
            return
        events: list[ClapCalibrationEvent] = []
        for event in frame.events:
            peaks = tuple(
                CameraMotionPeak(
                    sensor_id=str(peak.get("sensorId", "")),
                    timestamp_ns=int(peak.get("timestampNs", 0)),
                    uv=np.asarray(peak.get("uv", [0.0, 0.0]), dtype=np.float64),
                    score=float(peak.get("score", 0.0)),
                )
                for peak in event.camera_peaks
            )
            restored = ClapCalibrationEvent(
                stable_key=event.stable_key,
                position_m=event.position_m,
                acoustic_oracle_ns=event.acoustic_oracle_ns,
                visual_observed_ns=event.visual_observed_ns,
                timing_uncertainty_us=event.timing_uncertainty_us,
                visual_confidence=event.visual_confidence,
                acoustic_confidence=event.acoustic_confidence,
                camera_peaks=peaks,
            )
            self.sync_model.observe_event(restored)
            events.append(restored)
        self.events = tuple(events)

    def observe_frames(self, frames: dict[str, TimestampedFrame]) -> None:
        self.history.add_many(frames)

    def update(self, frame_id: int) -> tuple[RenderPointPacket, ...]:
        audio = self._read_audio()
        if audio is None or audio.frame_id == self.last_audio_frame_id:
            return self._event_points(frame_id)
        self.last_audio_frame_id = audio.frame_id
        self.last_audio_time_ns = int(audio.audio_time_ns)
        block = frame_to_numpy(audio)
        events = detect_clap_events(
            block,
            audio.sample_rate,
            self.history.frames_by_sensor(),
            audio_time_ns=audio.audio_time_ns,
            start_sample=audio.start_sample,
            rig=self.rig,
            config=self.config,
        )
        if events:
            self.events = tuple(events)
            self.last_event_key = events[-1].stable_key
            for event in events:
                self.sync_model.observe_event(event)
            put_live_clap_events(self.clap_cache, make_clap_events_frame(frame_id=frame_id, events=events))
        elif not self.events:
            put_live_clap_events(self.clap_cache, make_clap_events_frame(frame_id=frame_id, events=()))
        return self._event_points(frame_id)

    def _read_audio(self):
        if not self.audio_cache.exists():
            return None
        return get_live_spatial_audio_frame(self.audio_cache)

    def oracle_time_ns(self, fallback_ns: int) -> int:
        return int(self.last_audio_time_ns if self.last_audio_time_ns is not None else fallback_ns)

    def _event_points(self, timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
        points: list[RenderPointPacket] = []
        for event in self.events[-4:]:
            confidence = max(0.0, min(1.0, event.visual_confidence * event.acoustic_confidence))
            points.append(
                _render_point(
                    f"clap-calibration:{event.stable_key}",
                    np.asarray(event.position_m, dtype=np.float64),
                    0.075,
                    (0.15, 0.95, 1.0, 0.72),
                    confidence,
                    timestamp_ns,
                )
            )
            for peak in event.camera_peaks:
                camera = self.rig.cameras.get(peak.sensor_id)
                if peak.sensor_id == "leap":
                    points.append(
                        _render_point(
                            f"clap-timing:{event.stable_key}:leap",
                            np.asarray(event.position_m, dtype=np.float64) + np.array([0.0, -0.035, 0.12], dtype=np.float64),
                            0.04,
                            (0.45, 1.0, 0.72, 0.82),
                            max(0.0, min(1.0, peak.score)),
                            int(peak.timestamp_ns),
                        )
                    )
                    continue
                if camera is None:
                    continue
                ray = pixel_ray_point(camera, peak.uv, distance_m=1.2)
                points.append(
                    _render_point(
                        f"clap-ray:{event.stable_key}:{peak.sensor_id}",
                        ray,
                        0.026,
                        (0.2, 0.85, 1.0, 0.42),
                        max(0.0, min(1.0, peak.score)),
                        int(peak.timestamp_ns),
                    )
                )
        for estimate in self.sync_model.estimates():
            points.append(
                _render_point(
                    f"camera-sync:{estimate.sensor_id}",
                    sync_status_position(estimate.sensor_id),
                    0.035,
                    (0.45, 0.72, 1.0, 0.58),
                    estimate.confidence,
                    estimate.oracle_timestamp_ns,
                )
            )
        return tuple(points)


def sync_status_position(sensor_id: str) -> np.ndarray:
    positions = {
        "kiyo-primary": np.array([-0.18, -0.72, 1.28], dtype=np.float64),
        "kiyo-secondary": np.array([0.18, -0.72, 1.28], dtype=np.float64),
        "leap": np.array([0.0, -0.18, 0.76], dtype=np.float64),
    }
    return positions.get(sensor_id, np.zeros(3, dtype=np.float64))


def pixel_ray_point(camera: CameraModel, uv: np.ndarray, *, distance_m: float) -> np.ndarray:
    point = np.asarray([float(uv[0]), float(uv[1]), 1.0], dtype=np.float64)
    direction_sensor = np.linalg.inv(camera.camera_matrix) @ point
    direction_sensor = direction_sensor / max(1.0e-12, np.linalg.norm(direction_sensor))
    direction_world = camera.world_from_sensor[:3, :3] @ direction_sensor
    direction_world = direction_world / max(1.0e-12, np.linalg.norm(direction_world))
    return camera.position_world + direction_world * float(distance_m)


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
