from __future__ import annotations

from dataclasses import dataclass

from audio_field.runtime_sync import SyncState


@dataclass(frozen=True)
class ProbePolicy:
    target_confidence: float = 0.65
    trigger_confidence: float = 0.35
    min_interval_frames: int = 45
    max_probe_level_dbfs: float = -18.0
    prefer_masked_windows: bool = True


@dataclass(frozen=True)
class ProbeRequest:
    frame_index: int
    source_id: str
    reason: str
    level_dbfs: float
    urgency: float


class ActiveProbeOptimizer:
    """Schedules intentional chirplets when runtime sync confidence is weak."""

    def __init__(self, policy: ProbePolicy):
        self.policy = policy
        self._last_probe_frame: int | None = None

    def choose_probe(
        self,
        frame_index: int,
        states: dict[str, SyncState],
        masked_window: bool = False,
    ) -> ProbeRequest | None:
        if self._last_probe_frame is not None:
            if frame_index - self._last_probe_frame < self.policy.min_interval_frames:
                return None
        if self.policy.prefer_masked_windows and not masked_window:
            return None

        weak = [
            (source_id, state)
            for source_id, state in states.items()
            if state.confidence < self.policy.trigger_confidence
        ]
        if not weak:
            return None

        source_id, state = min(weak, key=lambda item: item[1].confidence)
        urgency = max(0.0, self.policy.target_confidence - state.confidence)
        self._last_probe_frame = frame_index
        return ProbeRequest(
            frame_index=frame_index,
            source_id=source_id,
            reason="sync-confidence-below-trigger",
            level_dbfs=self.policy.max_probe_level_dbfs,
            urgency=urgency,
        )
