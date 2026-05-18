from __future__ import annotations

from dataclasses import dataclass
import math

import numpy as np


@dataclass(frozen=True)
class PhaseBand:
    frequency_hz: float
    phase_delta_radians: float
    coherence: float


@dataclass(frozen=True)
class PhaseDelayEstimate:
    delay_samples: float
    delay_ms: float
    fit_error_radians: float
    bands: tuple[PhaseBand, ...]


class SmoothPhaseField:
    """Smooths phase-derived delay deltas over a bounded evidence window."""

    def __init__(self, smoothing: float = 0.25, max_step_samples: float = 32.0):
        if not 0.0 <= smoothing <= 1.0:
            raise ValueError("smoothing must be between 0 and 1")
        self.smoothing = float(smoothing)
        self.max_step_samples = float(max_step_samples)
        self._delay_by_source: dict[str, float] = {}

    def update(self, source_id: str, delay_delta_samples: float, confidence: float) -> float:
        previous = self._delay_by_source.get(source_id)
        if previous is None:
            self._delay_by_source[source_id] = float(delay_delta_samples)
            return float(delay_delta_samples)
        step = float(delay_delta_samples) - previous
        step = max(-self.max_step_samples, min(self.max_step_samples, step))
        alpha = self.smoothing * max(0.0, min(1.0, float(confidence)))
        updated = previous + alpha * step
        self._delay_by_source[source_id] = updated
        return updated

    def state(self) -> dict[str, float]:
        return dict(self._delay_by_source)


class IterativeFrequencyPhaseMapper:
    """Learns a smooth per-source frequency correction from phase residuals."""

    def __init__(
        self,
        frequencies_hz: list[float] | tuple[float, ...],
        sample_rate: int,
        *,
        learning_rate: float = 0.2,
        max_phase_step_radians: float = 0.2,
    ):
        self.frequencies_hz = tuple(float(f) for f in frequencies_hz)
        self.sample_rate = int(sample_rate)
        self.learning_rate = float(learning_rate)
        self.max_phase_step_radians = float(max_phase_step_radians)
        self._phase_by_source: dict[str, np.ndarray] = {}

    def correction_for(self, source_id: str) -> np.ndarray:
        return self._phase_by_source.get(source_id, np.zeros(len(self.frequencies_hz), dtype=np.float64)).copy()

    def update(self, source_id: str, estimate: PhaseDelayEstimate, confidence: float) -> np.ndarray:
        current = self._phase_by_source.get(source_id)
        if current is None:
            current = np.zeros(len(self.frequencies_hz), dtype=np.float64)
        residual = phase_residuals_with_sample_rate(self.frequencies_hz, estimate, self.sample_rate)
        if len(residual) != len(current):
            return current.copy()
        step = -self.learning_rate * max(0.0, min(1.0, float(confidence))) * residual
        step = np.clip(step, -self.max_phase_step_radians, self.max_phase_step_radians)
        updated = current + step
        self._phase_by_source[source_id] = updated
        return updated.copy()

    def state(self) -> dict[str, list[float]]:
        return {source_id: values.tolist() for source_id, values in self._phase_by_source.items()}


def phase_residuals_with_sample_rate(frequencies_hz: tuple[float, ...] | list[float], estimate: PhaseDelayEstimate, sample_rate: int) -> np.ndarray:
    by_frequency = {round(band.frequency_hz, 6): band.phase_delta_radians for band in estimate.bands}
    delay_seconds = estimate.delay_samples / float(sample_rate)
    residuals = []
    for frequency in frequencies_hz:
        phase = by_frequency.get(round(float(frequency), 6))
        if phase is None:
            return np.asarray([], dtype=np.float64)
        expected = -2.0 * math.pi * float(frequency) * delay_seconds
        residuals.append(wrap_phase(phase - expected))
    return np.asarray(residuals, dtype=np.float64)


def wrap_phase(value: float) -> float:
    return (float(value) + math.pi) % (2.0 * math.pi) - math.pi


def estimate_phase_delay(
    reference: np.ndarray,
    observed: np.ndarray,
    sample_rate: int,
    frequencies_hz: list[float] | tuple[float, ...],
    *,
    max_abs_delay_ms: float = 3.0,
) -> PhaseDelayEstimate:
    ref = np.asarray(reference, dtype=np.float32).reshape(-1)
    obs = np.asarray(observed, dtype=np.float32).reshape(-1)
    count = min(len(ref), len(obs))
    if count < 8:
        return PhaseDelayEstimate(0.0, 0.0, 0.0, tuple())
    ref = ref[:count] - float(np.mean(ref[:count]))
    obs = obs[:count] - float(np.mean(obs[:count]))
    window = np.hanning(count).astype(np.float32)
    ref = ref * window
    obs = obs * window
    fft_size = int(2 ** math.ceil(math.log2(max(16, count))))
    ref_fft = np.fft.rfft(ref, n=fft_size)
    obs_fft = np.fft.rfft(obs, n=fft_size)
    bin_hz = float(sample_rate) / float(fft_size)

    rows = []
    phases = []
    weights = []
    bands = []
    for frequency in frequencies_hz:
        if frequency <= 0 or frequency >= sample_rate / 2:
            continue
        index = int(round(float(frequency) / bin_hz))
        if index <= 0 or index >= len(ref_fft):
            continue
        cross = obs_fft[index] * np.conj(ref_fft[index])
        magnitude = float(abs(cross))
        denom = float(abs(obs_fft[index]) * abs(ref_fft[index]))
        coherence = 0.0 if denom <= 1e-12 else max(0.0, min(1.0, magnitude / denom))
        if magnitude <= 1e-12:
            continue
        phase = float(np.angle(cross))
        rows.append([-2.0 * math.pi * float(frequency), 1.0])
        phases.append(phase)
        weights.append(max(1e-6, coherence))
        bands.append(PhaseBand(float(frequency), phase, coherence))
    if len(rows) < 2:
        return PhaseDelayEstimate(0.0, 0.0, 0.0, tuple(bands))

    phases_array = np.unwrap(np.asarray(phases, dtype=np.float64))
    design = np.asarray(rows, dtype=np.float64)
    weights_array = np.sqrt(np.asarray(weights, dtype=np.float64))
    weighted_design = design * weights_array[:, None]
    weighted_phase = phases_array * weights_array
    delay_seconds, phase_offset = np.linalg.lstsq(weighted_design, weighted_phase, rcond=None)[0]
    max_abs_delay = float(max_abs_delay_ms) / 1000.0
    delay_seconds = max(-max_abs_delay, min(max_abs_delay, float(delay_seconds)))
    fitted = design @ np.asarray([delay_seconds, phase_offset])
    fit_error = float(np.sqrt(np.mean((phases_array - fitted) ** 2)))
    delay_samples = delay_seconds * float(sample_rate)
    return PhaseDelayEstimate(
        delay_samples=delay_samples,
        delay_ms=1000.0 * delay_seconds,
        fit_error_radians=fit_error,
        bands=tuple(bands),
    )
