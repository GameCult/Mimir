from __future__ import annotations

from dataclasses import dataclass
import math

import numpy as np

from audio_field.phase_fit import IterativeFrequencyPhaseMapper, SmoothPhaseField, estimate_phase_delay


@dataclass(frozen=True)
class SourcePhaseMeaning:
    source_id: str
    channel: int
    delay_samples: float
    smoothed_delay_samples: float
    delay_ms: float
    distance_delta_m: float
    coherence: float
    fit_error_radians: float
    confidence: float
    reference_bleed: float
    suppression_weight: float
    correction_energy: float


@dataclass(frozen=True)
class PhaseFieldMeaning:
    frame_id: int
    audio_time_ns: int
    sample_rate: int
    start_sample: int
    frame_count: int
    reference_id: str
    sources: tuple[SourcePhaseMeaning, ...]
    global_confidence: float
    needs_active_probe: bool
    active_probe_reason: str


class LivePhaseMeaningExtractor:
    """Extracts live alignment and cleanup meaning from internal phase evidence."""

    def __init__(
        self,
        source_ids: list[str] | tuple[str, ...],
        sample_rate: int,
        frequencies_hz: list[float] | tuple[float, ...],
        *,
        reference_id: str = "program-reference",
        speed_of_sound_mps: float = 343.0,
        min_confidence: float = 0.35,
        smoothing: float = 0.25,
        max_step_samples: float = 32.0,
    ):
        if not source_ids:
            raise ValueError("at least one source_id is required")
        self.source_ids = tuple(str(source_id) for source_id in source_ids)
        self.sample_rate = int(sample_rate)
        self.frequencies_hz = tuple(float(frequency) for frequency in frequencies_hz)
        self.reference_id = str(reference_id)
        self.speed_of_sound_mps = float(speed_of_sound_mps)
        self.min_confidence = float(min_confidence)
        self._phase_field = SmoothPhaseField(smoothing=smoothing, max_step_samples=max_step_samples)
        self._mapper = IterativeFrequencyPhaseMapper(self.frequencies_hz, self.sample_rate)

    def update(
        self,
        reference: np.ndarray,
        field: np.ndarray,
        *,
        frame_id: int,
        start_sample: int,
        audio_time_ns: int,
    ) -> PhaseFieldMeaning:
        ref = np.asarray(reference, dtype=np.float32).reshape(-1)
        samples = np.asarray(field, dtype=np.float32)
        if samples.ndim != 2:
            raise ValueError("field must be a frames-by-channels array")
        count = min(len(ref), samples.shape[0])
        if count <= 0:
            raise ValueError("reference and field must contain samples")
        ref = ref[:count]
        samples = samples[:count]

        source_rows: list[SourcePhaseMeaning] = []
        for channel, source_id in enumerate(self.source_ids):
            if channel >= samples.shape[1]:
                continue
            observed = samples[:, channel]
            estimate = estimate_phase_delay(ref, observed, self.sample_rate, self.frequencies_hz)
            coherence = mean_band_coherence(estimate.bands)
            bleed = normalized_correlation(ref, observed)
            confidence = phase_confidence(coherence, estimate.fit_error_radians, bleed, ref, observed)
            smoothed_delay = self._phase_field.update(source_id, estimate.delay_samples, confidence)
            correction = self._mapper.update(source_id, estimate, confidence)
            delay_ms = 1000.0 * smoothed_delay / float(self.sample_rate)
            source_rows.append(
                SourcePhaseMeaning(
                    source_id=source_id,
                    channel=channel,
                    delay_samples=float(estimate.delay_samples),
                    smoothed_delay_samples=float(smoothed_delay),
                    delay_ms=float(delay_ms),
                    distance_delta_m=float(smoothed_delay * self.speed_of_sound_mps / float(self.sample_rate)),
                    coherence=float(coherence),
                    fit_error_radians=float(estimate.fit_error_radians),
                    confidence=float(confidence),
                    reference_bleed=float(bleed),
                    suppression_weight=float(max(0.0, min(1.0, bleed * confidence))),
                    correction_energy=float(np.sqrt(np.mean(correction * correction))) if correction.size else 0.0,
                )
            )

        global_confidence = float(np.mean([row.confidence for row in source_rows])) if source_rows else 0.0
        low_sources = [row.source_id for row in source_rows if row.confidence < self.min_confidence]
        return PhaseFieldMeaning(
            frame_id=int(frame_id),
            audio_time_ns=int(audio_time_ns),
            sample_rate=self.sample_rate,
            start_sample=int(start_sample),
            frame_count=int(count),
            reference_id=self.reference_id,
            sources=tuple(source_rows),
            global_confidence=global_confidence,
            needs_active_probe=bool(low_sources or global_confidence < self.min_confidence),
            active_probe_reason=", ".join(low_sources) if low_sources else ("global-confidence" if global_confidence < self.min_confidence else ""),
        )


def mean_band_coherence(bands) -> float:
    values = [float(band.coherence) for band in bands]
    return float(np.mean(values)) if values else 0.0


def phase_confidence(coherence: float, fit_error_radians: float, reference_bleed: float, reference: np.ndarray, observed: np.ndarray) -> float:
    energy_gate = min(1.0, rms(observed) / max(rms(reference) * 0.05, 1e-9))
    fit_gate = math.exp(-max(0.0, float(fit_error_radians)))
    bleed_gate = math.sqrt(max(0.0, min(1.0, float(reference_bleed))))
    return float(max(0.0, min(1.0, float(coherence) * fit_gate * energy_gate * bleed_gate)))


def normalized_correlation(reference: np.ndarray, observed: np.ndarray) -> float:
    ref = np.asarray(reference, dtype=np.float64).reshape(-1)
    obs = np.asarray(observed, dtype=np.float64).reshape(-1)
    count = min(len(ref), len(obs))
    if count <= 1:
        return 0.0
    ref = ref[:count] - float(np.mean(ref[:count]))
    obs = obs[:count] - float(np.mean(obs[:count]))
    denom = float(np.linalg.norm(ref) * np.linalg.norm(obs))
    if denom <= 1e-12:
        return 0.0
    return float(max(0.0, min(1.0, abs(float(np.dot(ref, obs) / denom)))))


def rms(samples: np.ndarray) -> float:
    x = np.asarray(samples, dtype=np.float64).reshape(-1)
    return float(np.sqrt(np.mean(x * x))) if x.size else 0.0
