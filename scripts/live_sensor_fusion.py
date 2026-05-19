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
    DenseStereoConfig,
    FusionConfig,
    Observation2D,
    RenderBridgeConfig,
    RenderFramePacket,
    RenderPointPacket,
    SensorRig,
    SurfaceFeatureObservation,
    CameraClockSyncModel,
    CameraChirpPoseConstraint,
    CameraMotionPeak,
    ClapCalibrationEvent,
    TimestampedFrame,
    TimestampedFrameHistory,
    ClapDetectorConfig,
    camera_mic_geometry_from_audio_profile,
    constraints_from_phase_sources,
    detect_clap_events,
    dense_stereo_points,
    estimate_pose_corrections,
    evidence_from_fusion_items,
    evidence_from_render_points,
    get_live_clap_events,
    make_clap_events_frame,
    measure_frame_quality,
    multilod_cache_from_evidence,
    put_live_clap_events,
    put_live_render_frame,
    lower_points_to_render_frame,
    stochastic_transient_matches,
    speaker_geometry_from_audio_profile,
)
from localcast.sensor_fusion.camera_control import AdaptiveCameraController, OpenCvCameraSettingPort
from audio_field.cultcache_audio import frame_to_numpy, get_live_audio_phase_field, get_live_spatial_audio_frame


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


def stochastic_surface_observations(observations: list[Observation2D]) -> list[SurfaceFeatureObservation]:
    surface: list[SurfaceFeatureObservation] = []
    for obs in observations:
        marker_hash = abs(hash(obs.marker_id)) % 255
        descriptor = np.full(32, marker_hash, dtype=np.uint8)
        surface.append(
            SurfaceFeatureObservation(
                sensor_id=obs.sensor_id,
                feature_id=obs.marker_id,
                timestamp_ns=obs.timestamp_ns,
                uv=obs.uv,
                descriptor=descriptor,
                confidence=obs.confidence,
            )
        )
    return surface


def transient_match_render_points(matches, timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
    points: list[RenderPointPacket] = []
    for match in matches:
        confidence = max(0.0, min(1.0, float(match.confidence)))
        points.append(
            render_point(
                f"stochastic:{match.stable_key}",
                match.xyz,
                0.018 + 0.020 * confidence,
                (1.0, 0.88, 0.32, 0.35 + 0.55 * confidence),
                confidence,
                timestamp_ns,
            )
        )
    return tuple(points)


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
        dense_step: int,
        enable_cpu_dense_stereo: bool,
        adaptive_controls: bool,
        fallback_dir: Path,
    ) -> None:
        self.api = api
        self.primary_index = primary_index
        self.secondary_index = secondary_index
        self.width = width
        self.height = height
        self.sample_step = max(4, int(sample_step))
        self.room_step = max(self.sample_step, int(room_step))
        self.dense_step = max(1, int(dense_step))
        self.enable_cpu_dense_stereo = enable_cpu_dense_stereo
        self.adaptive_controls = adaptive_controls
        self.fallback_dir = fallback_dir
        self._captures: dict[int, object] = {}
        self._frames: dict[int, np.ndarray] = {}
        self._timestamped_frames: dict[str, TimestampedFrame] = {}
        self._history = TimestampedFrameHistory(max_frames=96)
        self._controllers: dict[int, AdaptiveCameraController] = {}
        self._setting_ports: dict[int, OpenCvCameraSettingPort] = {}

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
        return self._splats_from_frames(primary, secondary, now_s, timestamp_ns)

    def synced_splats(
        self,
        now_s: float,
        timestamp_ns: int,
        *,
        sync_model: CameraClockSyncModel,
        oracle_time_ns: int,
    ) -> tuple[RenderPointPacket, ...]:
        self._read_frame(self.primary_index)
        self._read_frame(self.secondary_index)
        primary = self._history.nearest("kiyo-primary", sync_model.raw_time_for_oracle("kiyo-primary", oracle_time_ns))
        secondary = self._history.nearest("kiyo-secondary", sync_model.raw_time_for_oracle("kiyo-secondary", oracle_time_ns))
        return self._splats_from_frames(
            None if primary is None else primary.image,
            None if secondary is None else secondary.image,
            now_s,
            timestamp_ns,
        )

    def _splats_from_frames(
        self,
        primary: np.ndarray | None,
        secondary: np.ndarray | None,
        now_s: float,
        timestamp_ns: int,
    ) -> tuple[RenderPointPacket, ...]:
        points: list[RenderPointPacket] = []
        if self.enable_cpu_dense_stereo and primary is not None and secondary is not None:
            points.extend(rgb_dense_stereo_splats(primary, secondary, timestamp_ns, step=self.dense_step))
        if primary is not None:
            points.extend(rgb_room_splats("room-rgb:primary", primary, np.array([-0.42, 0.05, 0.0]), timestamp_ns, step=self.room_step))
            if not points:
                points.extend(rgb_body_splats("host-rgb", primary, np.array([-0.42, 0.05, 0.0]), now_s, timestamp_ns, side=-1.0, step=self.sample_step))
        if secondary is not None:
            points.extend(rgb_room_splats("room-rgb:secondary", secondary, np.array([0.48, 0.28, 0.0]), timestamp_ns, step=self.room_step))
            if not points:
                points.extend(rgb_body_splats("deru-rgb", secondary, np.array([0.48, 0.28, 0.0]), now_s + 0.6, timestamp_ns, side=1.0, step=self.sample_step))
        return tuple(points)

    def latest_timestamped_frames(self) -> dict[str, TimestampedFrame]:
        return dict(self._timestamped_frames)

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
                if self.adaptive_controls:
                    self._controllers[index] = AdaptiveCameraController()
                    self._setting_ports[index] = OpenCvCameraSettingPort(capture)
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
                sensor_id = self._sensor_id(index)
                stamped = TimestampedFrame(sensor_id, time.monotonic_ns(), frame.copy())
                self._timestamped_frames[sensor_id] = stamped
                self._history.add(stamped)
                self._tune_capture(index, frame)
                self._frames[index] = frame
                return frame
        cached = self._frames.get(index)
        if cached is not None:
            return cached
        return self._fallback_frame(index)

    def _tune_capture(self, index: int, frame: np.ndarray) -> None:
        controller = self._controllers.get(index)
        port = self._setting_ports.get(index)
        if controller is None or port is None:
            return
        command = controller.update(measure_frame_quality(frame))
        port.apply(command)

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
                    sensor_id = self._sensor_id(index)
                    stamped = TimestampedFrame(sensor_id, time.monotonic_ns(), frame.copy())
                    self._timestamped_frames[sensor_id] = stamped
                    self._history.add(stamped)
                    return frame
        return None

    def _sensor_id(self, index: int) -> str:
        if index == self.primary_index:
            return "kiyo-primary"
        if index == self.secondary_index:
            return "kiyo-secondary"
        return f"rgb-{index}"


def cv2_api(name: str) -> int:
    import cv2

    return {
        "any": 0,
        "dshow": cv2.CAP_DSHOW,
        "msmf": cv2.CAP_MSMF,
    }[name]


def rgb_dense_stereo_splats(
    primary_bgr: np.ndarray,
    secondary_bgr: np.ndarray,
    timestamp_ns: int,
    *,
    step: int,
) -> tuple[RenderPointPacket, ...]:
    height, width = primary_bgr.shape[:2]
    secondary_height, secondary_width = secondary_bgr.shape[:2]
    width = min(width, secondary_width)
    height = min(height, secondary_height)
    left = rgb_dense_camera("kiyo-primary", -0.18, width, height)
    right = rgb_dense_camera("kiyo-secondary", 0.18, width, height)
    return dense_stereo_points(
        prefix="dense-rgb",
        left_frame_bgr=primary_bgr[:height, :width],
        right_frame_bgr=secondary_bgr[:height, :width],
        left_camera=left,
        right_camera=right,
        timestamp_ns=timestamp_ns,
        config=DenseStereoConfig(sample_step=step, block_radius=3, max_disparity_px=max(24, width // 3), radius_m=0.0045),
    )


def rgb_dense_camera(sensor_id: str, x: float, width: int, height: int) -> CameraModel:
    focal = 0.82 * float(width)
    return CameraModel(
        sensor_id=sensor_id,
        camera_matrix=np.array(
            [
                [focal, 0.0, width * 0.5],
                [0.0, focal, height * 0.5],
                [0.0, 0.0, 1.0],
            ],
            dtype=np.float64,
        ),
        dist_coeffs=np.zeros(5),
        world_from_sensor=np.array(
            [
                [1.0, 0.0, 0.0, x],
                [0.0, 1.0, 0.0, -0.72],
                [0.0, 0.0, 1.0, 1.28],
                [0.0, 0.0, 0.0, 1.0],
            ],
            dtype=np.float64,
        ),
        width=width,
        height=height,
        role="rgb_dense_stereo",
        latency_ms=45.0,
    )


class LiveClapCalibrator:
    def __init__(
        self,
        *,
        audio_cache: Path,
        clap_cache: Path,
        camera_width: int,
        camera_height: int,
        max_frames_per_sensor: int = 24,
    ) -> None:
        self.audio_cache = audio_cache
        self.clap_cache = clap_cache
        self.max_frames_per_sensor = max(2, int(max_frames_per_sensor))
        self.history = TimestampedFrameHistory(max_frames=self.max_frames_per_sensor)
        self.sync_model = CameraClockSyncModel()
        self.last_audio_frame_id: int | None = None
        self.last_audio_time_ns: int | None = None
        self.last_event_key: str | None = None
        left = rgb_dense_camera("kiyo-primary", -0.18, camera_width, camera_height)
        right = rgb_dense_camera("kiyo-secondary", 0.18, camera_width, camera_height)
        self.rig = SensorRig(
            cameras={left.sensor_id: left, right.sensor_id: right},
            config=FusionConfig(max_pair_dt_ns=80_000_000, max_reprojection_error_px=80.0, cache_ttl_ns=1_000_000_000),
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
            self._frames_for_detection(),
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

    def _frames_for_detection(self) -> dict[str, list[TimestampedFrame]]:
        return self.history.frames_by_sensor()

    def oracle_time_ns(self, fallback_ns: int) -> int:
        return int(self.last_audio_time_ns if self.last_audio_time_ns is not None else fallback_ns)

    def _event_points(self, timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
        points: list[RenderPointPacket] = []
        for event in self.events[-4:]:
            confidence = max(0.0, min(1.0, event.visual_confidence * event.acoustic_confidence))
            points.append(
                render_point(
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
                        render_point(
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
                    render_point(
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
                render_point(
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


class LiveChirpPoseMapper:
    def __init__(self, *, phase_cache: Path, audio_profile: Path | None = None) -> None:
        self.phase_cache = phase_cache
        self.camera_mics = None
        self.speakers = None
        if audio_profile is not None and audio_profile.exists():
            loaded_mics = camera_mic_geometry_from_audio_profile(audio_profile)
            loaded_speakers = speaker_geometry_from_audio_profile(audio_profile)
            self.camera_mics = loaded_mics or None
            self.speakers = loaded_speakers or None
        self.last_frame_id: int | None = None
        self.constraints: tuple[CameraChirpPoseConstraint, ...] = ()

    def update(self, fallback_timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
        if not self.phase_cache.exists():
            return self._constraint_points(fallback_timestamp_ns)
        field = get_live_audio_phase_field(self.phase_cache)
        if field is None:
            return self._constraint_points(fallback_timestamp_ns)
        if field.frame_id != self.last_frame_id:
            self.last_frame_id = field.frame_id
            self.constraints = constraints_from_phase_sources(
                field.sources,
                audio_time_ns=field.audio_time_ns,
                camera_mics=self.camera_mics,
                speakers=self.speakers,
            )
        return self._constraint_points(field.audio_time_ns)

    def _constraint_points(self, timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
        points: list[RenderPointPacket] = []
        for constraint in self.constraints:
            points.extend(chirp_pose_constraint_points(constraint, timestamp_ns))
        for estimate in estimate_pose_corrections(self.constraints):
            points.extend(chirp_pose_correction_points(estimate, timestamp_ns))
        return tuple(points)


def chirp_pose_constraint_points(
    constraint: CameraChirpPoseConstraint,
    timestamp_ns: int,
) -> tuple[RenderPointPacket, ...]:
    residual = float(constraint.range_residual_m)
    confidence = max(0.0, min(1.0, float(constraint.confidence)))
    camera = np.asarray(constraint.camera_position_m, dtype=np.float64)
    speaker = np.asarray(constraint.speaker_position_m, dtype=np.float64)
    direction = camera - speaker
    norm = float(np.linalg.norm(direction))
    if norm <= 1.0e-9:
        direction = np.array([0.0, 0.0, 1.0], dtype=np.float64)
        norm = 1.0
    unit = direction / norm
    corrected = speaker + unit * float(constraint.observed_range_m)
    midpoint = speaker + unit * (0.5 * (float(constraint.nominal_range_m) + float(constraint.observed_range_m)))
    residual_strength = min(1.0, abs(residual) / 0.25)
    return (
        render_point(
            f"{constraint.stable_key}:body",
            corrected,
            0.030 + 0.045 * residual_strength,
            (0.95, 0.78, 0.24, 0.38 + 0.42 * confidence),
            confidence,
            timestamp_ns,
        ),
        render_point(
            f"{constraint.stable_key}:range",
            midpoint,
            0.012 + 0.030 * residual_strength,
            (1.0, 0.55, 0.18, 0.22 + 0.36 * confidence),
            confidence * 0.85,
            timestamp_ns,
        ),
    )


def chirp_pose_correction_points(estimate, timestamp_ns: int) -> tuple[RenderPointPacket, ...]:
    confidence = max(0.0, min(1.0, float(estimate.confidence)))
    start = np.asarray(estimate.position_m, dtype=np.float64)
    correction = np.asarray(estimate.correction_m, dtype=np.float64)
    end = start + correction
    magnitude = float(np.linalg.norm(correction))
    strength = min(1.0, magnitude / 0.25)
    alpha = 0.22 + 0.55 * strength * confidence
    return (
        render_point(
            f"camera-pose-correction:{estimate.sensor_id}:origin",
            start,
            0.022 + 0.045 * strength,
            (0.35, 0.78, 1.0, alpha),
            confidence,
            timestamp_ns,
        ),
        render_point(
            f"camera-pose-correction:{estimate.sensor_id}:target",
            end,
            0.028 + 0.055 * strength,
            (1.0, 0.90, 0.30, alpha),
            confidence,
            timestamp_ns,
        ),
    )


def pixel_ray_point(camera: CameraModel, uv: np.ndarray, *, distance_m: float) -> np.ndarray:
    point = np.asarray([float(uv[0]), float(uv[1]), 1.0], dtype=np.float64)
    direction_sensor = np.linalg.inv(camera.camera_matrix) @ point
    direction_sensor = direction_sensor / max(1.0e-12, np.linalg.norm(direction_sensor))
    direction_world = camera.world_from_sensor[:3, :3] @ direction_sensor
    direction_world = direction_world / max(1.0e-12, np.linalg.norm(direction_world))
    return camera.position_world + direction_world * float(distance_m)


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
        self._last_frame_kind: str | None = None
        self._latest_timestamped_frame: TimestampedFrame | None = None
        self._fallback_index = 0

    @property
    def last_timestamp_ns(self) -> int | None:
        return self._last_timestamp_ns

    @property
    def last_frame_kind(self) -> str | None:
        return self._last_frame_kind

    def latest_timestamped_frames(self) -> dict[str, TimestampedFrame]:
        if self._latest_timestamped_frame is None:
            return {}
        return {self._latest_timestamped_frame.sensor_id: self._latest_timestamped_frame}

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
        self._latest_timestamped_frame = TimestampedFrame("leap", self._last_timestamp_ns, frame.copy())
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
            self._last_frame_kind = "capture"
            return frame
        return self._fallback_frame()

    def _fallback_frame(self) -> np.ndarray | None:
        import cv2

        path = self.fallback_dir / "leap-probe.png"
        if path.exists():
            frame = cv2.imread(str(path))
            if frame is not None:
                self._last_frame_kind = "fallback-file"
                return frame
        return self._synthetic_motion_frame()

    def _synthetic_motion_frame(self) -> np.ndarray:
        self._last_frame_kind = "fallback-synthetic"
        frame = np.zeros((self.height, self.width, 3), dtype=np.uint8)
        t = self._fallback_index
        self._fallback_index += 1
        cx = int((self.width * 0.30) + (self.width * 0.40) * ((np.sin(t * 0.37) + 1.0) * 0.5))
        cy = int((self.height * 0.30) + (self.height * 0.34) * ((np.cos(t * 0.41) + 1.0) * 0.5))
        y0 = max(0, cy - 4)
        y1 = min(self.height, cy + 5)
        x0 = max(0, cx - 4)
        x1 = min(self.width, cx + 5)
        frame[y0:y1, x0:x1, 1] = 220
        frame[max(0, y0 - 8) : min(self.height, y1 + 8), max(0, x0 - 8) : min(self.width, x1 + 8), 2] = 80
        frame[max(0, y0 - 12) : min(self.height, y1 + 12), max(0, x0 - 12) : min(self.width, x1 + 12), 0] = 55
        return frame


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
    parser.add_argument("--cpu-dense-stereo", action="store_true", help="Debug-only CPU stereo matcher. Production dense fusion belongs on the GPU.")
    parser.add_argument("--rgb-dense-step", type=int, default=16)
    parser.add_argument("--no-adaptive-camera-controls", action="store_true")
    parser.add_argument("--no-leap", action="store_true")
    parser.add_argument("--leap-api", choices=["any", "dshow", "msmf"], default="msmf")
    parser.add_argument("--leap-index", type=int, default=0)
    parser.add_argument("--leap-width", type=int, default=320)
    parser.add_argument("--leap-height", type=int, default=240)
    parser.add_argument("--leap-fps", type=float, default=120.0)
    parser.add_argument("--leap-step", type=int, default=12)
    parser.add_argument("--lod-cache", default=str(ROOT / "calibration" / "runs" / "visual-lod-cache.json"))
    parser.add_argument("--audio-cache", default=str(ROOT / "calibration" / "runs" / "audio-state.msgpack"))
    parser.add_argument("--phase-cache", default=str(ROOT / "calibration" / "runs" / "audio-phase-field.msgpack"))
    parser.add_argument("--audio-profile", default=str(ROOT / "config" / "audio-field.json"))
    parser.add_argument("--clap-cache", default=str(ROOT / "calibration" / "runs" / "clap-events.msgpack"))
    parser.add_argument("--no-clap-calibration", action="store_true")
    parser.add_argument("--no-chirp-pose", action="store_true")
    parser.add_argument("--no-stochastic-transients", action="store_true")
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
    lod_cache_path = Path(args.lod_cache)
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
        dense_step=args.rgb_dense_step,
        enable_cpu_dense_stereo=args.cpu_dense_stereo,
        adaptive_controls=not args.no_adaptive_camera_controls,
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
    clap_calibrator = None if args.no_clap_calibration else LiveClapCalibrator(
        audio_cache=Path(args.audio_cache),
        clap_cache=Path(args.clap_cache),
        camera_width=args.rgb_width,
        camera_height=args.rgb_height,
    )
    audio_profile = Path(args.audio_profile)
    if not audio_profile.exists() and audio_profile.name == "audio-field.json":
        example_profile = audio_profile.with_name("audio-field.example.json")
        audio_profile = example_profile if example_profile.exists() else audio_profile
    chirp_pose_mapper = None if args.no_chirp_pose else LiveChirpPoseMapper(
        phase_cache=Path(args.phase_cache),
        audio_profile=audio_profile if audio_profile.exists() else None,
    )
    try:
        while True:
            now = time.monotonic()
            if args.duration is not None and now - start >= args.duration:
                break
            observations = synthetic_observations(rig, now, args.points)
            result = rig.fuse(observations)
            transient_matches = () if args.no_stochastic_transients else stochastic_transient_matches(
                stochastic_surface_observations(observations),
                rig.cameras,
                max_descriptor_distance=0.0,
                max_reprojection_error_px=0.05,
                samples_per_observation=2,
                seed=frame_id,
            )
            frame = lower_points_to_render_frame(
                result.points,
                render_config,
                frame_id=frame_id,
                created_monotonic_ns=time.monotonic_ns(),
            )
            timestamp_ns = frame.source_time_max_ns
            leap_points = () if leap_sampler is None else leap_sampler.motion_splats(timestamp_ns)
            if clap_calibrator is not None:
                if rgb_sampler is not None:
                    clap_calibrator.observe_frames(rgb_sampler.latest_timestamped_frames())
                if leap_sampler is not None:
                    clap_calibrator.observe_frames(leap_sampler.latest_timestamped_frames())
            if leap_sampler is not None and leap_sampler.last_timestamp_ns is not None:
                timestamp_ns = max(timestamp_ns, leap_sampler.last_timestamp_ns)
            leap_source_kind = (
                "leap-ground-truth"
                if leap_sampler is not None and leap_sampler.last_frame_kind == "capture"
                else "leap-fallback"
            )
            leap_source_priority = 3.0 if leap_source_kind == "leap-ground-truth" else 1.1
            rgb_points = ()
            if rgb_sampler is not None:
                if clap_calibrator is not None and clap_calibrator.sync_model.estimates():
                    rgb_points = rgb_sampler.synced_splats(
                        now,
                        timestamp_ns,
                        sync_model=clap_calibrator.sync_model,
                        oracle_time_ns=clap_calibrator.oracle_time_ns(timestamp_ns),
                    )
                else:
                    rgb_points = rgb_sampler.splats(now, timestamp_ns)
            support_points = dim_fusion_points(tuple(frame.points)) if not rgb_points else ()
            stochastic_points = transient_match_render_points(transient_matches, timestamp_ns)
            clap_points = () if clap_calibrator is None else clap_calibrator.update(timestamp_ns)
            chirp_pose_points = () if chirp_pose_mapper is None else chirp_pose_mapper.update(timestamp_ns)
            multilod_cache_from_evidence(
                evidence_from_render_points(leap_points, source_kind=leap_source_kind, source_priority=leap_source_priority)
                + evidence_from_render_points(rgb_points, source_kind="rgb-surface", source_priority=1.4)
                + evidence_from_render_points(clap_points, source_kind="clap-calibration", source_priority=2.4)
                + evidence_from_render_points(chirp_pose_points, source_kind="chirp-camera-pose", source_priority=1.8)
                + evidence_from_fusion_items(transient_matches, source_kind="stochastic-transient", source_priority=1.2)
                + evidence_from_fusion_items(result.points, source_kind="synthetic-fusion", source_priority=0.8),
                levels=(0.01, 0.04, 0.16, 0.64),
                created_monotonic_ns=time.monotonic_ns(),
            ).write_json(lod_cache_path)
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
                points=clap_points + chirp_pose_points + leap_points + rgb_points + stochastic_points + support_points,
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
