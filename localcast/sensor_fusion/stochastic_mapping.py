from __future__ import annotations

from dataclasses import dataclass, field
import json
from pathlib import Path
from typing import Iterable, Sequence

import numpy as np

from .core import CameraModel, TriangulatedPoint, triangulate_dlt
from .surface_features import SurfaceFeatureObservation, descriptor_distance


@dataclass(frozen=True)
class SceneRay:
    sensor_id: str
    timestamp_ns: int
    uv: np.ndarray
    origin: np.ndarray
    direction: np.ndarray
    confidence: float


@dataclass(frozen=True)
class TransientMatchHypothesis:
    stable_key: str
    observations: tuple[SurfaceFeatureObservation, ...]
    xyz: np.ndarray
    confidence: float
    reprojection_error_px: float
    ray_disagreement_m: float


@dataclass(frozen=True)
class RayBiasSample:
    sensor_id: str
    uv: np.ndarray
    residual_px: np.ndarray
    confidence: float


@dataclass
class ImageRayBiasMap:
    """Learns a smooth per-sensor image-space correction from reprojection residuals."""

    cell_size_px: int = 64
    smoothing: float = 0.2
    _cells: dict[tuple[str, int, int], tuple[np.ndarray, float]] = field(default_factory=dict)

    def observe(self, sample: RayBiasSample) -> None:
        key = self._key(sample.sensor_id, sample.uv)
        previous = self._cells.get(key)
        residual = np.asarray(sample.residual_px, dtype=np.float64)
        confidence = max(0.0, min(1.0, float(sample.confidence)))
        if previous is None:
            self._cells[key] = (residual, confidence)
            return
        current, weight = previous
        alpha = self.smoothing * confidence
        updated = (1.0 - alpha) * current + alpha * residual
        self._cells[key] = (updated, max(weight * 0.98, confidence))

    def correct_uv(self, sensor_id: str, uv: np.ndarray) -> np.ndarray:
        correction, _ = self._cells.get(self._key(sensor_id, uv), (np.zeros(2, dtype=np.float64), 0.0))
        return np.asarray(uv, dtype=np.float64) + correction

    def _key(self, sensor_id: str, uv: np.ndarray) -> tuple[str, int, int]:
        point = np.asarray(uv, dtype=np.float64)
        return (str(sensor_id), int(point[0] // self.cell_size_px), int(point[1] // self.cell_size_px))

    def to_dict(self) -> dict:
        return {
            "cellSizePx": self.cell_size_px,
            "smoothing": self.smoothing,
            "cells": [
                {
                    "sensorId": sensor_id,
                    "cellX": cell_x,
                    "cellY": cell_y,
                    "residualPx": [float(value) for value in residual],
                    "confidence": float(confidence),
                }
                for (sensor_id, cell_x, cell_y), (residual, confidence) in sorted(self._cells.items())
            ],
        }


@dataclass(frozen=True)
class LODCell:
    level: int
    cell: tuple[int, int, int]
    center: tuple[float, float, float]
    radius_m: float
    confidence: float
    count: int
    source_time_min_ns: int
    source_time_max_ns: int
    source_priority: float = 1.0
    source_kind: str = "generic"
    material_albedo: tuple[float, float, float] = (1.0, 1.0, 1.0)
    material_roughness: float = 0.7
    material_metallic: float = 0.0


@dataclass(frozen=True)
class MultiLODSceneCache:
    schema: str
    created_monotonic_ns: int
    levels: tuple[float, ...]
    cells: tuple[LODCell, ...]

    def to_dict(self) -> dict:
        return {
            "schema": self.schema,
            "createdMonotonicNs": int(self.created_monotonic_ns),
            "levels": [float(level) for level in self.levels],
            "cells": [
                {
                    "level": cell.level,
                    "cell": list(cell.cell),
                    "center": list(cell.center),
                    "radiusM": cell.radius_m,
                    "confidence": cell.confidence,
                    "count": cell.count,
                    "sourceTimeMinNs": cell.source_time_min_ns,
                    "sourceTimeMaxNs": cell.source_time_max_ns,
                    "sourcePriority": cell.source_priority,
                    "sourceKind": cell.source_kind,
                    "material": {
                        "albedo": list(cell.material_albedo),
                        "roughness": float(cell.material_roughness),
                        "metallic": float(cell.material_metallic),
                    },
                }
                for cell in self.cells
            ],
        }

    def write_json(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.to_dict(), indent=2), encoding="utf-8")


def pixel_scene_ray(camera: CameraModel, uv: np.ndarray, timestamp_ns: int, confidence: float = 1.0) -> SceneRay:
    point = np.asarray([float(uv[0]), float(uv[1]), 1.0], dtype=np.float64)
    direction_sensor = np.linalg.inv(camera.camera_matrix) @ point
    direction_sensor = direction_sensor / max(1.0e-12, np.linalg.norm(direction_sensor))
    rotation = camera.world_from_sensor[:3, :3]
    direction_world = rotation @ direction_sensor
    direction_world = direction_world / max(1.0e-12, np.linalg.norm(direction_world))
    return SceneRay(
        sensor_id=camera.sensor_id,
        timestamp_ns=int(timestamp_ns),
        uv=np.asarray(uv, dtype=np.float64),
        origin=camera.position_world.astype(np.float64),
        direction=direction_world.astype(np.float64),
        confidence=float(confidence),
    )


def stochastic_transient_matches(
    observations: Sequence[SurfaceFeatureObservation],
    cameras: dict[str, CameraModel],
    *,
    max_dt_ns: int = 25_000_000,
    max_descriptor_distance: float = 48.0,
    max_reprojection_error_px: float = 6.0,
    samples_per_observation: int = 4,
    seed: int = 0,
) -> tuple[TransientMatchHypothesis, ...]:
    rng = np.random.default_rng(seed)
    usable = [obs for obs in observations if obs.sensor_id in cameras]
    hypotheses: list[TransientMatchHypothesis] = []
    for index, left in enumerate(usable):
        candidates = [
            right
            for right in usable
            if right.sensor_id != left.sensor_id
            and abs(right.timestamp_ns - left.timestamp_ns) <= max_dt_ns
            and descriptor_distance(left.descriptor, right.descriptor) <= max_descriptor_distance
        ]
        if not candidates:
            continue
        rng.shuffle(candidates)
        for right in candidates[: max(1, int(samples_per_observation))]:
            hypothesis = triangulate_hypothesis(
                left,
                right,
                cameras,
                max_reprojection_error_px=max_reprojection_error_px,
                max_descriptor_distance=max_descriptor_distance,
            )
            if hypothesis is not None:
                hypotheses.append(hypothesis)
    return suppress_duplicate_hypotheses(hypotheses)


def triangulate_hypothesis(
    left: SurfaceFeatureObservation,
    right: SurfaceFeatureObservation,
    cameras: dict[str, CameraModel],
    *,
    max_reprojection_error_px: float,
    max_descriptor_distance: float,
) -> TransientMatchHypothesis | None:
    cam_a = cameras[left.sensor_id]
    cam_b = cameras[right.sensor_id]
    try:
        xyz = triangulate_dlt(cam_a.projection_matrix, left.uv, cam_b.projection_matrix, right.uv)
    except ValueError:
        return None
    error_a = cam_a.reprojection_error(xyz, left.uv)
    error_b = cam_b.reprojection_error(xyz, right.uv)
    error = float((error_a + error_b) * 0.5)
    if not np.isfinite(error) or error > max_reprojection_error_px:
        return None
    distance = descriptor_distance(left.descriptor, right.descriptor)
    descriptor_score = 1.0 - min(1.0, distance / max(1.0e-9, max_descriptor_distance))
    error_score = 1.0 - min(1.0, error / max(1.0e-9, max_reprojection_error_px))
    ray_distance = closest_ray_distance(
        pixel_scene_ray(cam_a, left.uv, left.timestamp_ns, left.confidence),
        pixel_scene_ray(cam_b, right.uv, right.timestamp_ns, right.confidence),
    )
    ray_score = 1.0 / (1.0 + ray_distance)
    confidence = float(min(left.confidence, right.confidence) * (0.4 + 0.3 * descriptor_score + 0.2 * error_score + 0.1 * ray_score))
    return TransientMatchHypothesis(
        stable_key=f"transient:{left.sensor_id}:{right.sensor_id}:{left.feature_id}:{right.feature_id}",
        observations=(left, right),
        xyz=xyz.astype(np.float64),
        confidence=confidence,
        reprojection_error_px=error,
        ray_disagreement_m=float(ray_distance),
    )


def closest_ray_distance(left: SceneRay, right: SceneRay) -> float:
    p1 = left.origin
    d1 = left.direction
    p2 = right.origin
    d2 = right.direction
    cross = np.cross(d1, d2)
    denom = float(np.linalg.norm(cross))
    if denom <= 1.0e-9:
        return float(np.linalg.norm(np.cross(p2 - p1, d1)))
    return float(abs(np.dot(p2 - p1, cross)) / denom)


def suppress_duplicate_hypotheses(hypotheses: Iterable[TransientMatchHypothesis]) -> tuple[TransientMatchHypothesis, ...]:
    best: dict[str, TransientMatchHypothesis] = {}
    for hypothesis in hypotheses:
        key = ":".join(sorted(obs.sensor_id for obs in hypothesis.observations)) + ":" + ":".join(sorted(obs.feature_id for obs in hypothesis.observations))
        current = best.get(key)
        if current is None or hypothesis.confidence > current.confidence:
            best[key] = hypothesis
    return tuple(sorted(best.values(), key=lambda item: item.confidence, reverse=True))


def learn_ray_bias_from_points(
    cameras: dict[str, CameraModel],
    points: Iterable[TriangulatedPoint],
    bias_map: ImageRayBiasMap,
) -> ImageRayBiasMap:
    for point in points:
        for sensor_id in point.sensors:
            camera = cameras.get(sensor_id)
            if camera is None:
                continue
            projected = camera.project_world(point.xyz)
            bias_map.observe(
                RayBiasSample(
                    sensor_id=sensor_id,
                    uv=projected,
                    residual_px=np.zeros(2, dtype=np.float64),
                    confidence=point.confidence,
                )
            )
    return bias_map


def multilod_cache_from_points(
    points: Iterable[TriangulatedPoint | TransientMatchHypothesis],
    *,
    levels: tuple[float, ...] = (0.02, 0.08, 0.32),
    created_monotonic_ns: int = 0,
) -> MultiLODSceneCache:
    items = list(points)
    cells: list[LODCell] = []
    for level_index, cell_size in enumerate(levels):
        buckets: dict[tuple[int, int, int], list] = {}
        for item in items:
            xyz = np.asarray(item.xyz, dtype=np.float64)
            key = tuple(int(np.floor(value / float(cell_size))) for value in xyz)
            buckets.setdefault(key, []).append(item)
        for key, bucket in buckets.items():
            weights = np.asarray([max(1.0e-6, float(item.confidence)) for item in bucket], dtype=np.float64)
            positions = np.asarray([np.asarray(item.xyz, dtype=np.float64) for item in bucket], dtype=np.float64)
            center = np.average(positions, axis=0, weights=weights)
            timestamps = [item_timestamp_ns(item) for item in bucket]
            cells.append(
                LODCell(
                    level=level_index,
                    cell=key,
                    center=tuple(float(value) for value in center),
                    radius_m=float(cell_size) * 0.5,
                    confidence=float(np.mean(weights)),
                    count=len(bucket),
                    source_time_min_ns=min(timestamps),
                    source_time_max_ns=max(timestamps),
                )
            )
    return MultiLODSceneCache(
        schema="localcast.sensor_fusion.multilod_scene_cache.v1",
        created_monotonic_ns=int(created_monotonic_ns),
        levels=tuple(float(level) for level in levels),
        cells=tuple(cells),
    )


@dataclass(frozen=True)
class LODEvidencePoint:
    stable_key: str
    xyz: np.ndarray
    confidence: float
    timestamp_ns: int
    source_priority: float
    source_kind: str
    albedo: tuple[float, float, float] = (1.0, 1.0, 1.0)
    roughness: float = 0.7
    metallic: float = 0.0


def multilod_cache_from_evidence(
    evidence: Iterable[LODEvidencePoint],
    *,
    levels: tuple[float, ...] = (0.02, 0.08, 0.32),
    created_monotonic_ns: int = 0,
) -> MultiLODSceneCache:
    items = list(evidence)
    cells: list[LODCell] = []
    for level_index, cell_size in enumerate(levels):
        buckets: dict[tuple[int, int, int], list[LODEvidencePoint]] = {}
        for item in items:
            key = tuple(int(np.floor(float(value) / float(cell_size))) for value in np.asarray(item.xyz, dtype=np.float64))
            buckets.setdefault(key, []).append(item)
        for key, bucket in buckets.items():
            weights = np.asarray([max(1.0e-6, item.confidence * item.source_priority) for item in bucket], dtype=np.float64)
            positions = np.asarray([np.asarray(item.xyz, dtype=np.float64) for item in bucket], dtype=np.float64)
            center = np.average(positions, axis=0, weights=weights)
            priority = float(max(item.source_priority for item in bucket))
            source_kind = max(bucket, key=lambda item: item.source_priority).source_kind
            albedos = np.asarray([np.asarray(item.albedo, dtype=np.float64) for item in bucket], dtype=np.float64)
            albedo = np.average(albedos, axis=0, weights=weights)
            roughness = float(np.average([item.roughness for item in bucket], weights=weights))
            metallic = float(np.average([item.metallic for item in bucket], weights=weights))
            cells.append(
                LODCell(
                    level=level_index,
                    cell=key,
                    center=tuple(float(value) for value in center),
                    radius_m=float(cell_size) * 0.5,
                    confidence=float(min(1.0, np.mean(weights))),
                    count=len(bucket),
                    source_time_min_ns=min(item.timestamp_ns for item in bucket),
                    source_time_max_ns=max(item.timestamp_ns for item in bucket),
                    source_priority=priority,
                    source_kind=source_kind,
                    material_albedo=tuple(float(np.clip(value, 0.0, 1.0)) for value in albedo),
                    material_roughness=float(np.clip(roughness, 0.0, 1.0)),
                    material_metallic=float(np.clip(metallic, 0.0, 1.0)),
                )
            )
    return MultiLODSceneCache(
        schema="localcast.sensor_fusion.multilod_scene_cache.v1",
        created_monotonic_ns=int(created_monotonic_ns),
        levels=tuple(float(level) for level in levels),
        cells=tuple(cells),
    )


def evidence_from_render_points(points: Iterable, *, source_kind: str, source_priority: float) -> tuple[LODEvidencePoint, ...]:
    rows: list[LODEvidencePoint] = []
    for point in points:
        rgba = np.clip(np.asarray(getattr(point, "color_rgba", (1.0, 1.0, 1.0, 1.0)), dtype=np.float64), 0.0, 1.0)
        rows.append(
            LODEvidencePoint(
                stable_key=str(point.stable_key),
                xyz=np.asarray(point.xyz, dtype=np.float64),
                confidence=float(point.confidence),
                timestamp_ns=int(point.source_timestamp_ns),
                source_priority=float(source_priority),
                source_kind=str(source_kind),
                albedo=tuple(float(value) for value in rgba[:3]),
                roughness=roughness_from_rgba(rgba, source_kind=source_kind),
                metallic=0.0,
            )
        )
    return tuple(rows)


def evidence_from_fusion_items(
    items: Iterable[TriangulatedPoint | TransientMatchHypothesis],
    *,
    source_kind: str,
    source_priority: float,
) -> tuple[LODEvidencePoint, ...]:
    rows: list[LODEvidencePoint] = []
    for item in items:
        rows.append(
            LODEvidencePoint(
                stable_key=str(getattr(item, "marker_id", getattr(item, "stable_key", "scene-claim"))),
                xyz=np.asarray(item.xyz, dtype=np.float64),
                confidence=float(item.confidence),
                timestamp_ns=item_timestamp_ns(item),
                source_priority=float(source_priority),
                source_kind=str(source_kind),
            )
        )
    return tuple(rows)


def item_timestamp_ns(item: TriangulatedPoint | TransientMatchHypothesis) -> int:
    if hasattr(item, "timestamp_ns"):
        return int(getattr(item, "timestamp_ns"))
    return int(max(obs.timestamp_ns for obs in item.observations))


def roughness_from_rgba(rgba: np.ndarray, *, source_kind: str) -> float:
    rgb = np.asarray(rgba[:3], dtype=np.float64)
    saturation = float(np.max(rgb) - np.min(rgb))
    alpha = float(rgba[3]) if rgba.size > 3 else 1.0
    base = 0.82 if "rgb" in source_kind else 0.74
    roughness = base - 0.22 * saturation + 0.10 * (1.0 - alpha)
    return float(np.clip(roughness, 0.25, 0.95))
