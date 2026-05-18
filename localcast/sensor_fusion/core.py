from __future__ import annotations

from dataclasses import dataclass, field
import json
import math
from pathlib import Path
from typing import Iterable

import numpy as np


ArrayLike = Iterable[Iterable[float]] | Iterable[float]


def _array(value: ArrayLike, shape: tuple[int, ...], name: str) -> np.ndarray:
    arr = np.asarray(value, dtype=np.float64)
    if arr.shape != shape:
        raise ValueError(f"{name} must have shape {shape}, got {arr.shape}")
    return arr


def _normalize_homogeneous(point: np.ndarray) -> np.ndarray:
    if abs(point[-1]) < 1e-12:
        raise ValueError("Cannot normalize homogeneous point with near-zero W")
    return point[:-1] / point[-1]


@dataclass(frozen=True)
class CameraModel:
    """Pinhole camera model with a world-from-camera transform."""

    sensor_id: str
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray
    world_from_sensor: np.ndarray
    width: int
    height: int
    role: str = "tracking"
    latency_ms: float = 0.0

    @staticmethod
    def from_dict(data: dict) -> "CameraModel":
        intrinsics = data.get("intrinsics", {})
        return CameraModel(
            sensor_id=data["id"],
            camera_matrix=_array(intrinsics["camera_matrix"], (3, 3), "camera_matrix"),
            dist_coeffs=np.asarray(intrinsics.get("dist_coeffs", []), dtype=np.float64),
            world_from_sensor=_array(data["world_from_sensor"], (4, 4), "world_from_sensor"),
            width=int(intrinsics["width"]),
            height=int(intrinsics["height"]),
            role=data.get("role", "tracking"),
            latency_ms=float(data.get("latency_ms", 0.0)),
        )

    @property
    def sensor_from_world(self) -> np.ndarray:
        return np.linalg.inv(self.world_from_sensor)

    @property
    def projection_matrix(self) -> np.ndarray:
        return self.camera_matrix @ self.sensor_from_world[:3, :]

    @property
    def position_world(self) -> np.ndarray:
        return self.world_from_sensor[:3, 3]

    def project_world(self, point_world: np.ndarray) -> np.ndarray:
        point = np.ones(4, dtype=np.float64)
        point[:3] = point_world
        uvw = self.projection_matrix @ point
        return _normalize_homogeneous(uvw)

    def reprojection_error(self, point_world: np.ndarray, uv: np.ndarray) -> float:
        return float(np.linalg.norm(self.project_world(point_world) - uv))


@dataclass(frozen=True)
class Observation2D:
    sensor_id: str
    marker_id: str
    timestamp_ns: int
    uv: np.ndarray
    confidence: float = 1.0
    sequence: int | None = None

    @staticmethod
    def from_dict(data: dict) -> "Observation2D":
        return Observation2D(
            sensor_id=data["sensor_id"],
            marker_id=data["marker_id"],
            timestamp_ns=int(data["timestamp_ns"]),
            uv=_array(data["uv"], (2,), "uv"),
            confidence=float(data.get("confidence", 1.0)),
            sequence=None if data.get("sequence") is None else int(data["sequence"]),
        )


@dataclass(frozen=True)
class TriangulatedPoint:
    marker_id: str
    timestamp_ns: int
    xyz: np.ndarray
    confidence: float
    reprojection_error_px: float
    sensors: tuple[str, ...]


@dataclass(frozen=True)
class FusionResult:
    points: tuple[TriangulatedPoint, ...]
    dropped: tuple[str, ...] = ()


@dataclass
class FusionConfig:
    max_pair_dt_ns: int = 25_000_000
    max_reprojection_error_px: float = 4.0
    min_confidence: float = 0.1
    cache_ttl_ns: int = 500_000_000

    @staticmethod
    def from_dict(data: dict) -> "FusionConfig":
        return FusionConfig(
            max_pair_dt_ns=int(data.get("max_pair_dt_ns", 25_000_000)),
            max_reprojection_error_px=float(data.get("max_reprojection_error_px", 4.0)),
            min_confidence=float(data.get("min_confidence", 0.1)),
            cache_ttl_ns=int(data.get("cache_ttl_ns", 500_000_000)),
        )


@dataclass
class SensorRig:
    cameras: dict[str, CameraModel]
    config: FusionConfig = field(default_factory=FusionConfig)

    @staticmethod
    def from_dict(data: dict) -> "SensorRig":
        cameras = {item["id"]: CameraModel.from_dict(item) for item in data.get("cameras", [])}
        if len(cameras) < 2:
            raise ValueError("Sensor rig needs at least two cameras for triangulation")
        return SensorRig(cameras=cameras, config=FusionConfig.from_dict(data.get("fusion", {})))

    def fuse(self, observations: Iterable[Observation2D]) -> FusionResult:
        grouped: dict[str, list[Observation2D]] = {}
        dropped: list[str] = []
        for obs in observations:
            if obs.sensor_id not in self.cameras:
                dropped.append(f"{obs.marker_id}: unknown sensor {obs.sensor_id}")
                continue
            if obs.confidence < self.config.min_confidence:
                dropped.append(f"{obs.marker_id}: low confidence from {obs.sensor_id}")
                continue
            grouped.setdefault(obs.marker_id, []).append(obs)

        points: list[TriangulatedPoint] = []
        for marker_id, marker_obs in grouped.items():
            point = self._fuse_marker(marker_id, marker_obs)
            if point is None:
                dropped.append(f"{marker_id}: no valid camera pair")
            else:
                points.append(point)
        return FusionResult(points=tuple(points), dropped=tuple(dropped))

    def _fuse_marker(self, marker_id: str, observations: list[Observation2D]) -> TriangulatedPoint | None:
        best: TriangulatedPoint | None = None
        ordered = sorted(observations, key=lambda item: item.confidence, reverse=True)
        for i, left in enumerate(ordered):
            for right in ordered[i + 1 :]:
                if left.sensor_id == right.sensor_id:
                    continue
                dt = abs(left.timestamp_ns - right.timestamp_ns)
                if dt > self.config.max_pair_dt_ns:
                    continue
                point = self._triangulate_pair(marker_id, left, right)
                if point is None:
                    continue
                if best is None or point.confidence > best.confidence:
                    best = point
        return best

    def _triangulate_pair(
        self,
        marker_id: str,
        left: Observation2D,
        right: Observation2D,
    ) -> TriangulatedPoint | None:
        cam_a = self.cameras[left.sensor_id]
        cam_b = self.cameras[right.sensor_id]
        xyz = triangulate_dlt(cam_a.projection_matrix, left.uv, cam_b.projection_matrix, right.uv)
        error_a = cam_a.reprojection_error(xyz, left.uv)
        error_b = cam_b.reprojection_error(xyz, right.uv)
        reprojection_error = (error_a + error_b) * 0.5
        if not math.isfinite(reprojection_error) or reprojection_error > self.config.max_reprojection_error_px:
            return None
        dt = abs(left.timestamp_ns - right.timestamp_ns)
        time_score = 1.0 - min(1.0, dt / max(1, self.config.max_pair_dt_ns))
        error_score = 1.0 - min(1.0, reprojection_error / max(1e-9, self.config.max_reprojection_error_px))
        confidence = float(min(left.confidence, right.confidence) * (0.5 + 0.5 * time_score) * (0.5 + 0.5 * error_score))
        return TriangulatedPoint(
            marker_id=marker_id,
            timestamp_ns=max(left.timestamp_ns, right.timestamp_ns),
            xyz=xyz,
            confidence=confidence,
            reprojection_error_px=float(reprojection_error),
            sensors=(left.sensor_id, right.sensor_id),
        )


def triangulate_dlt(proj_a: np.ndarray, uv_a: np.ndarray, proj_b: np.ndarray, uv_b: np.ndarray) -> np.ndarray:
    """Triangulate one point from two calibrated cameras using linear DLT."""

    a = np.asarray(proj_a, dtype=np.float64)
    b = np.asarray(proj_b, dtype=np.float64)
    u0, v0 = np.asarray(uv_a, dtype=np.float64)
    u1, v1 = np.asarray(uv_b, dtype=np.float64)
    system = np.vstack(
        [
            u0 * a[2] - a[0],
            v0 * a[2] - a[1],
            u1 * b[2] - b[0],
            v1 * b[2] - b[1],
        ]
    )
    _, _, vh = np.linalg.svd(system)
    return _normalize_homogeneous(vh[-1])


@dataclass
class TrackCache:
    ttl_ns: int
    tracks: dict[str, TriangulatedPoint] = field(default_factory=dict)

    def update(self, point: TriangulatedPoint) -> None:
        current = self.tracks.get(point.marker_id)
        if current is None or point.timestamp_ns >= current.timestamp_ns or point.confidence > current.confidence:
            self.tracks[point.marker_id] = point

    def expire(self, now_ns: int) -> None:
        expired = [key for key, value in self.tracks.items() if now_ns - value.timestamp_ns > self.ttl_ns]
        for key in expired:
            del self.tracks[key]

    def points(self) -> tuple[TriangulatedPoint, ...]:
        return tuple(sorted(self.tracks.values(), key=lambda item: item.marker_id))


@dataclass(frozen=True)
class PointCloud:
    points: tuple[TriangulatedPoint, ...]

    def write_ply(self, path: Path) -> None:
        lines = [
            "ply",
            "format ascii 1.0",
            f"element vertex {len(self.points)}",
            "property float x",
            "property float y",
            "property float z",
            "property uchar red",
            "property uchar green",
            "property uchar blue",
            "property float confidence",
            "end_header",
        ]
        for point in self.points:
            shade = int(max(0, min(255, round(point.confidence * 255))))
            x, y, z = point.xyz
            lines.append(f"{x:.9f} {y:.9f} {z:.9f} {shade} {shade} 255 {point.confidence:.6f}")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def load_fusion_config(path: Path) -> SensorRig:
    return SensorRig.from_dict(json.loads(path.read_text(encoding="utf-8")))
