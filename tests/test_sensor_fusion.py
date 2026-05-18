import io
from pathlib import Path
import tempfile
import unittest

import numpy as np

from localcast.sensor_fusion import (
    CameraModel,
    FusionConfig,
    Observation2D,
    PointCloud,
    RenderBridgeConfig,
    RenderFramePacket,
    RenderPointPacket,
    SensorRig,
    SpoutOutputConfig,
    TrackCache,
    frame_to_vertex_array,
    get_live_render_frame,
    lower_frame_to_screen_brushes,
    lower_points_to_render_frame,
    overlay_audio_events,
    put_live_render_frame,
    remote_video_artifact_for_present_time,
)
from localcast.sensor_fusion.spout_output import rasterize_frame_rgba
from scripts.live_sensor_fusion import (
    leap_channel_motion_points,
    rgb_room_splats,
    unpack_leap_packed_channels,
)
from audio_field.cultcache_audio import make_audio_source_events, make_spatial_audio_frame
from localcast.sensor_fusion.adapters import read_raw_bgr_frames


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


if __name__ == "__main__":
    unittest.main()
