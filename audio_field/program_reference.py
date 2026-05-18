from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from scipy import signal


@dataclass(frozen=True)
class ReferenceSuppressionReport:
    channel: int
    input_rms: float
    predicted_rms: float
    output_rms: float
    reduction_db: float
    phase_mapping: tuple[tuple[float, float, float], ...]


def suppress_program_reference(
    field: np.ndarray,
    reference: np.ndarray,
    sample_rate: int,
    channels: list[int] | tuple[int, ...],
    *,
    nperseg: int = 2048,
    noverlap: int = 1536,
    regularization: float = 1e-6,
    subtraction_strength: float = 0.85,
) -> tuple[np.ndarray, list[ReferenceSuppressionReport]]:
    x = np.asarray(field, dtype=np.float32)
    ref = np.asarray(reference, dtype=np.float32).reshape(-1)
    if x.ndim != 2:
        raise ValueError("field must be frames-by-channels")
    ref = pad_or_trim(ref, len(x))
    cleaned = x.copy()
    reports = []
    for channel in channels:
        ch = int(channel)
        if ch < 0 or ch >= x.shape[1]:
            continue
        transfer, freqs = estimate_transfer(reference=ref, observed=x[:, ch], sample_rate=sample_rate, nperseg=nperseg, noverlap=noverlap, regularization=regularization)
        predicted = synthesize_reference(reference=ref, transfer=transfer, sample_rate=sample_rate, nperseg=nperseg, noverlap=noverlap)
        predicted = pad_or_trim(predicted, len(x))
        before = x[:, ch]
        after = before - float(subtraction_strength) * predicted
        cleaned[:, ch] = after.astype(np.float32)
        before_rms = rms(before)
        after_rms = rms(after)
        reports.append(
            ReferenceSuppressionReport(
                channel=ch,
                input_rms=before_rms,
                predicted_rms=rms(predicted),
                output_rms=after_rms,
                reduction_db=20.0 * np.log10(max(after_rms, 1e-12) / max(before_rms, 1e-12)),
                phase_mapping=phase_mapping(freqs, transfer),
            )
        )
    return cleaned.astype(np.float32), reports


def estimate_transfer(*, reference: np.ndarray, observed: np.ndarray, sample_rate: int, nperseg: int, noverlap: int, regularization: float):
    freqs, _, ref_stft = signal.stft(reference, fs=sample_rate, nperseg=nperseg, noverlap=noverlap, boundary="zeros", padded=True)
    _, _, obs_stft = signal.stft(pad_or_trim(observed, len(reference)), fs=sample_rate, nperseg=nperseg, noverlap=noverlap, boundary="zeros", padded=True)
    numerator = np.sum(obs_stft * np.conj(ref_stft), axis=1)
    denominator = np.sum(np.abs(ref_stft) ** 2, axis=1) + float(regularization)
    transfer = numerator / denominator
    return transfer.astype(np.complex64), freqs.astype(np.float32)


def synthesize_reference(*, reference: np.ndarray, transfer: np.ndarray, sample_rate: int, nperseg: int, noverlap: int):
    _, _, ref_stft = signal.stft(reference, fs=sample_rate, nperseg=nperseg, noverlap=noverlap, boundary="zeros", padded=True)
    predicted_stft = ref_stft * transfer[:, None]
    _, predicted = signal.istft(predicted_stft, fs=sample_rate, nperseg=nperseg, noverlap=noverlap, input_onesided=True, boundary=True)
    return predicted.astype(np.float32)


def phase_mapping(freqs: np.ndarray, transfer: np.ndarray, *, min_hz: float = 80.0, max_hz: float = 16000.0, bins: int = 24):
    valid = (freqs >= min_hz) & (freqs <= max_hz) & (np.abs(transfer) > 1e-9)
    if not np.any(valid):
        return tuple()
    selected_freqs = np.geomspace(max(min_hz, float(freqs[valid][0])), min(max_hz, float(freqs[valid][-1])), bins)
    rows = []
    for frequency in selected_freqs:
        index = int(np.argmin(np.abs(freqs - frequency)))
        magnitude = float(abs(transfer[index]))
        phase = float(np.angle(transfer[index]))
        rows.append((float(freqs[index]), phase, magnitude))
    return tuple(rows)


def pad_or_trim(samples: np.ndarray, length: int) -> np.ndarray:
    x = np.asarray(samples, dtype=np.float32).reshape(-1)
    if len(x) == length:
        return x
    if len(x) > length:
        return x[:length]
    out = np.zeros(length, dtype=np.float32)
    out[: len(x)] = x
    return out


def rms(samples: np.ndarray) -> float:
    x = np.asarray(samples, dtype=np.float32)
    return float(np.sqrt(np.mean(x * x))) if x.size else 0.0
