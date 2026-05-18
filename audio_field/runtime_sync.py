from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ChirpletObservation:
    source_id: str
    frame_index: int
    delay_samples: float
    phase_radians: float
    score: float
    rate_scale: float = 1.0


@dataclass(frozen=True)
class SyncState:
    delay_samples: float = 0.0
    phase_radians: float = 0.0
    rate_scale: float = 1.0
    confidence: float = 0.0
    last_frame_index: int = -1


class RuntimeSyncEstimator:
    """Maintains per-source delay/SRO/phase state from known speaker chirplets."""

    def __init__(self, min_score: float = 0.18, smoothing: float = 0.2):
        if not 0.0 <= smoothing <= 1.0:
            raise ValueError("smoothing must be between 0 and 1")
        self.min_score = min_score
        self.smoothing = smoothing
        self._states: dict[str, SyncState] = {}

    def state_for(self, source_id: str) -> SyncState:
        return self._states.get(source_id, SyncState())

    def update(self, observation: ChirpletObservation) -> SyncState:
        previous = self.state_for(observation.source_id)
        if observation.score < self.min_score:
            frozen = SyncState(
                delay_samples=previous.delay_samples,
                phase_radians=previous.phase_radians,
                rate_scale=previous.rate_scale,
                confidence=observation.score,
                last_frame_index=previous.last_frame_index,
            )
            self._states[observation.source_id] = frozen
            return frozen

        alpha = self.smoothing
        updated = SyncState(
            delay_samples=blend(previous.delay_samples, observation.delay_samples, alpha, previous.confidence),
            phase_radians=blend(previous.phase_radians, observation.phase_radians, alpha, previous.confidence),
            rate_scale=blend(previous.rate_scale, observation.rate_scale, alpha, previous.confidence),
            confidence=observation.score,
            last_frame_index=observation.frame_index,
        )
        self._states[observation.source_id] = updated
        return updated


def blend(previous: float, current: float, alpha: float, previous_confidence: float) -> float:
    if previous_confidence <= 0.0:
        return current
    return (1.0 - alpha) * previous + alpha * current
