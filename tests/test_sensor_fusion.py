import io
import unittest

import numpy as np

from localcast.sensor_fusion import (
    CameraModel,
    FusionConfig,
    Observation2D,
    PointCloud,
    RenderBridgeConfig,
    SensorRig,
    TrackCache,
    lower_points_to_render_frame,
)
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


if __name__ == "__main__":
    unittest.main()
