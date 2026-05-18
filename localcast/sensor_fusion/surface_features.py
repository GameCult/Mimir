from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable

import numpy as np

from .core import CameraModel, TriangulatedPoint, triangulate_dlt


@dataclass(frozen=True)
class SurfaceFeatureObservation:
    sensor_id: str
    feature_id: str
    timestamp_ns: int
    uv: np.ndarray
    descriptor: np.ndarray
    color_bgr: tuple[int, int, int] = (255, 255, 255)
    confidence: float = 1.0


@dataclass(frozen=True)
class SurfaceFeatureTrack:
    track_id: str
    observations: tuple[SurfaceFeatureObservation, ...]


def match_surface_features(
    left: Iterable[SurfaceFeatureObservation],
    right: Iterable[SurfaceFeatureObservation],
    *,
    max_descriptor_distance: float,
    max_dt_ns: int,
) -> tuple[SurfaceFeatureTrack, ...]:
    tracks: list[SurfaceFeatureTrack] = []
    used_right: set[int] = set()
    right_items = list(right)
    for left_item in left:
        best_index = -1
        best_distance = float("inf")
        for index, right_item in enumerate(right_items):
            if index in used_right or right_item.sensor_id == left_item.sensor_id:
                continue
            if abs(right_item.timestamp_ns - left_item.timestamp_ns) > max_dt_ns:
                continue
            distance = descriptor_distance(left_item.descriptor, right_item.descriptor)
            if distance < best_distance:
                best_index = index
                best_distance = distance
        if best_index < 0 or best_distance > max_descriptor_distance:
            continue
        used_right.add(best_index)
        right_item = right_items[best_index]
        tracks.append(
            SurfaceFeatureTrack(
                track_id=f"{left_item.sensor_id}:{right_item.sensor_id}:{left_item.feature_id}:{right_item.feature_id}",
                observations=(left_item, right_item),
            )
        )
    return tuple(tracks)


def triangulate_surface_tracks(
    tracks: Iterable[SurfaceFeatureTrack],
    cameras: dict[str, CameraModel],
    *,
    max_reprojection_error_px: float = 4.0,
) -> tuple[TriangulatedPoint, ...]:
    points: list[TriangulatedPoint] = []
    for track in tracks:
        if len(track.observations) < 2:
            continue
        left, right = track.observations[:2]
        cam_a = cameras.get(left.sensor_id)
        cam_b = cameras.get(right.sensor_id)
        if cam_a is None or cam_b is None:
            continue
        try:
            xyz = triangulate_dlt(cam_a.projection_matrix, left.uv, cam_b.projection_matrix, right.uv)
        except ValueError:
            continue
        error_a = cam_a.reprojection_error(xyz, left.uv)
        error_b = cam_b.reprojection_error(xyz, right.uv)
        reprojection_error = float((error_a + error_b) * 0.5)
        if not np.isfinite(reprojection_error) or reprojection_error > max_reprojection_error_px:
            continue
        confidence = min(left.confidence, right.confidence) * max(0.0, 1.0 - reprojection_error / max_reprojection_error_px)
        points.append(
            TriangulatedPoint(
                marker_id=track.track_id,
                timestamp_ns=max(left.timestamp_ns, right.timestamp_ns),
                xyz=xyz,
                confidence=float(confidence),
                reprojection_error_px=reprojection_error,
                sensors=(left.sensor_id, right.sensor_id),
            )
        )
    return tuple(points)


def orb_surface_observations(
    sensor_id: str,
    frame_bgr: np.ndarray,
    timestamp_ns: int,
    *,
    max_features: int = 2000,
) -> tuple[SurfaceFeatureObservation, ...]:
    import cv2

    gray = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2GRAY)
    detector = cv2.ORB_create(nfeatures=max_features)
    keypoints, descriptors = detector.detectAndCompute(gray, None)
    if descriptors is None:
        return ()
    observations: list[SurfaceFeatureObservation] = []
    height, width = frame_bgr.shape[:2]
    for index, (keypoint, descriptor) in enumerate(zip(keypoints, descriptors)):
        x = int(np.clip(round(keypoint.pt[0]), 0, width - 1))
        y = int(np.clip(round(keypoint.pt[1]), 0, height - 1))
        b, g, r = frame_bgr[y, x].tolist()
        observations.append(
            SurfaceFeatureObservation(
                sensor_id=sensor_id,
                feature_id=f"orb:{index}",
                timestamp_ns=timestamp_ns,
                uv=np.array([keypoint.pt[0], keypoint.pt[1]], dtype=np.float64),
                descriptor=descriptor.astype(np.uint8),
                color_bgr=(int(b), int(g), int(r)),
                confidence=float(min(1.0, keypoint.response * 40.0 + 0.25)),
            )
        )
    return tuple(observations)


def descriptor_distance(left: np.ndarray, right: np.ndarray) -> float:
    a = np.asarray(left)
    b = np.asarray(right)
    if a.shape != b.shape:
        return float("inf")
    if a.dtype == np.uint8 and b.dtype == np.uint8:
        return float(np.unpackbits(np.bitwise_xor(a, b)).sum())
    delta = a.astype(np.float32) - b.astype(np.float32)
    return float(np.linalg.norm(delta))
