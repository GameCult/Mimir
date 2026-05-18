from __future__ import annotations

from dataclasses import dataclass

import numpy as np


@dataclass(frozen=True)
class RoomSuppressionConfig:
    block_size: int = 1024
    hop_size: int = 512
    transient_ratio: float = 2.5
    max_witness_attenuation_db: float = -18.0
    anchor_transient_attenuation_db: float = -6.0
    room_subtraction: float = 0.15
    envelope_floor: float = 1e-5


@dataclass(frozen=True)
class RoomSuppressionReport:
    frames: int
    anchor_channels: tuple[int, ...]
    witness_channels: tuple[int, ...]
    transient_blocks: int
    mean_witness_gain: float
    mean_anchor_gain: float


def suppress_room_field(
    field: np.ndarray,
    anchor_channels: list[int] | tuple[int, ...],
    witness_channels: list[int] | tuple[int, ...],
    config: RoomSuppressionConfig | None = None,
) -> tuple[np.ndarray, RoomSuppressionReport]:
    cfg = config or RoomSuppressionConfig()
    x = np.asarray(field, dtype=np.float32)
    if x.ndim != 2:
        raise ValueError("field must be frames-by-channels")
    anchors = tuple(int(channel) for channel in anchor_channels if 0 <= int(channel) < x.shape[1])
    witnesses = tuple(int(channel) for channel in witness_channels if 0 <= int(channel) < x.shape[1] and int(channel) not in anchors)
    if not anchors:
        raise ValueError("at least one anchor channel is required")
    if not witnesses:
        return x.copy(), RoomSuppressionReport(len(x), anchors, witnesses, 0, 1.0, 1.0)

    y = np.zeros_like(x)
    weight = np.zeros(len(x), dtype=np.float32)
    witness_gain_sum = 0.0
    anchor_gain_sum = 0.0
    blocks = 0
    transient_blocks = 0
    min_witness_gain = db_to_amp(cfg.max_witness_attenuation_db)
    min_anchor_gain = db_to_amp(cfg.anchor_transient_attenuation_db)
    window = np.hanning(cfg.block_size).astype(np.float32)
    if not np.any(window):
        window = np.ones(cfg.block_size, dtype=np.float32)

    for start in range(0, max(1, len(x) - cfg.block_size + 1), cfg.hop_size):
        end = min(len(x), start + cfg.block_size)
        block = x[start:end]
        win = window[: end - start, None]
        anchor_env = channel_rms(block[:, anchors])
        witness_env = channel_rms(block[:, witnesses])
        anchor_peak = channel_peak(block[:, anchors])
        witness_peak = channel_peak(block[:, witnesses])
        transient_score = max(witness_env / max(anchor_env, cfg.envelope_floor), witness_peak / max(anchor_peak, cfg.envelope_floor))
        is_transient = witness_env > cfg.envelope_floor and transient_score >= cfg.transient_ratio

        witness_gain = min_witness_gain if is_transient else 1.0
        anchor_gain = min_anchor_gain if is_transient and anchor_env < witness_env else 1.0
        if is_transient:
            transient_blocks += 1
        witness_gain_sum += witness_gain
        anchor_gain_sum += anchor_gain
        blocks += 1

        cleaned = block.copy()
        room_estimate = np.mean(block[:, witnesses], axis=1, keepdims=True)
        cleaned[:, witnesses] *= witness_gain
        if cfg.room_subtraction > 0.0:
            cleaned[:, anchors] -= cfg.room_subtraction * witness_gain * room_estimate
        cleaned[:, anchors] *= anchor_gain
        y[start:end] += cleaned * win
        weight[start:end] += win.reshape(-1)

    active = weight > 1e-9
    y[active] = y[active] / weight[active, None]
    y[~active] = x[~active]
    report = RoomSuppressionReport(
        frames=len(x),
        anchor_channels=anchors,
        witness_channels=witnesses,
        transient_blocks=transient_blocks,
        mean_witness_gain=witness_gain_sum / max(1, blocks),
        mean_anchor_gain=anchor_gain_sum / max(1, blocks),
    )
    return y.astype(np.float32), report


def channel_rms(block: np.ndarray) -> float:
    if block.size == 0:
        return 0.0
    return float(np.sqrt(np.mean(np.asarray(block, dtype=np.float32) ** 2)))


def channel_peak(block: np.ndarray) -> float:
    if block.size == 0:
        return 0.0
    return float(np.max(np.abs(block)))


def db_to_amp(db: float) -> float:
    return 10.0 ** (float(db) / 20.0)
