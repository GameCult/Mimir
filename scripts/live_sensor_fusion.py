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
    FusionConfig,
    Observation2D,
    RenderBridgeConfig,
    RenderFramePacket,
    RenderPointPacket,
    SensorRig,
    put_live_render_frame,
    lower_points_to_render_frame,
)


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
    lock = acquire_runtime_lock(cache_path.with_suffix(".producer.lock"))
    if lock is None:
        return
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
        timestamp_ns = frame.source_time_max_ns
        frame = RenderFramePacket(
            schema=frame.schema,
            frame_id=frame.frame_id,
            created_monotonic_ns=frame.created_monotonic_ns,
            source_time_min_ns=frame.source_time_min_ns,
            source_time_max_ns=frame.source_time_max_ns,
            present_time_ns=frame.present_time_ns,
            audio_alignment_time_ns=frame.audio_alignment_time_ns,
            spout_sender_name=frame.spout_sender_name,
            target_width=frame.target_width,
            target_height=frame.target_height,
            points=tuple(frame.points) + reconstruction_context_points(now, timestamp_ns),
        )
        put_live_render_frame(cache_path, frame)
        frame_id += 1
        sleep_for = interval - (time.monotonic() - now)
        if sleep_for > 0:
            time.sleep(sleep_for)


if __name__ == "__main__":
    main()
