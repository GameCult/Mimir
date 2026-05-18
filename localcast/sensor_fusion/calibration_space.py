from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable

import numpy as np

from .core import CameraModel


@dataclass(frozen=True)
class CameraIntrinsics:
    sensor_id: str
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray
    width: int
    height: int
    role: str = "tracking"
    latency_ms: float = 0.0


@dataclass(frozen=True)
class BoardSpec:
    squares_x: int
    squares_y: int
    square_length_m: float

    def object_point(self, corner_id: int) -> np.ndarray:
        cols = self.squares_x - 1
        if corner_id < 0 or corner_id >= cols * (self.squares_y - 1):
            raise ValueError(f"ChArUco corner id {corner_id} outside board")
        row = corner_id // cols
        col = corner_id % cols
        return np.array(
            [(col + 1) * self.square_length_m, (row + 1) * self.square_length_m, 0.0],
            dtype=np.float64,
        )


@dataclass(frozen=True)
class BoardObservation:
    sensor_id: str
    timestamp_ns: int
    corner_ids: np.ndarray
    image_points: np.ndarray
    confidence: float = 1.0

    @property
    def corner_count(self) -> int:
        return int(len(self.corner_ids))


@dataclass(frozen=True)
class CameraPoseSolve:
    sensor_id: str
    world_from_sensor: np.ndarray
    reprojection_error_px: float
    corner_count: int
    timestamp_ns: int


def solve_camera_pose_from_fixed_board(
    intrinsics: CameraIntrinsics,
    board: BoardSpec,
    observation: BoardObservation,
    *,
    min_corners: int = 6,
) -> CameraPoseSolve:
    if observation.sensor_id != intrinsics.sensor_id:
        raise ValueError("Observation sensor_id does not match intrinsics")
    if observation.corner_count < min_corners:
        raise ValueError(f"Need at least {min_corners} board corners; got {observation.corner_count}")

    object_points = np.vstack([board.object_point(int(corner_id)) for corner_id in observation.corner_ids])
    image_points = np.asarray(observation.image_points, dtype=np.float64).reshape((-1, 2))
    if len(object_points) != len(image_points):
        raise ValueError("corner_ids and image_points must have matching length")

    import cv2

    ok, rvec, tvec = cv2.solvePnP(
        object_points,
        image_points,
        np.asarray(intrinsics.camera_matrix, dtype=np.float64),
        np.asarray(intrinsics.dist_coeffs, dtype=np.float64),
        flags=cv2.SOLVEPNP_ITERATIVE,
    )
    if not ok:
        raise ValueError(f"Could not solve pose for {intrinsics.sensor_id}")

    rotation, _ = cv2.Rodrigues(rvec)
    sensor_from_world = np.eye(4, dtype=np.float64)
    sensor_from_world[:3, :3] = rotation
    sensor_from_world[:3, 3] = np.asarray(tvec, dtype=np.float64).reshape(3)
    world_from_sensor = np.linalg.inv(sensor_from_world)

    projected, _ = cv2.projectPoints(
        object_points,
        rvec,
        tvec,
        np.asarray(intrinsics.camera_matrix, dtype=np.float64),
        np.asarray(intrinsics.dist_coeffs, dtype=np.float64),
    )
    error = float(np.mean(np.linalg.norm(projected.reshape((-1, 2)) - image_points, axis=1)))
    return CameraPoseSolve(
        sensor_id=intrinsics.sensor_id,
        world_from_sensor=world_from_sensor,
        reprojection_error_px=error,
        corner_count=observation.corner_count,
        timestamp_ns=observation.timestamp_ns,
    )


def solve_common_space_from_fixed_board(
    intrinsics: Iterable[CameraIntrinsics],
    board: BoardSpec,
    observations: Iterable[BoardObservation],
    *,
    max_reprojection_error_px: float = 2.5,
) -> tuple[dict[str, CameraModel], tuple[CameraPoseSolve, ...]]:
    intrinsics_by_id = {item.sensor_id: item for item in intrinsics}
    best_solves: dict[str, CameraPoseSolve] = {}
    for observation in observations:
        camera_intrinsics = intrinsics_by_id.get(observation.sensor_id)
        if camera_intrinsics is None:
            continue
        solve = solve_camera_pose_from_fixed_board(camera_intrinsics, board, observation)
        if solve.reprojection_error_px > max_reprojection_error_px:
            continue
        current = best_solves.get(solve.sensor_id)
        if current is None or (solve.corner_count, -solve.reprojection_error_px) > (current.corner_count, -current.reprojection_error_px):
            best_solves[solve.sensor_id] = solve

    cameras: dict[str, CameraModel] = {}
    for sensor_id, solve in best_solves.items():
        item = intrinsics_by_id[sensor_id]
        cameras[sensor_id] = CameraModel(
            sensor_id=sensor_id,
            camera_matrix=np.asarray(item.camera_matrix, dtype=np.float64),
            dist_coeffs=np.asarray(item.dist_coeffs, dtype=np.float64),
            world_from_sensor=solve.world_from_sensor,
            width=item.width,
            height=item.height,
            role=item.role,
            latency_ms=item.latency_ms,
        )
    return cameras, tuple(best_solves.values())


def camera_models_to_config(cameras: Iterable[CameraModel]) -> list[dict]:
    payload = []
    for camera in cameras:
        payload.append(
            {
                "id": camera.sensor_id,
                "role": camera.role,
                "intrinsics": {
                    "width": camera.width,
                    "height": camera.height,
                    "camera_matrix": camera.camera_matrix.tolist(),
                    "dist_coeffs": camera.dist_coeffs.tolist(),
                },
                "world_from_sensor": camera.world_from_sensor.tolist(),
                "latency_ms": camera.latency_ms,
            }
        )
    return payload
