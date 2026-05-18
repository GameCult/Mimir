from __future__ import annotations

from dataclasses import dataclass
from typing import Mapping, Sequence

import numpy as np


SPEED_OF_SOUND_MPS = 343.0


@dataclass(frozen=True)
class CameraMicGeometry:
    sensor_id: str
    source_id: str
    position_m: np.ndarray


@dataclass(frozen=True)
class SpeakerGeometry:
    speaker_id: str
    position_m: np.ndarray


@dataclass(frozen=True)
class CameraChirpPoseConstraint:
    stable_key: str
    sensor_id: str
    source_id: str
    speaker_id: str
    camera_position_m: np.ndarray
    speaker_position_m: np.ndarray
    nominal_range_m: float
    observed_range_m: float
    range_residual_m: float
    confidence: float
    timestamp_ns: int


def default_camera_mic_geometry() -> tuple[CameraMicGeometry, ...]:
    return (
        CameraMicGeometry("kiyo-primary", "kiyo-0", np.array([-0.18, -0.72, 1.28], dtype=np.float64)),
        CameraMicGeometry("kiyo-secondary", "kiyo-1", np.array([0.18, -0.72, 1.28], dtype=np.float64)),
        CameraMicGeometry("ps3eye-left", "ps-eye-0", np.array([-0.78, -0.82, 1.34], dtype=np.float64)),
        CameraMicGeometry("ps3eye-right", "ps-eye-1", np.array([0.78, -0.82, 1.34], dtype=np.float64)),
    )


def default_speaker_geometry() -> tuple[SpeakerGeometry, ...]:
    return (
        SpeakerGeometry("local-speaker-left", np.array([-0.32, -1.08, 1.18], dtype=np.float64)),
        SpeakerGeometry("local-speaker-right", np.array([0.32, -1.08, 1.18], dtype=np.float64)),
    )


def constraints_from_phase_sources(
    sources: Sequence[Mapping[str, object]],
    *,
    audio_time_ns: int,
    camera_mics: Sequence[CameraMicGeometry] | None = None,
    speakers: Sequence[SpeakerGeometry] | None = None,
    max_abs_residual_m: float = 2.0,
) -> tuple[CameraChirpPoseConstraint, ...]:
    """Convert phase-field delay meaning into camera-body range constraints."""

    camera_mics = default_camera_mic_geometry() if camera_mics is None else camera_mics
    speakers = default_speaker_geometry() if speakers is None else speakers
    mic_by_source = {item.source_id: item for item in camera_mics}
    if not speakers:
        return ()
    rows: list[CameraChirpPoseConstraint] = []
    for source in sources:
        source_id = str(source.get("sourceId", ""))
        mic = mic_by_source.get(source_id)
        if mic is None:
            continue
        confidence = max(0.0, min(1.0, float(source.get("confidence", 0.0))))
        if confidence <= 0.0:
            continue
        distance_delta_m = _source_distance_delta_m(source)
        if not np.isfinite(distance_delta_m):
            continue
        speaker = _nearest_speaker(mic.position_m, speakers)
        nominal = float(np.linalg.norm(mic.position_m - speaker.position_m))
        residual = float(max(-max_abs_residual_m, min(max_abs_residual_m, distance_delta_m)))
        observed = max(0.0, nominal + residual)
        rows.append(
            CameraChirpPoseConstraint(
                stable_key=f"camera-chirp:{mic.sensor_id}:{speaker.speaker_id}",
                sensor_id=mic.sensor_id,
                source_id=source_id,
                speaker_id=speaker.speaker_id,
                camera_position_m=mic.position_m.astype(np.float64),
                speaker_position_m=speaker.position_m.astype(np.float64),
                nominal_range_m=nominal,
                observed_range_m=observed,
                range_residual_m=residual,
                confidence=confidence,
                timestamp_ns=int(audio_time_ns),
            )
        )
    return tuple(rows)


def _nearest_speaker(position_m: np.ndarray, speakers: Sequence[SpeakerGeometry]) -> SpeakerGeometry:
    return min(speakers, key=lambda speaker: float(np.linalg.norm(np.asarray(position_m) - speaker.position_m)))


def _source_distance_delta_m(source: Mapping[str, object]) -> float:
    if "distanceDeltaMeters" in source:
        return float(source["distanceDeltaMeters"])
    if "delayMs" in source:
        return float(source["delayMs"]) * 0.001 * SPEED_OF_SOUND_MPS
    if "delaySamples" in source and "sampleRate" in source:
        return float(source["delaySamples"]) / max(1.0, float(source["sampleRate"])) * SPEED_OF_SOUND_MPS
    return float("nan")
