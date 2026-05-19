"""Shared reservoir-window clipping rules for visual evidence."""

from .render_bridge import RenderPointPacket


DEFAULT_RESERVOIR_NS = 5_000_000_000


def reservoir_window_ns(latest_timestamp_ns: int, reservoir_ns: int) -> tuple[int, int]:
    end_ns = int(latest_timestamp_ns)
    return end_ns - int(reservoir_ns), end_ns


def evidence_in_reservoir(evidence: tuple, *, latest_timestamp_ns: int, reservoir_ns: int) -> tuple:
    start_ns, end_ns = reservoir_window_ns(latest_timestamp_ns, reservoir_ns)
    return tuple(item for item in evidence if start_ns <= int(item.timestamp_ns) <= end_ns)


def render_points_in_reservoir(
    points: tuple[RenderPointPacket, ...],
    *,
    latest_timestamp_ns: int,
    reservoir_ns: int,
) -> tuple[RenderPointPacket, ...]:
    start_ns, end_ns = reservoir_window_ns(latest_timestamp_ns, reservoir_ns)
    return tuple(point for point in points if start_ns <= int(point.source_timestamp_ns) <= end_ns)
