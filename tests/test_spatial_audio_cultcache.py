import tempfile
import unittest
from pathlib import Path

import numpy as np

from audio_field.cultcache_audio import (
    SPATIAL_AUDIO_SCHEMA_ID,
    frame_to_numpy,
    get_live_spatial_audio_frame,
    make_spatial_audio_frame,
    put_live_spatial_audio_frame,
)


class SpatialAudioCultCacheTests(unittest.TestCase):
    def test_spatial_audio_frame_round_trips_float32_ambix_block(self):
        block = np.arange(32, dtype=np.float32).reshape(8, 4) / 32.0
        frame = make_spatial_audio_frame(
            block,
            frame_id=12,
            sample_rate=48000,
            start_sample=1024,
            audio_time_ns=50_000,
        )

        self.assertEqual(SPATIAL_AUDIO_SCHEMA_ID, frame.schema_version)
        self.assertEqual(("W", "Y", "Z", "X"), frame.channels)
        self.assertEqual("ACN", frame.channel_order)
        self.assertEqual("SN3D", frame.normalization)
        np.testing.assert_allclose(block, frame_to_numpy(frame))

    def test_live_spatial_audio_frame_round_trips_through_cultcache(self):
        block = np.ones((16, 4), dtype=np.float32)
        frame = make_spatial_audio_frame(block, frame_id=3, sample_rate=48000, start_sample=2048, audio_time_ns=90_000)

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "audio-state.msgpack"
            put_live_spatial_audio_frame(path, frame)
            loaded = get_live_spatial_audio_frame(path)

        self.assertIsNotNone(loaded)
        self.assertEqual(3, loaded.frame_id)
        self.assertEqual(2048, loaded.start_sample)
        np.testing.assert_allclose(block, frame_to_numpy(loaded))


if __name__ == "__main__":
    unittest.main()
