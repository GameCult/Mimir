import io
from pathlib import Path
import tempfile
import time
import unittest

import numpy as np

from localcast.sensor_fusion import (
    AdaptiveCameraController,
    BoardObservation,
    BoardSpec,
    CameraMicGeometry,
    CameraModel,
    CameraClockSyncModel,
    CameraIntrinsics,
    CameraQualityTarget,
    ClapDetectorConfig,
    DenseStereoConfig,
    FusionConfig,
    LODEvidencePoint,
    Observation2D,
    PointCloud,
    RenderBridgeConfig,
    RenderFramePacket,
    RenderPointPacket,
    SensorRig,
    SpeakerGeometry,
    TimestampedFrame,
    TimestampedFrameHistory,
    TrackCache,
    camera_mic_geometry_from_audio_profile,
    camera_sensor_id_for_microphone,
    constraints_from_phase_sources,
    detect_camera_motion_peaks,
    detect_clap_events,
    estimate_pose_corrections,
    dense_stereo_points,
    get_live_clap_events,
    get_live_render_frame,
    lower_points_to_render_frame,
    match_surface_features,
    overlay_audio_events,
    make_clap_events_frame,
    put_live_clap_events,
    put_live_render_frame,
    remote_video_artifact_for_present_time,
    phase_source_id_for_microphone,
    solve_common_space_from_fixed_board,
    speaker_geometry_from_audio_profile,
    SurfaceFeatureObservation,
    triangulate_surface_tracks,
)
from localcast.sensor_fusion.audio_overlay import clamp_frame_to_audio_reservoir
from localcast.diagnostics.spout_output import (
    SpoutOutputConfig,
    VirtualCamera,
    frame_to_vertex_array,
    frame_with_point_budget,
    lower_frame_to_screen_brushes,
    point_prefix_counts,
    rasterize_frame_rgba,
)
from scripts.diagnostic_live_sensor_fusion import (
    DEFAULT_RESERVOIR_NS,
    LiveClapCalibrator,
    evidence_in_reservoir,
    leap_channel_motion_points,
    render_points_in_reservoir,
    rgb_dense_camera,
    rgb_dense_stereo_splats,
    rgb_room_splats,
    unpack_leap_packed_channels,
)
from localcast.sensor_fusion.active_illumination import ActiveIlluminationController, IlluminationPulsePlan
from audio_field.cultcache_audio import make_audio_source_events, make_spatial_audio_frame, put_live_spatial_audio_frame
from localcast.sensor_fusion.adapters import read_raw_bgr_frames
from localcast.sensor_fusion.adapters import LatestFramePump, FramePacket


def camera(sensor_id, x):
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


class SensorFusionTests(unittest.TestCase):
    def test_triangulates_marker_from_two_calibrated_cameras(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        rig = SensorRig(
            cameras={"left": left, "right": right},
            config=FusionConfig(max_pair_dt_ns=30_000_000, max_reprojection_error_px=0.01),
        )
        target = np.array([0.05, 0.02, 2.0])
        observations = [
            Observation2D("left", "ball", 1_000_000_000, left.project_world(target), 0.9),
            Observation2D("right", "ball", 1_010_000_000, right.project_world(target), 0.8),
        ]

        result = rig.fuse(observations)

        self.assertEqual(1, len(result.points))
        np.testing.assert_allclose(target, result.points[0].xyz, atol=1e-9)
        self.assertGreater(result.points[0].confidence, 0.5)

    def test_rejects_observations_outside_time_window(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        rig = SensorRig(cameras={"left": left, "right": right}, config=FusionConfig(max_pair_dt_ns=5))
        target = np.array([0.0, 0.0, 2.0])

        result = rig.fuse(
            [
                Observation2D("left", "ball", 10, left.project_world(target), 1.0),
                Observation2D("right", "ball", 100, right.project_world(target), 1.0),
            ]
        )

        self.assertEqual(0, len(result.points))
        self.assertIn("ball: no valid camera pair", result.dropped)

    def test_track_cache_expires_stale_points(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        target = np.array([0.0, 0.0, 2.0])
        point = SensorRig(
            cameras={"left": left, "right": right}
        ).fuse(
            [
                Observation2D("left", "ball", 100, left.project_world(target), 1.0),
                Observation2D("right", "ball", 100, right.project_world(target), 1.0),
            ]
        ).points[0]
        cache = TrackCache(ttl_ns=50)

        cache.update(point)
        cache.expire(120)
        self.assertEqual(1, len(cache.points()))
        cache.expire(200)
        self.assertEqual(0, len(cache.points()))

    def test_point_cloud_writes_ply(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        point = SensorRig(cameras={"left": left, "right": right}).fuse(
            [
                Observation2D("left", "ball", 100, left.project_world(np.array([0.0, 0.0, 2.0])), 1.0),
                Observation2D("right", "ball", 100, right.project_world(np.array([0.0, 0.0, 2.0])), 1.0),
            ]
        ).points[0]
        class MemoryPath:
            parent = None

            def __init__(self):
                self.text = ""

            def write_text(self, text, encoding):
                self.text = text

        sink = MemoryPath()
        sink.parent = type("Parent", (), {"mkdir": lambda self, parents, exist_ok: None})()
        PointCloud((point,)).write_ply(sink)
        self.assertIn("element vertex 1", sink.text)
        self.assertIn("end_header", sink.text)

    def test_raw_bgr_reader_emits_timestamped_frames(self):
        stream = io.BytesIO(bytes(range(12)))
        frames = list(read_raw_bgr_frames(stream, "eye", width=2, height=2, frame_bytes=12))
        self.assertEqual(1, len(frames))
        self.assertEqual("eye", frames[0].sensor_id)
        self.assertEqual((2, 2, 3), frames[0].image_bgr.shape)

    def test_render_frame_packet_carries_spout_and_audio_alignment_metadata(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        point = SensorRig(cameras={"left": left, "right": right}).fuse(
            [
                Observation2D("left", "ball", 1_000, left.project_world(np.array([0.0, 0.0, 2.0])), 1.0),
                Observation2D("right", "ball", 2_000, right.project_world(np.array([0.0, 0.0, 2.0])), 0.8),
            ]
        ).points[0]
        config = RenderBridgeConfig(
            spout_sender_name="test-spout",
            visual_delay_ns=100,
            audio_alignment_delay_ns=200,
            default_point_radius_m=0.05,
        )

        frame = lower_points_to_render_frame((point,), config, frame_id=7, created_monotonic_ns=3_000)
        data = frame.to_dict()

        self.assertEqual("localcast.sensor_fusion.render_frame.v1", data["schema"])
        self.assertEqual("test-spout", data["spout_sender_name"])
        self.assertEqual(2_100, data["present_time_ns"])
        self.assertEqual(2_200, data["audio_alignment_time_ns"])
        self.assertEqual(0.05, data["points"][0]["radius_m"])

        loaded = RenderFramePacket.from_dict(data)
        vertices = frame_to_vertex_array(loaded, point_scale=2.0)
        self.assertEqual((1, 8), vertices.shape)
        self.assertAlmostEqual(0.10, float(vertices[0, 7]), places=6)
        self.assertGreater(float(vertices[0, 6]), 0.0)
        brushes = lower_frame_to_screen_brushes(loaded, width=1920, height=1080, point_scale=2.0)
        self.assertEqual(1, len(brushes))
        self.assertGreaterEqual(brushes[0].radii_px[0], brushes[0].radii_px[1])

    def test_spout_output_config_is_importable_without_gpu_context(self):
        config = SpoutOutputConfig(sender_name="unit-test", width=128, height=72, fps=30)
        self.assertEqual("unit-test", config.sender_name)
        self.assertEqual(128, config.width)
        self.assertEqual("kiyo-mid-deru", config.camera_preset)

    def test_kiyo_midpoint_camera_frames_deru_target(self):
        frame = RenderFramePacket(
            schema="localcast.sensor_fusion.render_frame.v1",
            frame_id=1,
            created_monotonic_ns=1,
            source_time_min_ns=1,
            source_time_max_ns=1,
            present_time_ns=1,
            audio_alignment_time_ns=1,
            spout_sender_name="test",
            target_width=320,
            target_height=180,
            points=(
                RenderPointPacket(
                    stable_key="deru-focus",
                    xyz=np.array([0.48, 0.28, 1.24]),
                    radius_m=0.06,
                    color_rgba=(0.2, 1.0, 0.7, 1.0),
                    confidence=1.0,
                    source_timestamp_ns=1,
                ),
            ),
        )
        camera = VirtualCamera(eye=(0.05, -1.035, 1.64), target=(0.48, 0.28, 1.24), fov_degrees=64.0)

        brushes = lower_frame_to_screen_brushes(frame, width=320, height=180, camera=camera)

        self.assertEqual(1, len(brushes))
        self.assertAlmostEqual(160, brushes[0].center_px[0], delta=8)
        self.assertAlmostEqual(90, brushes[0].center_px[1], delta=8)

    def test_render_point_budget_prefers_focus_surface_claims_and_rotates_remainder(self):
        def make_frame(frame_id):
            return RenderFramePacket(
                schema="localcast.sensor_fusion.render_frame.v1",
                frame_id=frame_id,
                created_monotonic_ns=1,
                source_time_min_ns=1,
                source_time_max_ns=1,
                present_time_ns=1,
                audio_alignment_time_ns=1,
                spout_sender_name="test",
                target_width=320,
                target_height=180,
                points=tuple(
                    RenderPointPacket(
                        stable_key=f"room-rgb:deru:{index}",
                        xyz=np.array([0.48 + index * 0.005, 0.28, 1.1 + index * 0.01]),
                        radius_m=0.02,
                        color_rgba=(1.0, 1.0, 1.0, 1.0),
                        confidence=0.55,
                        source_timestamp_ns=1,
                    )
                    for index in range(8)
                )
                + tuple(
                    RenderPointPacket(
                        stable_key=f"stochastic:debug:{index}",
                        xyz=np.array([-1.0 + index * 0.05, -0.8, 1.0]),
                        radius_m=0.04,
                        color_rgba=(1.0, 0.88, 0.32, 1.0),
                        confidence=0.95,
                        source_timestamp_ns=1,
                    )
                    for index in range(8)
                ),
            )

        first = frame_with_point_budget(make_frame(1), 6, "kiyo-mid-deru")
        second = frame_with_point_budget(make_frame(2), 6, "kiyo-mid-deru")

        self.assertEqual(6, len(first.points))
        self.assertEqual(6, len(second.points))
        self.assertGreater(
            sum(point.stable_key.startswith("room-rgb:deru:") for point in first.points),
            sum(point.stable_key.startswith("stochastic:debug:") for point in first.points),
        )
        self.assertNotEqual(
            {point.stable_key for point in first.points},
            {point.stable_key for point in second.points},
        )

    def test_render_point_budget_keeps_all_points_when_under_budget(self):
        frame = RenderFramePacket(
            schema="localcast.sensor_fusion.render_frame.v1",
            frame_id=1,
            created_monotonic_ns=1,
            source_time_min_ns=1,
            source_time_max_ns=1,
            present_time_ns=1,
            audio_alignment_time_ns=1,
            spout_sender_name="test",
            target_width=320,
            target_height=180,
            points=tuple(
                RenderPointPacket(
                    stable_key=f"point-{index}",
                    xyz=np.array([0.0, 0.0, 1.0 + index * 0.01]),
                    radius_m=0.02,
                    color_rgba=(1.0, 1.0, 1.0, 1.0),
                    confidence=index / 10.0,
                    source_timestamp_ns=1,
                )
                for index in range(10)
            ),
        )

        limited = frame_with_point_budget(frame, 20)

        self.assertIs(frame, limited)

    def test_render_point_budget_keeps_cross_modal_calibration_constraints(self):
        pinned = (
            RenderPointPacket("camera-chirp:kiyo-primary:local-speaker-left:body", np.array([0.0, 0.0, 1.0]), 0.02, (1, 1, 0, 1), 0.15, 1),
            RenderPointPacket("camera-sync:kiyo-primary", np.array([0.0, 0.0, 1.0]), 0.02, (0, 1, 1, 1), 0.15, 1),
            RenderPointPacket("clap-calibration:clap:test", np.array([0.0, 0.0, 1.0]), 0.02, (0, 1, 1, 1), 0.15, 1),
        )
        noisy = tuple(
            RenderPointPacket(f"room-rgb:noisy:{index}", np.array([0.48, 0.28, 1.2]), 0.02, (1, 1, 1, 1), 1.0, 1)
            for index in range(20)
        )
        frame = RenderFramePacket(
            schema="localcast.sensor_fusion.render_frame.v1",
            frame_id=3,
            created_monotonic_ns=1,
            source_time_min_ns=1,
            source_time_max_ns=1,
            present_time_ns=1,
            audio_alignment_time_ns=1,
            spout_sender_name="test",
            target_width=320,
            target_height=180,
            points=pinned + noisy,
        )

        limited = frame_with_point_budget(frame, 6, "kiyo-mid-deru")
        keys = {point.stable_key for point in limited.points}

        self.assertEqual(6, len(limited.points))
        self.assertTrue(all(point.stable_key in keys for point in pinned))
        self.assertEqual(1, point_prefix_counts(limited.points)["camera-chirp"])
        self.assertEqual(1, point_prefix_counts(limited.points)["camera-sync"])
        self.assertEqual(1, point_prefix_counts(limited.points)["clap-calibration"])

    def test_point_prefix_counts_groups_audio_events(self):
        points = (
            RenderPointPacket("audio-event-16", np.zeros(3), 0.02, (1, 1, 1, 1), 1.0, 1),
            RenderPointPacket("audio-event-17", np.zeros(3), 0.02, (1, 1, 1, 1), 1.0, 1),
            RenderPointPacket("camera-chirp:kiyo:speaker:body", np.zeros(3), 0.02, (1, 1, 1, 1), 1.0, 1),
        )

        self.assertEqual({"audio-event": 2, "camera-chirp": 1}, point_prefix_counts(points))

    def test_rasterizer_uses_gaussian_envelope_not_solid_ellipse(self):
        frame = RenderFramePacket(
            schema="localcast.sensor_fusion.render_frame.v1",
            frame_id=1,
            created_monotonic_ns=1,
            source_time_min_ns=1,
            source_time_max_ns=1,
            present_time_ns=1,
            audio_alignment_time_ns=1,
            spout_sender_name="test",
            target_width=320,
            target_height=180,
            points=(
                RenderPointPacket(
                    stable_key="rgb-splat",
                    xyz=np.array([0.0, 0.0, 1.2]),
                    radius_m=0.08,
                    color_rgba=(1.0, 0.0, 0.0, 1.0),
                    confidence=1.0,
                    source_timestamp_ns=1,
                ),
            ),
        )

        image = rasterize_frame_rgba(frame, 320, 180, point_scale=1.0)
        red = image[:, :, 0]
        hot = red[red > 24]

        self.assertGreater(len(hot), 20)
        self.assertGreater(int(hot.max()), 180)
        self.assertGreater(len(np.unique(hot)), 8)
        self.assertTrue(np.any((hot > 40) & (hot < 160)))

    def test_render_frame_round_trips_through_typed_cultcache_doc(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        point = SensorRig(cameras={"left": left, "right": right}).fuse(
            [
                Observation2D("left", "ball", 1_000, left.project_world(np.array([0.0, 0.0, 2.0])), 1.0),
                Observation2D("right", "ball", 1_000, right.project_world(np.array([0.0, 0.0, 2.0])), 1.0),
            ]
        ).points[0]
        frame = lower_points_to_render_frame((point,), RenderBridgeConfig(), frame_id=3, created_monotonic_ns=10)

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "visual-state.msgpack"
            put_live_render_frame(path, frame)
            loaded = get_live_render_frame(path)

        self.assertIsNotNone(loaded)
        self.assertEqual(3, loaded.frame_id)
        self.assertEqual(1, len(loaded.points))
        self.assertEqual("ball", loaded.points[0].stable_key)

    def test_clap_detector_promotes_audio_transient_with_camera_motion(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        rig = SensorRig(
            cameras={"left": left, "right": right},
            config=FusionConfig(max_pair_dt_ns=40_000_000, max_reprojection_error_px=2.0),
        )
        target = np.array([0.02, 0.01, 2.0])
        sample_rate = 48_000
        start_ns = 1_000_000_000
        clap_sample = 960
        oracle_ns = start_ns + round(clap_sample * 1_000_000_000 / sample_rate)
        field = np.zeros((4096, 4), dtype=np.float32)
        field[clap_sample] = np.array([0.85, -0.75, 0.6, -0.55], dtype=np.float32)

        frames = {}
        for cam in (left, right):
            uv = cam.project_world(target)
            previous = np.zeros((cam.height, cam.width), dtype=np.float32)
            current = previous.copy()
            x, y = int(round(uv[0])), int(round(uv[1]))
            current[y - 2 : y + 3, x - 2 : x + 3] = 1.0
            frames[cam.sensor_id] = (
                TimestampedFrame(cam.sensor_id, oracle_ns - 8_000_000, previous),
                TimestampedFrame(cam.sensor_id, oracle_ns + 8_000_000, current),
            )

        events = detect_clap_events(
            field,
            sample_rate,
            frames,
            audio_time_ns=start_ns,
            rig=rig,
            config=ClapDetectorConfig(
                audio_window_samples=128,
                audio_hop_samples=32,
                audio_onset_ratio=3.0,
                min_audio_energy=1.0e-6,
                min_camera_motion_score=0.2,
            ),
        )

        self.assertEqual(1, len(events))
        event = events[0]
        np.testing.assert_allclose(np.asarray(event.position_m), target, atol=0.04)
        self.assertLessEqual(abs(oracle_ns - event.acoustic_oracle_ns), 3_000_000)
        self.assertGreater(event.acoustic_confidence, 0.5)
        self.assertGreater(event.visual_confidence, 0.5)

    def test_clap_events_round_trip_through_typed_cultcache_doc(self):
        left = camera("left", -0.25)
        sample_rate = 48_000
        start_ns = 2_000_000_000
        clap_sample = 960
        oracle_ns = start_ns + round(clap_sample * 1_000_000_000 / sample_rate)
        field = np.zeros((2048, 1), dtype=np.float32)
        field[clap_sample] = 1.0
        previous = np.zeros((left.height, left.width), dtype=np.float32)
        current = previous.copy()
        current[100:108, 120:128] = 1.0
        events = detect_clap_events(
            field,
            sample_rate,
            {
                "left": (
                    TimestampedFrame("left", oracle_ns - 5_000_000, previous),
                    TimestampedFrame("left", oracle_ns + 5_000_000, current),
                )
            },
            audio_time_ns=start_ns,
            rig=None,
            config=ClapDetectorConfig(
                audio_window_samples=128,
                audio_hop_samples=32,
                audio_onset_ratio=3.0,
                min_audio_energy=1.0e-6,
                min_camera_count=1,
                min_camera_motion_score=0.2,
            ),
        )
        frame = make_clap_events_frame(frame_id=12, events=events)

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "visual-state.msgpack"
            put_live_clap_events(path, frame)
            loaded = get_live_clap_events(path)

        self.assertIsNotNone(loaded)
        self.assertEqual(12, loaded.frame_id)
        self.assertEqual(1, len(loaded.events))
        self.assertEqual(events[0].acoustic_oracle_ns, loaded.events[0].acoustic_oracle_ns)

    def test_live_clap_calibrator_uses_audio_and_kiyo_motion(self):
        width, height = 320, 240
        target = np.array([0.0, -0.5, 2.0])
        sample_rate = 48_000
        audio_time_ns = 4_000_000_000
        clap_sample = 960
        clap_time_ns = audio_time_ns + round(clap_sample * 1_000_000_000 / sample_rate)
        block = np.zeros((2048, 4), dtype=np.float32)
        block[clap_sample] = np.array([1.0, -0.8, 0.6, -0.4], dtype=np.float32)

        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            audio_cache = tmp_path / "audio-state.msgpack"
            clap_cache = tmp_path / "clap-events.msgpack"
            put_live_spatial_audio_frame(
                audio_cache,
                make_spatial_audio_frame(
                    block,
                    frame_id=1,
                    sample_rate=sample_rate,
                    start_sample=0,
                    audio_time_ns=audio_time_ns,
                ),
            )
            calibrator = LiveClapCalibrator(
                audio_cache=audio_cache,
                clap_cache=clap_cache,
                camera_width=width,
                camera_height=height,
            )
            frames = {}
            for sensor_id, x in (("kiyo-primary", -0.18), ("kiyo-secondary", 0.18)):
                cam = rgb_dense_camera(sensor_id, x, width, height)
                uv = cam.project_world(target)
                previous = np.zeros((height, width, 3), dtype=np.uint8)
                current = previous.copy()
                col, row = int(round(uv[0])), int(round(uv[1]))
                current[row - 3 : row + 4, col - 3 : col + 4, :] = 255
                calibrator.observe_frames(
                    {
                        sensor_id: TimestampedFrame(sensor_id, clap_time_ns - 8_000_000, previous),
                    }
                )
                frames[sensor_id] = TimestampedFrame(sensor_id, clap_time_ns + 8_000_000, current)
            calibrator.observe_frames(frames)

            points = calibrator.update(frame_id=12)

            self.assertTrue(clap_cache.exists())
            self.assertTrue(any(point.stable_key.startswith("clap-calibration:") for point in points))
            estimates = {estimate.sensor_id: estimate for estimate in calibrator.sync_model.estimates()}
            self.assertIn("kiyo-primary", estimates)
            self.assertIn("kiyo-secondary", estimates)
            restored = LiveClapCalibrator(
                audio_cache=audio_cache,
                clap_cache=clap_cache,
                camera_width=width,
                camera_height=height,
            )
            restored_estimates = {estimate.sensor_id: estimate for estimate in restored.sync_model.estimates()}
            self.assertIn("kiyo-primary", restored_estimates)
            self.assertIn("kiyo-secondary", restored_estimates)

    def test_camera_sync_model_selects_history_frame_at_oracle_time(self):
        event = type(
            "Event",
            (),
            {
                "stable_key": "clap:test",
                "acoustic_oracle_ns": 1_000,
                "acoustic_confidence": 1.0,
                "camera_peaks": (
                    type("Peak", (), {"sensor_id": "cam-a", "timestamp_ns": 900, "score": 0.8})(),
                ),
            },
        )()
        sync = CameraClockSyncModel(smoothing=1.0)
        sync.observe_event(event)
        history = TimestampedFrameHistory(max_frames=4)
        for timestamp, value in ((880, 0.0), (900, 1.0), (940, 2.0)):
            history.add(TimestampedFrame("cam-a", timestamp, np.full((2, 2), value, dtype=np.float32)))

        selected = history.nearest("cam-a", sync.raw_time_for_oracle("cam-a", 1_000))

        self.assertIsNotNone(selected)
        self.assertEqual(900, selected.timestamp_ns)
        self.assertEqual(100, sync.offset_ns("cam-a"))

    def test_timestamp_history_expires_from_shared_five_second_edge(self):
        history = TimestampedFrameHistory(max_frames=20, max_age_ns=DEFAULT_RESERVOIR_NS)
        history.add(TimestampedFrame("cam-a", 1_000_000_000, np.zeros((1, 1), dtype=np.float32)))
        history.add(TimestampedFrame("cam-b", 4_000_000_000, np.ones((1, 1), dtype=np.float32)))
        history.add(TimestampedFrame("cam-a", 7_100_000_000, np.full((1, 1), 2.0, dtype=np.float32)))

        frames = history.frames_by_sensor()

        self.assertNotIn(1_000_000_000, [frame.timestamp_ns for frame in frames["cam-a"]])
        self.assertEqual(7_100_000_000, history.latest_timestamp_ns())
        self.assertIsNotNone(history.nearest("cam-b", 4_000_000_000))

    def test_lod_evidence_is_clipped_to_visual_reservoir(self):
        evidence = (
            LODEvidencePoint("old", np.zeros(3), 1.0, 9, 1.0, "rgb"),
            LODEvidencePoint("fresh", np.ones(3), 1.0, DEFAULT_RESERVOIR_NS + 10, 1.0, "rgb"),
        )

        kept = evidence_in_reservoir(
            evidence,
            latest_timestamp_ns=DEFAULT_RESERVOIR_NS + 10,
            reservoir_ns=DEFAULT_RESERVOIR_NS,
        )

        self.assertEqual(("fresh",), tuple(item.stable_key for item in kept))

    def test_render_points_are_clipped_to_shared_reservoir_edge(self):
        points = (
            RenderPointPacket("old", np.zeros(3), 0.01, (1.0, 1.0, 1.0, 1.0), 1.0, 9),
            RenderPointPacket("fresh", np.ones(3), 0.01, (1.0, 1.0, 1.0, 1.0), 1.0, DEFAULT_RESERVOIR_NS + 10),
        )

        kept = render_points_in_reservoir(
            points,
            latest_timestamp_ns=DEFAULT_RESERVOIR_NS + 10,
            reservoir_ns=DEFAULT_RESERVOIR_NS,
        )

        self.assertEqual(("fresh",), tuple(point.stable_key for point in kept))

    def test_spout_overlay_clamps_stale_visual_frame_to_audio_reservoir(self):
        frame = RenderFramePacket(
            schema="localcast.sensor_fusion.render_frame.v1",
            frame_id=4,
            created_monotonic_ns=1,
            source_time_min_ns=0,
            source_time_max_ns=100,
            present_time_ns=100,
            audio_alignment_time_ns=100,
            spout_sender_name="test",
            target_width=32,
            target_height=32,
            points=(
                RenderPointPacket("old", np.zeros(3), 0.01, (1.0, 1.0, 1.0, 1.0), 1.0, 9),
                RenderPointPacket("fresh", np.ones(3), 0.01, (1.0, 1.0, 1.0, 1.0), 1.0, DEFAULT_RESERVOIR_NS + 10),
            ),
        )
        audio = make_spatial_audio_frame(
            np.zeros((8, 4), dtype=np.float32),
            frame_id=1,
            sample_rate=48000,
            start_sample=0,
            audio_time_ns=DEFAULT_RESERVOIR_NS + 10,
        )

        clamped = clamp_frame_to_audio_reservoir(frame, audio)

        self.assertEqual(DEFAULT_RESERVOIR_NS + 10, clamped.audio_alignment_time_ns)
        self.assertEqual(5_000_000_000, clamped.source_time_max_ns - clamped.source_time_min_ns)
        self.assertEqual(("fresh",), tuple(point.stable_key for point in clamped.points))

    def test_latest_frame_pump_exposes_newest_frame_without_blocking_reader(self):
        class FakeSource:
            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, tb):
                return None

            def frames(self):
                yield FramePacket("cam", 10, 0, np.zeros((1, 1, 3), dtype=np.uint8))
                yield FramePacket("cam", 20, 1, np.ones((1, 1, 3), dtype=np.uint8))

        pump = LatestFramePump(lambda: FakeSource())
        pump.start()
        deadline = time.monotonic() + 1.0
        latest = None
        while time.monotonic() < deadline:
            latest = pump.latest()
            if latest is not None and latest.sequence == 1:
                break
            time.sleep(0.01)

        self.assertIsNotNone(latest)
        self.assertEqual(1, latest.sequence)
        self.assertEqual(20, latest.timestamp_ns)

    def test_camera_motion_peaks_skip_mismatched_frame_shapes(self):
        frames = {
            "leap": [
                TimestampedFrame("leap", 1_000, np.zeros((12, 16), dtype=np.float32)),
                TimestampedFrame("leap", 2_000, np.zeros((24, 16), dtype=np.float32)),
            ]
        }

        peaks = detect_camera_motion_peaks(frames, 1_500)

        self.assertEqual([], peaks)

    def test_chirp_pose_constraints_map_camera_mics_to_range_residuals(self):
        constraints = constraints_from_phase_sources(
            (
                {"sourceId": "kiyo-0", "distanceDeltaMeters": 0.12, "confidence": 0.8},
                {"sourceId": "host-focusrite", "distanceDeltaMeters": 0.40, "confidence": 1.0},
            ),
            audio_time_ns=12_345,
            camera_mics=(
                CameraMicGeometry("kiyo-primary", "kiyo-0", np.array([1.0, 0.0, 0.0], dtype=np.float64)),
            ),
            speakers=(
                SpeakerGeometry("speaker-left", np.array([0.0, 0.0, 0.0], dtype=np.float64)),
            ),
        )

        self.assertEqual(1, len(constraints))
        constraint = constraints[0]
        self.assertEqual("kiyo-primary", constraint.sensor_id)
        self.assertEqual("kiyo-0", constraint.source_id)
        self.assertEqual("speaker-left", constraint.speaker_id)
        self.assertAlmostEqual(1.0, constraint.nominal_range_m)
        self.assertAlmostEqual(1.12, constraint.observed_range_m)
        self.assertAlmostEqual(0.12, constraint.range_residual_m)
        self.assertEqual(12_345, constraint.timestamp_ns)

    def test_chirp_pose_constraints_produce_pose_correction_estimates(self):
        constraints = constraints_from_phase_sources(
            (
                {"sourceId": "kiyo-0", "distanceDeltaMeters": 0.20, "confidence": 0.8},
            ),
            audio_time_ns=9_000,
            camera_mics=(
                CameraMicGeometry("kiyo-primary", "kiyo-0", np.array([1.0, 0.0, 0.0], dtype=np.float64)),
            ),
            speakers=(
                SpeakerGeometry("speaker-left", np.array([0.0, 0.0, 0.0], dtype=np.float64)),
            ),
        )

        estimates = estimate_pose_corrections(constraints)

        self.assertEqual(1, len(estimates))
        self.assertEqual("kiyo-primary", estimates[0].sensor_id)
        self.assertTrue(np.allclose([0.20, 0.0, 0.0], estimates[0].correction_m))
        self.assertEqual(1, estimates[0].constraint_count)

    def test_chirp_pose_geometry_loads_from_audio_profile(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "audio-field.json"
            path.write_text(
                """
{
  "microphones": [
    {"id": "mic_kiyo_left", "positionMeters": [-0.4, 0.2, 1.3]},
    {"id": "ignored_focusrite", "positionMeters": [0.0, 0.0, 1.0]},
    {"id": "custom", "phaseSourceId": "ps-eye-1", "cameraSensorId": "ps3eye-right", "positionMeters": [0.7, -0.1, 1.2]}
  ],
  "speakers": [
    {"id": "speaker_left", "positionMeters": [-0.6, 1.0, 1.1]}
  ]
}
""",
                encoding="utf-8",
            )

            mics = camera_mic_geometry_from_audio_profile(path)
            speakers = speaker_geometry_from_audio_profile(path)

        self.assertEqual(("kiyo-0", "ps-eye-1"), tuple(mic.source_id for mic in mics))
        self.assertEqual(("kiyo-primary", "ps3eye-right"), tuple(mic.sensor_id for mic in mics))
        self.assertEqual("speaker_left", speakers[0].speaker_id)
        self.assertEqual("kiyo-0", phase_source_id_for_microphone({"id": "mic_kiyo_left"}))
        self.assertEqual("kiyo-primary", camera_sensor_id_for_microphone({"id": "mic_kiyo_left"}))

    def test_audio_source_events_overlay_against_audio_alignment_time(self):
        frame = RenderFramePacket(
            schema="localcast.sensor_fusion.render_frame.v1",
            frame_id=9,
            created_monotonic_ns=1_000,
            source_time_min_ns=1_000,
            source_time_max_ns=1_000,
            present_time_ns=1_100,
            audio_alignment_time_ns=1_000_000_000,
            spout_sender_name="test",
            target_width=128,
            target_height=72,
            points=(),
        )
        audio = make_spatial_audio_frame(
            np.zeros((1024, 4), dtype=np.float32),
            frame_id=4,
            sample_rate=48000,
            start_sample=48000,
            audio_time_ns=1_000_000_000,
        )
        events = make_audio_source_events(
            frame_id=5,
            sample_rate=48000,
            start_sample=0,
            frame_count=96000,
            audio_time_ns=0,
            events=[
                {"eventId": 1, "startSample": 48000, "positionMeters": [0.2, 0.3, 1.4], "confidence": 0.9, "energy": 0.001},
                {"eventId": 2, "startSample": 70000, "positionMeters": [2.0, 0.0, 1.0], "confidence": 0.9, "energy": 0.001},
            ],
            voice_focus=[],
        )

        augmented, status = overlay_audio_events(frame, events, audio, window_ns=40_000_000)

        self.assertEqual(1, len(augmented.points))
        self.assertEqual("audio-event-1", augmented.points[0].stable_key)
        self.assertTrue(status.synchronized)
        self.assertEqual(1, status.overlay_event_count)

    def test_remote_video_artifact_reports_sync_delta(self):
        artifact = remote_video_artifact_for_present_time(
            source_name="Neighbor PC - Video",
            url="srt://0.0.0.0:5100?mode=listener&latency=120000",
            present_time_ns=1_250_000_000,
            observed_time_ns=1_100_000_000,
            expected_latency_ns=120_000_000,
            tolerance_ns=40_000_000,
        )

        self.assertEqual(30_000_000, artifact.delta_ns)
        self.assertTrue(artifact.synchronized)

    def test_rgb_room_splats_emit_camera_resolved_background(self):
        frame = np.zeros((48, 64, 3), dtype=np.uint8)
        frame[:, :, :] = np.array([48, 72, 96], dtype=np.uint8)
        frame[16:34, 24:40, :] = np.array([240, 240, 240], dtype=np.uint8)

        points = rgb_room_splats(
            "room-rgb:test",
            frame,
            np.array([0.0, -1.0, 1.5]),
            123,
            step=12,
        )

        self.assertGreater(len(points), 0)
        self.assertTrue(all(point.stable_key.startswith("room-rgb:test") for point in points))
        self.assertTrue(any(point.xyz[2] < 0.2 for point in points))
        self.assertTrue(any(point.xyz[1] > 0.7 for point in points))

    def test_leap_packed_map_channels_are_mapped_individually(self):
        packed = np.zeros((12, 12, 3), dtype=np.uint8)
        packed[:, :, 0] = 20
        packed[:, :, 1] = 80
        packed[:, :, 2] = 160

        channels = unpack_leap_packed_channels(packed)

        self.assertAlmostEqual(80 / 255.0, float(channels["green"][0, 0]), places=6)
        self.assertAlmostEqual(160 / 255.0, float(channels["magenta"][0, 0]), places=6)
        self.assertAlmostEqual(20 / 255.0, float(channels["blue"][0, 0]), places=6)

    def test_leap_motion_points_use_channel_identity(self):
        previous = np.zeros((24, 24), dtype=np.float32)
        current = previous.copy()
        current[12, 12] = 1.0

        points = leap_channel_motion_points("green", current, previous, 456, step=12)

        self.assertGreater(len(points), 0)
        self.assertTrue(all(point.stable_key.startswith("leap-motion:green") for point in points))
        self.assertTrue(all(point.source_timestamp_ns == 456 for point in points))

    def test_active_illumination_pulse_plan_emits_timed_brightness_changes(self):
        class FakeLight:
            def __init__(self):
                self.calls = []

            def set_white(self, *, brightness_percent, color_temp_percent):
                self.calls.append((brightness_percent, color_temp_percent))

        ticks = iter([1_000_000_000, 1_300_000_000])
        light = FakeLight()
        controller = ActiveIlluminationController(
            light,
            IlluminationPulsePlan(
                pulse_count=2,
                bright_ms=10,
                settle_ms=20,
                brightness_percent=91,
                idle_brightness_percent=17,
                color_temp_percent=44,
            ),
        )

        pulses = controller.run(sleep=lambda _: None, clock_ns=lambda: next(ticks))

        self.assertEqual(2, len(pulses))
        self.assertEqual((91, 44), light.calls[0])
        self.assertEqual((17, 44), light.calls[1])
        self.assertEqual((17, 44), light.calls[-1])
        self.assertEqual(1_010_000_000, pulses[0].bright_until_monotonic_ns)

    def test_adaptive_camera_controller_reduces_overexposure(self):
        controller = AdaptiveCameraController(target=CameraQualityTarget(mean_luma=0.5, luma_deadband=0.02))

        command = controller.update(
            quality=type(
                "Quality",
                (),
                {
                    "mean_luma": 0.82,
                    "dark_clip": 0.0,
                    "bright_clip": 0.12,
                    "contrast": 0.2,
                    "sharpness": 0.2,
                },
            )()
        )

        self.assertTrue(command.changed)
        self.assertIsNotNone(command.exposure)
        self.assertLess(command.exposure, 0.5)
        self.assertIsNotNone(command.gain)
        self.assertLess(command.gain, 0.25)

    def test_dense_stereo_emits_calibrated_rgb_surface_claims(self):
        height, width = 48, 72
        rng = np.random.default_rng(42)
        left_frame = rng.integers(0, 255, size=(height, width, 3), dtype=np.uint8)
        right_frame = np.roll(left_frame, -8, axis=1)
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        left = CameraModel("left", left.camera_matrix, left.dist_coeffs, left.world_from_sensor, width, height)
        right = CameraModel("right", right.camera_matrix, right.dist_coeffs, right.world_from_sensor, width, height)

        points = dense_stereo_points(
            prefix="dense-test",
            left_frame_bgr=left_frame,
            right_frame_bgr=right_frame,
            left_camera=left,
            right_camera=right,
            timestamp_ns=789,
            config=DenseStereoConfig(
                sample_step=8,
                block_radius=2,
                max_disparity_px=16,
                min_ncc=0.55,
                max_reprojection_error_px=200.0,
            ),
        )

        self.assertGreater(len(points), 0)
        self.assertTrue(all(point.stable_key.startswith("dense-test") for point in points))
        self.assertTrue(all(point.source_timestamp_ns == 789 for point in points))
        self.assertTrue(any(point.radius_m < 0.01 for point in points))

    def test_live_rgb_sampler_dense_path_uses_camera_pair_before_fallback_surfaces(self):
        height, width = 48, 72
        rng = np.random.default_rng(7)
        primary = rng.integers(0, 255, size=(height, width, 3), dtype=np.uint8)
        secondary = np.roll(primary, -7, axis=1)

        points = rgb_dense_stereo_splats(primary, secondary, 321, step=8)

        self.assertGreater(len(points), 0)
        self.assertTrue(all(point.stable_key.startswith("dense-rgb") for point in points))

    def test_fixed_board_calibration_solves_camera_into_common_space(self):
        board = BoardSpec(squares_x=5, squares_y=4, square_length_m=0.04)
        intrinsics = CameraIntrinsics(
            sensor_id="cam-a",
            camera_matrix=np.array([[420.0, 0.0, 160.0], [0.0, 420.0, 120.0], [0.0, 0.0, 1.0]]),
            dist_coeffs=np.zeros(5),
            width=320,
            height=240,
        )
        expected = CameraModel(
            sensor_id="cam-a",
            camera_matrix=intrinsics.camera_matrix,
            dist_coeffs=intrinsics.dist_coeffs,
            world_from_sensor=np.array(
                [
                    [1.0, 0.0, 0.0, -0.12],
                    [0.0, 1.0, 0.0, 0.03],
                    [0.0, 0.0, 1.0, -0.80],
                    [0.0, 0.0, 0.0, 1.0],
                ]
            ),
            width=320,
            height=240,
        )
        ids = np.array([0, 1, 2, 4, 5, 6, 8, 9, 10], dtype=np.int32)
        image_points = np.vstack([expected.project_world(board.object_point(int(corner_id))) for corner_id in ids])

        cameras, solves = solve_common_space_from_fixed_board(
            [intrinsics],
            board,
            [BoardObservation("cam-a", 100, ids, image_points)],
            max_reprojection_error_px=0.05,
        )

        self.assertEqual(1, len(solves))
        np.testing.assert_allclose(expected.world_from_sensor, cameras["cam-a"].world_from_sensor, atol=1e-5)
        self.assertLess(solves[0].reprojection_error_px, 1e-4)

    def test_surface_features_match_and_triangulate_between_calibrated_views(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        target = np.array([0.04, 0.02, 2.0])
        left_obs = SurfaceFeatureObservation(
            sensor_id="left",
            feature_id="a",
            timestamp_ns=1_000,
            uv=left.project_world(target),
            descriptor=np.array([1, 2, 3, 4], dtype=np.uint8),
            confidence=0.95,
        )
        right_obs = SurfaceFeatureObservation(
            sensor_id="right",
            feature_id="b",
            timestamp_ns=1_200,
            uv=right.project_world(target),
            descriptor=np.array([1, 2, 3, 4], dtype=np.uint8),
            confidence=0.90,
        )

        tracks = match_surface_features([left_obs], [right_obs], max_descriptor_distance=0.0, max_dt_ns=1_000)
        points = triangulate_surface_tracks(tracks, {"left": left, "right": right}, max_reprojection_error_px=0.01)

        self.assertEqual(1, len(tracks))
        self.assertEqual(1, len(points))
        np.testing.assert_allclose(target, points[0].xyz, atol=1e-9)
        self.assertEqual(("left", "right"), points[0].sensors)


if __name__ == "__main__":
    unittest.main()
    match_surface_features,
