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
        pattern: str = "single",
        harmonic_root_hz: float = 440.0,
        harmonic_voices: int = 24,
    ):
        self.optimizer = optimizer
        self.sample_rate = int(sample_rate)
        self.output_dir = Path(output_dir)
        self.duration_seconds = float(duration_seconds)
        self.start_hz = float(start_hz)
        self.end_hz = float(end_hz)
        self.channels = int(channels)
        self.channel = int(channel)
        self.pattern = str(pattern)
        self.harmonic_root_hz = float(harmonic_root_hz)
        self.harmonic_voices = int(harmonic_voices)
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
        waveform = make_probe_waveform(
            self.sample_rate,
            duration_seconds=self.duration_seconds,
            start_hz=self.start_hz,
            end_hz=self.end_hz,
            level_dbfs=request.level_dbfs,
            channels=self.channels,
            channel=self.channel,
            pattern=self.pattern,
            harmonic_root_hz=self.harmonic_root_hz,
            harmonic_voices=self.harmonic_voices,
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


def make_probe_waveform(
    sample_rate: int,
    *,
    duration_seconds: float,
    start_hz: float,
    end_hz: float,
    level_dbfs: float,
    channels: int = 2,
    channel: int = 0,
    pattern: str = "single",
    harmonic_root_hz: float = 440.0,
    harmonic_voices: int = 24,
) -> np.ndarray:
    if pattern == "single":
        return make_probe_chirplet(
            sample_rate,
            duration_seconds=duration_seconds,
            start_hz=start_hz,
            end_hz=end_hz,
            level_dbfs=level_dbfs,
            channels=channels,
            channel=channel,
        )
    if pattern == "harmonic-dense":
        return make_harmonic_probe_texture(
            sample_rate,
            duration_seconds=duration_seconds,
            start_hz=start_hz,
            end_hz=end_hz,
            level_dbfs=level_dbfs,
            channels=channels,
            channel=channel,
            root_hz=harmonic_root_hz,
            voices=harmonic_voices,
        )
    raise ValueError(f"unknown probe pattern: {pattern}")


def make_harmonic_probe_texture(
    sample_rate: int,
    *,
    duration_seconds: float,
    start_hz: float,
    end_hz: float,
    level_dbfs: float,
    channels: int = 2,
    channel: int = 0,
    root_hz: float = 440.0,
    voices: int = 24,
) -> np.ndarray:
    frames = max(8, int(round(float(duration_seconds) * int(sample_rate))))
    texture = np.zeros(frames, dtype=np.float64)
    harmonics = harmonic_frequencies(float(root_hz), float(start_hz), float(end_hz))
    if not harmonics:
        harmonics = list(np.linspace(float(start_hz), float(end_hz), max(1, int(voices))))
    span_hz = max(40.0, min(420.0, (float(end_hz) - float(start_hz)) / max(4.0, float(voices))))
    voice_count = max(1, int(voices))
    for index in range(voice_count):
        center = harmonics[index % len(harmonics)]
        direction = -1.0 if index % 2 else 1.0
        f0 = clamp(center - direction * span_hz * 0.5, float(start_hz), float(end_hz))
        f1 = clamp(center + direction * span_hz * 0.5, float(start_hz), float(end_hz))
        voice_frames = max(8, min(frames, int(round(frames * 0.38))))
        offset = int(((index * 0.61803398875) % 1.0) * max(1, frames - voice_frames))
        t = np.arange(voice_frames, dtype=np.float64) / float(sample_rate)
        chirp = signal.chirp(t, f0=f0, f1=f1, t1=voice_frames / float(sample_rate), method="linear")
        chirp *= signal.windows.tukey(voice_frames, alpha=0.7)
        chirp *= 0.55 + 0.45 * ((index % 5) / 4.0)
        texture[offset : offset + voice_frames] += chirp
    peak = float(np.max(np.abs(texture)))
    if peak > 0.0:
        texture = texture / peak
    texture *= db_to_amp(level_dbfs)
    out = np.zeros((frames, int(channels)), dtype=np.float32)
    out[:, int(channel)] = texture.astype(np.float32)
    return out


def harmonic_frequencies(root_hz: float, start_hz: float, end_hz: float) -> list[float]:
    if root_hz <= 0.0:
        raise ValueError("root_hz must be positive")
    first = max(1, int(math.ceil(start_hz / root_hz)))
    last = int(math.floor(end_hz / root_hz))
    return [root_hz * harmonic for harmonic in range(first, last + 1)]


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


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
