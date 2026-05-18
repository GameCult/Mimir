from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class LatencyPolicy:
    sample_rate: int
    target_latency_ms: float
    max_latency_ms: float
    block_size: int

    @property
    def target_latency_samples(self) -> int:
        return int(round(self.sample_rate * self.target_latency_ms / 1000.0))

    @property
    def max_latency_samples(self) -> int:
        return int(round(self.sample_rate * self.max_latency_ms / 1000.0))


class RealtimeConvergence:
    """Chooses when an aligned field may be emitted behind the live edge."""

    def __init__(self, policy: LatencyPolicy):
        if policy.target_latency_samples > policy.max_latency_samples:
            raise ValueError("target latency cannot exceed max latency")
        self.policy = policy
        self._next_start_sample = 0

    @property
    def next_start_sample(self) -> int:
        return self._next_start_sample

    def ready_start_samples(self, latest_by_source: dict[str, int]) -> list[int]:
        if not latest_by_source:
            return []
        live_edge = min(latest_by_source.values())
        emit_until = live_edge - self.policy.target_latency_samples
        starts: list[int] = []
        while self._next_start_sample + self.policy.block_size <= emit_until:
            starts.append(self._next_start_sample)
            self._next_start_sample += self.policy.block_size
        return starts

    def is_lagging(self, latest_by_source: dict[str, int]) -> bool:
        if not latest_by_source:
            return False
        live_edge = min(latest_by_source.values())
        return live_edge - self._next_start_sample > self.policy.max_latency_samples
