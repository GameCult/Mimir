from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Mapping, Sequence

import numpy as np

from .core import Observation2D, SensorRig


@dataclass(frozen=True)
class ClapDetectorConfig:
    audio_window_samples: int = 256
    audio_hop_samples: int = 64
    audio_onset_ratio: float = 6.0
    min_audio_energy: float = 1.0e-5
    min_camera_motion_score: float = 0.035
    camera_window_ns: int = 35_000_000
    min_camera_count: int = 2
    min_event_spacing_ns: int = 180_000_000
    timing_uncertainty_us: float = 2.0


@dataclass(frozen=True)
class AudioTransientCandidate:
    oracle_time_ns: int
    start_sample: int
    energy: float
    onset_ratio: float
    confidence: float


@dataclass(frozen=True)
class TimestampedFrame:
    sensor_id: str
    timestamp_ns: int
    image: np.ndarray


@dataclass(frozen=True)
class CameraMotionPeak:
    sensor_id: str
    timestamp_ns: int
    uv: np.ndarray
    score: float


@dataclass(frozen=True)
class ClapCalibrationEvent:
    stable_key: str
    position_m: tuple[float, float, float]
    acoustic_oracle_ns: int
    visual_observed_ns: int
    timing_uncertainty_us: float
    visual_confidence: float
    acoustic_confidence: float
    camera_peaks: tuple[CameraMotionPeak, ...]


@dataclass(frozen=True)
class CameraSyncEstimate:
    sensor_id: str
    offset_ns: int
    confidence: float
    updated_by_event: str
    observed_timestamp_ns: int
    oracle_timestamp_ns: int


class CameraClockSyncModel:
    def __init__(self, smoothing: float = 0.35) -> None:
        self.smoothing = max(0.0, min(1.0, float(smoothing)))
        self._estimates: dict[str, CameraSyncEstimate] = {}

    def observe_event(self, event: ClapCalibrationEvent) -> None:
        for peak in event.camera_peaks:
            raw_offset = int(event.acoustic_oracle_ns - peak.timestamp_ns)
            confidence = max(0.0, min(1.0, float(peak.score) * float(event.acoustic_confidence)))
            previous = self._estimates.get(peak.sensor_id)
            if previous is None:
                offset = raw_offset
                confidence = max(confidence, 1.0e-6)
            else:
                alpha = self.smoothing * confidence
                offset = round((1.0 - alpha) * previous.offset_ns + alpha * raw_offset)
                confidence = max(previous.confidence * 0.96, confidence)
            self._estimates[peak.sensor_id] = CameraSyncEstimate(
                sensor_id=peak.sensor_id,
                offset_ns=int(offset),
                confidence=float(confidence),
                updated_by_event=event.stable_key,
                observed_timestamp_ns=int(peak.timestamp_ns),
                oracle_timestamp_ns=int(event.acoustic_oracle_ns),
            )

    def offset_ns(self, sensor_id: str) -> int:
        estimate = self._estimates.get(sensor_id)
        return 0 if estimate is None else estimate.offset_ns

    def raw_time_for_oracle(self, sensor_id: str, oracle_time_ns: int) -> int:
        return int(oracle_time_ns) - self.offset_ns(sensor_id)

    def estimates(self) -> tuple[CameraSyncEstimate, ...]:
        return tuple(sorted(self._estimates.values(), key=lambda item: item.sensor_id))


class TimestampedFrameHistory:
    def __init__(self, max_frames: int = 120, max_age_ns: int | None = None) -> None:
        self.max_frames = max(2, int(max_frames))
        self.max_age_ns = None if max_age_ns is None else max(0, int(max_age_ns))
        self._frames: dict[str, list[TimestampedFrame]] = {}

    def add(self, frame: TimestampedFrame) -> None:
        bucket = self._frames.setdefault(frame.sensor_id, [])
        if bucket and bucket[-1].timestamp_ns == frame.timestamp_ns:
            return
        bucket.append(frame)
        bucket.sort(key=lambda item: item.timestamp_ns)
        del bucket[:-self.max_frames]
        self._expire_by_shared_latest()

    def add_many(self, frames: Mapping[str, TimestampedFrame]) -> None:
        for frame in frames.values():
            self.add(frame)

    def frames_by_sensor(self) -> dict[str, list[TimestampedFrame]]:
        return {sensor_id: list(frames) for sensor_id, frames in self._frames.items()}

    def nearest(self, sensor_id: str, raw_time_ns: int) -> TimestampedFrame | None:
        frames = self._frames.get(sensor_id, [])
        if not frames:
            return None
        target = int(raw_time_ns)
        return min(frames, key=lambda frame: abs(frame.timestamp_ns - target))

    def latest_timestamp_ns(self) -> int | None:
        latest = None
        for frames in self._frames.values():
            if frames:
                timestamp = frames[-1].timestamp_ns
                latest = timestamp if latest is None else max(latest, timestamp)
        return latest

    def _expire_by_shared_latest(self) -> None:
        if self.max_age_ns is None:
            return
        latest = self.latest_timestamp_ns()
        if latest is None:
            return
        cutoff = latest - self.max_age_ns
        empty: list[str] = []
        for sensor_id, frames in self._frames.items():
            self._frames[sensor_id] = [frame for frame in frames if frame.timestamp_ns >= cutoff]
            if not self._frames[sensor_id]:
                empty.append(sensor_id)
        for sensor_id in empty:
            del self._frames[sensor_id]


def detect_audio_transients(
    field: np.ndarray,
    sample_rate: int,
    *,
    audio_time_ns: int,
    start_sample: int = 0,
    config: ClapDetectorConfig = ClapDetectorConfig(),
) -> list[AudioTransientCandidate]:
    samples = np.asarray(field, dtype=np.float32)
    if samples.ndim == 1:
        samples = samples[:, None]
    if samples.ndim != 2:
        raise ValueError("audio field must be samples or samples-by-channels")
    if sample_rate <= 0:
        raise ValueError("sample_rate must be positive")
    window = int(config.audio_window_samples)
    hop = int(config.audio_hop_samples)
    if window <= 0 or hop <= 0:
        raise ValueError("audio windows must be positive")

    candidates: list[AudioTransientCandidate] = []
    previous_energy = 0.0
    for offset in range(0, max(0, samples.shape[0] - window + 1), hop):
        block = samples[offset : offset + window]
        diff = np.diff(block, axis=0)
        energy = float(np.mean(block.astype(np.float64) ** 2))
        derivative_energy = float(np.mean(diff.astype(np.float64) ** 2)) if diff.size else 0.0
        onset = max(energy, derivative_energy) / max(previous_energy, 1.0e-12)
        previous_energy = max(energy, 1.0e-12)
        if energy < config.min_audio_energy or onset < config.audio_onset_ratio:
            continue
        sample = start_sample + offset
        oracle_time_ns = audio_time_ns + round((sample - start_sample) * 1_000_000_000 / sample_rate)
        confidence = max(0.0, min(1.0, 0.35 + math.log10(max(onset, 1.0)) * 0.28))
        candidates.append(AudioTransientCandidate(oracle_time_ns, sample, energy, onset, confidence))

    return suppress_nearby_audio_candidates(candidates, config.min_event_spacing_ns)


def detect_camera_motion_peaks(
    frames_by_sensor: Mapping[str, Sequence[TimestampedFrame]],
    oracle_time_ns: int,
    *,
    config: ClapDetectorConfig = ClapDetectorConfig(),
) -> list[CameraMotionPeak]:
    peaks: list[CameraMotionPeak] = []
    for sensor_id, frames in frames_by_sensor.items():
        ordered = sorted(frames, key=lambda frame: frame.timestamp_ns)
        best: CameraMotionPeak | None = None
        for previous, current in zip(ordered, ordered[1:]):
            midpoint_ns = (previous.timestamp_ns + current.timestamp_ns) // 2
            if abs(midpoint_ns - oracle_time_ns) > config.camera_window_ns:
                continue
            if previous.image.shape[:2] != current.image.shape[:2]:
                continue
            score, uv = frame_motion_score(previous.image, current.image)
            if score < config.min_camera_motion_score:
                continue
            peak = CameraMotionPeak(sensor_id, midpoint_ns, uv, score)
            if best is None or peak.score > best.score:
                best = peak
        if best is not None:
            peaks.append(best)
    return sorted(peaks, key=lambda peak: peak.score, reverse=True)


def detect_clap_events(
    field: np.ndarray,
    sample_rate: int,
    frames_by_sensor: Mapping[str, Sequence[TimestampedFrame]],
    *,
    audio_time_ns: int,
    rig: SensorRig | None = None,
    start_sample: int = 0,
    config: ClapDetectorConfig = ClapDetectorConfig(),
) -> list[ClapCalibrationEvent]:
    events: list[ClapCalibrationEvent] = []
    for index, candidate in enumerate(
        detect_audio_transients(field, sample_rate, audio_time_ns=audio_time_ns, start_sample=start_sample, config=config)
    ):
        peaks = detect_camera_motion_peaks(frames_by_sensor, candidate.oracle_time_ns, config=config)
        if len(peaks) < config.min_camera_count:
            continue
        position, visual_confidence = localize_clap(peaks, rig)
        if visual_confidence <= 0.0:
            continue
        visual_time_ns = round(sum(peak.timestamp_ns * peak.score for peak in peaks) / max(1.0e-12, sum(peak.score for peak in peaks)))
        timing_spread_us = max(abs(peak.timestamp_ns - candidate.oracle_time_ns) for peak in peaks) / 1000.0
        events.append(
            ClapCalibrationEvent(
                stable_key=f"clap:{candidate.oracle_time_ns}:{index}",
                position_m=position,
                acoustic_oracle_ns=candidate.oracle_time_ns,
                visual_observed_ns=visual_time_ns,
                timing_uncertainty_us=max(config.timing_uncertainty_us, timing_spread_us),
                visual_confidence=visual_confidence,
                acoustic_confidence=candidate.confidence,
                camera_peaks=tuple(peaks),
            )
        )
    return events


def frame_motion_score(previous: np.ndarray, current: np.ndarray) -> tuple[float, np.ndarray]:
    a = to_luma(previous)
    b = to_luma(current)
    if a.shape != b.shape:
        raise ValueError(f"frame shape mismatch: {a.shape} vs {b.shape}")
    diff = np.abs(b - a)
    threshold = max(0.02, float(np.percentile(diff, 92)))
    mask = diff >= threshold
    if not np.any(mask):
        return 0.0, np.array([a.shape[1] * 0.5, a.shape[0] * 0.5], dtype=np.float64)
    weights = diff * mask
    yy, xx = np.indices(a.shape, dtype=np.float64)
    total = float(np.sum(weights))
    uv = np.array([float(np.sum(xx * weights) / total), float(np.sum(yy * weights) / total)], dtype=np.float64)
    score = float(np.mean(diff[mask]))
    return score, uv


def localize_clap(peaks: Sequence[CameraMotionPeak], rig: SensorRig | None) -> tuple[tuple[float, float, float], float]:
    if rig is None:
        best = peaks[0]
        return (float(best.uv[0]), float(best.uv[1]), 0.0), max(0.0, min(1.0, best.score))
    observations = [
        Observation2D(peak.sensor_id, "clap-impact", peak.timestamp_ns, peak.uv, max(0.0, min(1.0, peak.score)))
        for peak in peaks
        if peak.sensor_id in rig.cameras
    ]
    result = rig.fuse(observations)
    if not result.points:
        return (0.0, 0.0, 0.0), 0.0
    point = max(result.points, key=lambda item: item.confidence)
    return tuple(float(value) for value in point.xyz), float(point.confidence)


def suppress_nearby_audio_candidates(
    candidates: list[AudioTransientCandidate],
    min_spacing_ns: int,
) -> list[AudioTransientCandidate]:
    kept: list[AudioTransientCandidate] = []
    for candidate in sorted(candidates, key=lambda item: item.energy, reverse=True):
        if any(abs(candidate.oracle_time_ns - existing.oracle_time_ns) < min_spacing_ns for existing in kept):
            continue
        kept.append(candidate)
    return sorted(kept, key=lambda item: item.oracle_time_ns)


def to_luma(frame: np.ndarray) -> np.ndarray:
    image = np.asarray(frame)
    if image.ndim == 2:
        return normalize_image(image)
    if image.ndim != 3 or image.shape[2] < 3:
        raise ValueError("camera frame must be grayscale or color image")
    rgb = image[:, :, :3].astype(np.float32)
    return normalize_image((0.2126 * rgb[:, :, 2]) + (0.7152 * rgb[:, :, 1]) + (0.0722 * rgb[:, :, 0]))


def normalize_image(image: np.ndarray) -> np.ndarray:
    arr = np.asarray(image, dtype=np.float32)
    if arr.size == 0:
        raise ValueError("camera frame is empty")
    max_value = 255.0 if np.max(arr) > 1.5 else 1.0
    return np.clip(arr / max_value, 0.0, 1.0)
