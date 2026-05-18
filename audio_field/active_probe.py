from __future__ import annotations

from dataclasses import dataclass
import json
import math
from pathlib import Path
import time

import numpy as np
from scipy import signal
from scipy.io import wavfile

from audio_field.phase_meaning import PhaseFieldMeaning
from audio_field.probe_optimizer import ActiveProbeOptimizer, ProbeRequest
from audio_field.runtime_sync import SyncState


@dataclass(frozen=True)
class EmittedProbe:
    request: ProbeRequest
    sample_rate: int
    start_hz: float
    end_hz: float
    duration_seconds: float
    channel: int
    path: Path
    emitted_monotonic_ns: int


class ActiveConfidenceMaintainer:
    """Turns low phase-field confidence into bounded active chirplet probes."""

    def __init__(
        self,
        optimizer: ActiveProbeOptimizer,
        *,
        sample_rate: int,
        output_dir: Path,
        duration_seconds: float = 0.03,
        start_hz: float = 1800.0,
        end_hz: float = 9000.0,
        channels: int = 2,
        channel: int = 0,
    ):
        self.optimizer = optimizer
        self.sample_rate = int(sample_rate)
        self.output_dir = Path(output_dir)
        self.duration_seconds = float(duration_seconds)
        self.start_hz = float(start_hz)
        self.end_hz = float(end_hz)
        self.channels = int(channels)
        self.channel = int(channel)
        if self.channels <= 0:
            raise ValueError("channels must be positive")
        if self.channel < 0 or self.channel >= self.channels:
            raise ValueError("channel must be inside output channel count")
        self.output_dir.mkdir(parents=True, exist_ok=True)

    def update(self, meaning: PhaseFieldMeaning, reference_block: np.ndarray, *, force_masked: bool = False) -> EmittedProbe | None:
        states = sync_states_from_phase_meaning(meaning)
        masked = force_masked or is_masked_window(reference_block)
        request = self.optimizer.choose_probe(meaning.frame_id, states, masked_window=masked)
        if request is None:
            return None
        waveform = make_probe_chirplet(
            self.sample_rate,
            duration_seconds=self.duration_seconds,
            start_hz=self.start_hz,
            end_hz=self.end_hz,
            level_dbfs=request.level_dbfs,
            channels=self.channels,
            channel=self.channel,
        )
        path = self.output_dir / f"probe-{meaning.frame_id:08d}-{request.source_id}.wav"
        wavfile.write(path, self.sample_rate, waveform.astype(np.float32))
        emitted = EmittedProbe(
            request=request,
            sample_rate=self.sample_rate,
            start_hz=self.start_hz,
            end_hz=self.end_hz,
            duration_seconds=self.duration_seconds,
            channel=self.channel,
            path=path,
            emitted_monotonic_ns=time.monotonic_ns(),
        )
        append_probe_manifest(self.output_dir / "active-probes.jsonl", emitted)
        return emitted


def sync_states_from_phase_meaning(meaning: PhaseFieldMeaning) -> dict[str, SyncState]:
    return {
        source.source_id: SyncState(
            delay_samples=source.smoothed_delay_samples,
            phase_radians=0.0,
            rate_scale=1.0,
            confidence=source.confidence,
            last_frame_index=meaning.frame_id,
        )
        for source in meaning.sources
    }


def is_masked_window(reference_block: np.ndarray, *, min_rms_dbfs: float = -42.0, crest_factor_limit: float = 8.0) -> bool:
    samples = np.asarray(reference_block, dtype=np.float32).reshape(-1)
    if samples.size == 0:
        return False
    rms = float(np.sqrt(np.mean(samples.astype(np.float64) ** 2)))
    if rms <= db_to_amp(min_rms_dbfs):
        return False
    peak = float(np.max(np.abs(samples)))
    crest = peak / max(rms, 1e-12)
    return crest <= float(crest_factor_limit)


def make_probe_chirplet(
    sample_rate: int,
    *,
    duration_seconds: float,
    start_hz: float,
    end_hz: float,
    level_dbfs: float,
    channels: int = 2,
    channel: int = 0,
) -> np.ndarray:
    frames = max(8, int(round(float(duration_seconds) * int(sample_rate))))
    t = np.arange(frames, dtype=np.float64) / float(sample_rate)
    chirp = signal.chirp(t, f0=float(start_hz), f1=float(end_hz), t1=float(duration_seconds), method="linear")
    taper = signal.windows.tukey(frames, alpha=0.5)
    chirp = chirp * taper
    peak = float(np.max(np.abs(chirp)))
    if peak > 0.0:
        chirp = chirp / peak
    chirp = chirp * db_to_amp(level_dbfs)
    out = np.zeros((frames, int(channels)), dtype=np.float32)
    out[:, int(channel)] = chirp.astype(np.float32)
    return out


def db_to_amp(db: float) -> float:
    return float(math.pow(10.0, float(db) / 20.0))


def append_probe_manifest(path: Path, emitted: EmittedProbe) -> None:
    row = {
        "frameIndex": emitted.request.frame_index,
        "sourceId": emitted.request.source_id,
        "reason": emitted.request.reason,
        "levelDbfs": emitted.request.level_dbfs,
        "urgency": emitted.request.urgency,
        "sampleRate": emitted.sample_rate,
        "startHz": emitted.start_hz,
        "endHz": emitted.end_hz,
        "durationSeconds": emitted.duration_seconds,
        "channel": emitted.channel,
        "path": str(emitted.path),
        "emittedMonotonicNs": emitted.emitted_monotonic_ns,
    }
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(row, sort_keys=True) + "\n")
