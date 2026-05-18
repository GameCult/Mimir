from __future__ import annotations

from dataclasses import dataclass

import numpy as np


@dataclass(frozen=True)
class AudioChunk:
    source_id: str
    start_sample: int
    sample_rate: int
    samples: np.ndarray

    @property
    def end_sample(self) -> int:
        return self.start_sample + int(self.samples.shape[0])


class SourceBuffer:
    """Append-only sample buffer with timeline-indexed reads."""

    def __init__(self, source_id: str, sample_rate: int):
        self.source_id = source_id
        self.sample_rate = sample_rate
        self._start_sample = 0
        self._samples = np.empty((0,), dtype=np.float32)
        self._initialized = False

    @property
    def start_sample(self) -> int:
        return self._start_sample

    @property
    def end_sample(self) -> int:
        return self._start_sample + int(self._samples.shape[0])

    def push(self, chunk: AudioChunk) -> None:
        if chunk.source_id != self.source_id:
            raise ValueError(f"chunk source {chunk.source_id!r} does not match buffer {self.source_id!r}")
        if chunk.sample_rate != self.sample_rate:
            raise ValueError(f"chunk sample rate {chunk.sample_rate} does not match buffer {self.sample_rate}")
        samples = np.asarray(chunk.samples, dtype=np.float32).reshape(-1)
        if not self._initialized:
            self._start_sample = chunk.start_sample
            self._samples = samples.copy()
            self._initialized = True
            return
        if chunk.start_sample < self.end_sample:
            raise ValueError(f"overlapping chunk for {self.source_id}: {chunk.start_sample} < {self.end_sample}")
        gap = chunk.start_sample - self.end_sample
        if gap:
            self._samples = np.concatenate([self._samples, np.zeros(gap, dtype=np.float32), samples])
            return
        self._samples = np.concatenate([self._samples, samples])

    def has_window(self, start_sample: int, frame_count: int) -> bool:
        end_sample = start_sample + frame_count
        return self._initialized and self.start_sample <= start_sample and self.end_sample >= end_sample

    def read_window(self, start_sample: int, frame_count: int) -> np.ndarray:
        if not self.has_window(start_sample, frame_count):
            raise ValueError(f"{self.source_id} does not cover [{start_sample}, {start_sample + frame_count})")
        offset = start_sample - self.start_sample
        return self._samples[offset : offset + frame_count].copy()

    def trim_before(self, sample_index: int) -> None:
        if not self._initialized or sample_index <= self.start_sample:
            return
        keep_from = min(sample_index, self.end_sample) - self.start_sample
        self._samples = self._samples[keep_from:]
        self._start_sample += keep_from


class FieldAssemblyCache:
    """Caches aligned mono sources and assembles ordered multichannel windows."""

    def __init__(self, field_order: list[tuple[int, str]], sample_rate: int):
        self.field_order = sorted(field_order)
        self.buffers = {
            source_id: SourceBuffer(source_id=source_id, sample_rate=sample_rate)
            for _, source_id in self.field_order
        }

    def push(self, chunk: AudioChunk) -> None:
        self.buffers[chunk.source_id].push(chunk)

    def has_window(self, start_sample: int, frame_count: int) -> bool:
        return all(buffer.has_window(start_sample, frame_count) for buffer in self.buffers.values())

    def assemble(self, start_sample: int, frame_count: int) -> np.ndarray:
        if not self.has_window(start_sample, frame_count):
            raise ValueError(f"field cache does not cover [{start_sample}, {start_sample + frame_count})")
        channels = [
            self.buffers[source_id].read_window(start_sample, frame_count)
            for _, source_id in self.field_order
        ]
        return np.stack(channels, axis=1)

    def trim_before(self, sample_index: int) -> None:
        for buffer in self.buffers.values():
            buffer.trim_before(sample_index)
