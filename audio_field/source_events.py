from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Iterable

import numpy as np


@dataclass(frozen=True)
class SourceEvent:
    event_id: int
    start_sample: int
    duration_samples: int
    position_m: tuple[float, float, float]
    direction_m: tuple[float, float, float]
    energy: float
    confidence: float
    kind: str


@dataclass(frozen=True)
class VoiceFocusFrame:
    start_sample: int
    duration_samples: int
    anchor_weights: dict[int, float]
    witness_energy: float
    anchor_energy: float
    noise_ratio: float


def analyze_source_field(
    field: np.ndarray,
    sample_rate: int,
    mic_positions: dict[int, tuple[float, float, float]],
    anchor_channels: Iterable[int],
    witness_channels: Iterable[int],
    *,
    block_size: int = 1024,
    hop_size: int = 512,
    transient_ratio: float = 2.5,
    min_event_energy: float = 1e-6,
    onset_ratio: float = 1.8,
    min_event_spacing_samples: int = 2048,
) -> tuple[list[SourceEvent], list[VoiceFocusFrame]]:
    samples = np.asarray(field, dtype=np.float32)
    if samples.ndim != 2:
        raise ValueError("source field must be frames-by-channels")
    anchors = [int(channel) for channel in anchor_channels if int(channel) < samples.shape[1]]
    witnesses = [int(channel) for channel in witness_channels if int(channel) < samples.shape[1]]
    if not witnesses:
        return [], []
    block_size = int(block_size)
    hop_size = int(hop_size)
    if block_size <= 0 or hop_size <= 0:
        raise ValueError("block_size and hop_size must be positive")

    events: list[SourceEvent] = []
    focus: list[VoiceFocusFrame] = []
    candidates = []
    window = np.hanning(block_size).astype(np.float32)
    previous_witness_energy = 0.0
    for start in range(0, max(0, samples.shape[0] - block_size + 1), hop_size):
        block = samples[start : start + block_size] * window[:, None]
        witness_energy_by_channel = {
            channel: rms_energy(block[:, channel])
            for channel in witnesses
            if channel in mic_positions
        }
        anchor_energy_by_channel = {channel: rms_energy(block[:, channel]) for channel in anchors}
        witness_energy = float(sum(witness_energy_by_channel.values()))
        anchor_energy = float(sum(anchor_energy_by_channel.values()))
        noise_ratio = witness_energy / max(anchor_energy, 1e-12)
        focus.append(
            VoiceFocusFrame(
                start_sample=start,
                duration_samples=block_size,
                anchor_weights=anchor_mix_weights(anchor_energy_by_channel),
                witness_energy=witness_energy,
                anchor_energy=anchor_energy,
                noise_ratio=noise_ratio,
            )
        )
        onset = witness_energy / max(previous_witness_energy, 1e-12)
        previous_witness_energy = witness_energy
        if witness_energy < min_event_energy or noise_ratio < transient_ratio or onset < onset_ratio:
            continue
        position, confidence = localize_energy_centroid(witness_energy_by_channel, mic_positions)
        candidates.append(
            (
                start,
                SourceEvent(
                    event_id=-1,
                    start_sample=start,
                    duration_samples=block_size,
                    position_m=position,
                    direction_m=unit_vector(position),
                    energy=witness_energy,
                    confidence=confidence,
                    kind="witness-dominant-transient",
                ),
            )
        )
    events = suppress_nearby_events([event for _, event in candidates], int(min_event_spacing_samples))
    return events, focus


def rms_energy(samples: np.ndarray) -> float:
    if samples.size == 0:
        return 0.0
    return float(np.mean(np.asarray(samples, dtype=np.float64) ** 2))


def anchor_mix_weights(energies: dict[int, float]) -> dict[int, float]:
    if not energies:
        return {}
    floor = max(max(energies.values()) * 1e-6, 1e-12)
    total = sum(max(value, floor) for value in energies.values())
    return {channel: max(value, floor) / total for channel, value in sorted(energies.items())}


def localize_energy_centroid(
    energies: dict[int, float],
    mic_positions: dict[int, tuple[float, float, float]],
) -> tuple[tuple[float, float, float], float]:
    usable = [(channel, max(float(energy), 0.0)) for channel, energy in energies.items() if energy > 0.0]
    if not usable:
        return (0.0, 0.0, 0.0), 0.0
    weights = np.asarray([energy for _, energy in usable], dtype=np.float64)
    positions = np.asarray([mic_positions[channel] for channel, _ in usable], dtype=np.float64)
    total = float(np.sum(weights))
    centroid = np.sum(positions * weights[:, None], axis=0) / total
    dominance = float(np.max(weights) / total)
    spread = float(np.sqrt(np.average(np.sum((positions - centroid) ** 2, axis=1), weights=weights)))
    confidence = max(0.0, min(1.0, 0.35 + dominance - 0.15 * spread))
    return tuple(float(value) for value in centroid), confidence


def suppress_nearby_events(events: list[SourceEvent], min_spacing_samples: int) -> list[SourceEvent]:
    if not events:
        return []
    min_spacing_samples = max(0, int(min_spacing_samples))
    kept: list[SourceEvent] = []
    for event in sorted(events, key=lambda item: item.energy, reverse=True):
        if any(abs(event.start_sample - existing.start_sample) < min_spacing_samples for existing in kept):
            continue
        kept.append(event)
    kept.sort(key=lambda item: item.start_sample)
    return [
        SourceEvent(
            event_id=index,
            start_sample=event.start_sample,
            duration_samples=event.duration_samples,
            position_m=event.position_m,
            direction_m=event.direction_m,
            energy=event.energy,
            confidence=event.confidence,
            kind=event.kind,
        )
        for index, event in enumerate(kept)
    ]


def unit_vector(position: tuple[float, float, float]) -> tuple[float, float, float]:
    norm = math.sqrt(sum(value * value for value in position))
    if norm <= 1e-12:
        return (0.0, 0.0, 0.0)
    return tuple(float(value / norm) for value in position)
