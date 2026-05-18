from __future__ import annotations

from typing import Protocol

import numpy as np

from audio_field.buffering import AudioChunk


class CaptureSource(Protocol):
    source_id: str

    def read(self) -> AudioChunk | None:
        """Return the next raw chunk, or None when no data is currently available."""


class ClockAligner(Protocol):
    def align(self, chunk: AudioChunk) -> AudioChunk:
        """Map a source chunk into the shared field timeline."""


class FieldEncoder(Protocol):
    def encode(self, aligned_field: np.ndarray) -> np.ndarray:
        """Encode an aligned field frame into the output representation."""


class FieldSink(Protocol):
    def write(self, start_sample: int, field: np.ndarray) -> None:
        """Write one emitted field block."""
