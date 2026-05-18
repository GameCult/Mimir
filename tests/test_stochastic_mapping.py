import tempfile
import unittest
from pathlib import Path

import numpy as np

from localcast.sensor_fusion.core import CameraModel, SensorRig, Observation2D
from localcast.sensor_fusion.stochastic_mapping import (
    ImageRayBiasMap,
    RayBiasSample,
    multilod_cache_from_points,
    pixel_scene_ray,
    stochastic_transient_matches,
)
from localcast.sensor_fusion.surface_features import SurfaceFeatureObservation


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


class StochasticMappingTests(unittest.TestCase):
    def test_pixel_scene_ray_points_through_projected_target(self):
        cam = camera("left", -0.25)
        target = np.array([0.0, 0.0, 2.0])
        ray = pixel_scene_ray(cam, cam.project_world(target), 100, 0.9)
        to_target = target - ray.origin
        to_target /= np.linalg.norm(to_target)

        self.assertGreater(float(np.dot(ray.direction, to_target)), 0.999)

    def test_stochastic_transient_matches_triangulate_descriptor_consistent_features(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        target = np.array([0.04, 0.02, 2.0])
        descriptor = np.array([1, 2, 3, 4], dtype=np.uint8)
        observations = [
            SurfaceFeatureObservation("left", "a", 1000, left.project_world(target), descriptor, confidence=0.95),
            SurfaceFeatureObservation("right", "b", 1200, right.project_world(target), descriptor.copy(), confidence=0.9),
        ]

        matches = stochastic_transient_matches(
            observations,
            {"left": left, "right": right},
            max_descriptor_distance=0.0,
            max_reprojection_error_px=0.01,
            seed=4,
        )

        self.assertEqual(1, len(matches))
        np.testing.assert_allclose(target, matches[0].xyz, atol=1e-9)
        self.assertGreater(matches[0].confidence, 0.7)

    def test_bias_map_applies_cell_correction(self):
        bias = ImageRayBiasMap(cell_size_px=32, smoothing=1.0)
        bias.observe(RayBiasSample("cam", np.array([45.0, 50.0]), np.array([1.5, -2.0]), 1.0))

        corrected = bias.correct_uv("cam", np.array([40.0, 42.0]))

        np.testing.assert_allclose(corrected, np.array([41.5, 40.0]))

    def test_multilod_cache_groups_points_for_compute_reconciliation(self):
        left = camera("left", -0.25)
        right = camera("right", 0.25)
        rig = SensorRig(cameras={"left": left, "right": right})
        points = rig.fuse(
            [
                Observation2D("left", "a", 100, left.project_world(np.array([0.0, 0.0, 2.0])), 1.0),
                Observation2D("right", "a", 100, right.project_world(np.array([0.0, 0.0, 2.0])), 1.0),
                Observation2D("left", "b", 110, left.project_world(np.array([0.03, 0.0, 2.0])), 1.0),
                Observation2D("right", "b", 110, right.project_world(np.array([0.03, 0.0, 2.0])), 1.0),
            ]
        ).points

        cache = multilod_cache_from_points(points, levels=(0.02, 0.10), created_monotonic_ns=999)

        self.assertEqual("localcast.sensor_fusion.multilod_scene_cache.v1", cache.schema)
        self.assertTrue(any(cell.level == 1 and cell.count == 2 for cell in cache.cells))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "lod.json"
            cache.write_json(path)
            self.assertIn("multilod_scene_cache", path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
