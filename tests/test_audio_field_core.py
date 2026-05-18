import unittest

import numpy as np

from audio_field.buffering import AudioChunk, FieldAssemblyCache, SourceBuffer
from audio_field.latency import LatencyPolicy, RealtimeConvergence
from audio_field.pipeline import AudioFieldPipeline


class IdentityAligner:
    def align(self, chunk):
        return chunk


class IdentityEncoder:
    def encode(self, aligned_field):
        return aligned_field


class ListSink:
    def __init__(self):
        self.writes = []

    def write(self, start_sample, field):
        self.writes.append((start_sample, field.copy()))


class ListSource:
    def __init__(self, source_id, chunks):
        self.source_id = source_id
        self.chunks = list(chunks)

    def read(self):
        if not self.chunks:
            return None
        return self.chunks.pop(0)


class BufferingTests(unittest.TestCase):
    def test_source_buffer_fills_gaps_with_silence(self):
        buffer = SourceBuffer("a", 48000)
        buffer.push(AudioChunk("a", 0, 48000, np.array([1.0, 2.0], dtype=np.float32)))
        buffer.push(AudioChunk("a", 4, 48000, np.array([5.0], dtype=np.float32)))

        np.testing.assert_allclose(buffer.read_window(0, 5), np.array([1.0, 2.0, 0.0, 0.0, 5.0]))

    def test_field_cache_assembles_field_channel_order(self):
        cache = FieldAssemblyCache([(1, "b"), (0, "a")], 48000)
        cache.push(AudioChunk("a", 0, 48000, np.array([1.0, 2.0], dtype=np.float32)))
        cache.push(AudioChunk("b", 0, 48000, np.array([3.0, 4.0], dtype=np.float32)))

        field = cache.assemble(0, 2)
        np.testing.assert_allclose(field, np.array([[1.0, 3.0], [2.0, 4.0]], dtype=np.float32))


class LatencyTests(unittest.TestCase):
    def test_convergence_emits_blocks_behind_live_edge(self):
        policy = LatencyPolicy(sample_rate=48000, target_latency_ms=100.0, max_latency_ms=500.0, block_size=480)
        convergence = RealtimeConvergence(policy)

        starts = convergence.ready_start_samples({"a": 4800 + 960, "b": 4800 + 960})

        self.assertEqual(starts, [0, 480])
        self.assertFalse(convergence.is_lagging({"a": 4800 + 960, "b": 4800 + 960}))


class PipelineTests(unittest.TestCase):
    def test_pipeline_uses_injected_ports(self):
        chunks_a = [AudioChunk("a", 0, 48000, np.ones(480, dtype=np.float32))]
        chunks_b = [AudioChunk("b", 0, 48000, np.ones(480, dtype=np.float32) * 2)]
        cache = FieldAssemblyCache([(0, "a"), (1, "b")], 48000)
        convergence = RealtimeConvergence(
            LatencyPolicy(sample_rate=48000, target_latency_ms=0.0, max_latency_ms=100.0, block_size=480)
        )
        sink = ListSink()
        pipeline = AudioFieldPipeline(
            sources=[ListSource("a", chunks_a), ListSource("b", chunks_b)],
            aligner=IdentityAligner(),
            cache=cache,
            convergence=convergence,
            encoder=IdentityEncoder(),
            sink=sink,
        )

        self.assertEqual(pipeline.poll_once(), 1)
        self.assertEqual(len(sink.writes), 1)
        self.assertEqual(sink.writes[0][0], 0)
        self.assertEqual(sink.writes[0][1].shape, (480, 2))
        np.testing.assert_allclose(sink.writes[0][1][0], np.array([1.0, 2.0], dtype=np.float32))


if __name__ == "__main__":
    unittest.main()
