from __future__ import annotations

from audio_field.buffering import FieldAssemblyCache
from audio_field.latency import RealtimeConvergence
from audio_field.ports import CaptureSource, ClockAligner, FieldEncoder, FieldSink


class AudioFieldPipeline:
    """Small orchestration shell with injectable capture, alignment, encode, and sink ports."""

    def __init__(
        self,
        sources: list[CaptureSource],
        aligner: ClockAligner,
        cache: FieldAssemblyCache,
        convergence: RealtimeConvergence,
        encoder: FieldEncoder,
        sink: FieldSink,
    ):
        self.sources = sources
        self.aligner = aligner
        self.cache = cache
        self.convergence = convergence
        self.encoder = encoder
        self.sink = sink
        self.latest_by_source: dict[str, int] = {}

    def poll_once(self) -> int:
        for source in self.sources:
            chunk = source.read()
            if chunk is None:
                continue
            aligned = self.aligner.align(chunk)
            self.cache.push(aligned)
            self.latest_by_source[aligned.source_id] = aligned.end_sample

        emitted = 0
        for start_sample in self.convergence.ready_start_samples(self.latest_by_source):
            frame_count = self.convergence.policy.block_size
            if not self.cache.has_window(start_sample, frame_count):
                continue
            field = self.cache.assemble(start_sample, frame_count)
            encoded = self.encoder.encode(field)
            self.sink.write(start_sample, encoded)
            self.cache.trim_before(start_sample + frame_count)
            emitted += 1
        return emitted
