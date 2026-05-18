import argparse
from pathlib import Path
import sys
import time

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.sensor_fusion import (
    CameraModel,
    FusionConfig,
    Observation2D,
    RenderBridgeConfig,
    SensorRig,
    put_live_render_frame,
    lower_points_to_render_frame,
)


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
    for index in range(count):
        u = index / max(1, count)
        theta = index * 2.399963229728653 + now_s * 0.65
        radius = 0.12 + 1.05 * np.sqrt(u)
        point = np.array(
            [
                radius * np.cos(theta),
                radius * np.sin(theta),
                1.25 + 0.45 * np.sin(index * 0.041 + now_s),
            ],
            dtype=np.float64,
        )
        marker = f"synthetic-fused-{index}"
        observations.append(Observation2D("ps3eye_left", marker, timestamp, left.project_world(point), 0.95))
        observations.append(Observation2D("ps3eye_right", marker, timestamp, right.project_world(point), 0.92))
    return observations


def main() -> None:
    parser = argparse.ArgumentParser(description="Write live sensor-fusion render frames into typed CultCache state.")
    parser.add_argument("--cache", default=str(ROOT / "calibration" / "runs" / "visual-state.msgpack"))
    parser.add_argument("--fps", type=float, default=30.0)
    parser.add_argument("--points", type=int, default=256)
    parser.add_argument("--duration", type=float)
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
    while True:
        now = time.monotonic()
        if args.duration is not None and now - start >= args.duration:
            break
        observations = synthetic_observations(rig, now, args.points)
        result = rig.fuse(observations)
        frame = lower_points_to_render_frame(
            result.points,
            render_config,
            frame_id=frame_id,
            created_monotonic_ns=time.monotonic_ns(),
        )
        put_live_render_frame(cache_path, frame)
        frame_id += 1
        sleep_for = interval - (time.monotonic() - now)
        if sleep_for > 0:
            time.sleep(sleep_for)


if __name__ == "__main__":
    main()
