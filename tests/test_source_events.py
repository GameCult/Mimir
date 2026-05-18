import unittest

import numpy as np

from audio_field.source_events import analyze_source_field


class SourceEventTests(unittest.TestCase):
    def test_witness_transient_becomes_positioned_event(self):
        field = np.zeros((4096, 4), dtype=np.float32)
        field[1024:1280, 2] = 0.8
        field[1024:1280, 3] = 0.2
        positions = {
            0: (0.0, 0.0, 1.2),
            1: (0.0, 2.0, 1.2),
            2: (-1.0, 0.0, 1.0),
            3: (1.0, 0.0, 1.0),
        }

        events, focus = analyze_source_field(
            field,
            48000,
            positions,
            anchor_channels=[0, 1],
            witness_channels=[2, 3],
            block_size=512,
            hop_size=256,
            transient_ratio=2.0,
        )

        self.assertTrue(events)
        strongest = max(events, key=lambda event: event.energy)
        self.assertLess(strongest.position_m[0], -0.5)
        self.assertEqual("witness-dominant-transient", strongest.kind)
        self.assertGreater(strongest.confidence, 0.5)
        self.assertTrue(focus)

    def test_anchor_weights_follow_voice_energy(self):
        field = np.zeros((2048, 3), dtype=np.float32)
        field[:, 0] = 0.2
        field[:, 1] = 0.6
        positions = {0: (0, 0, 1), 1: (0, 2, 1), 2: (1, 1, 1)}

        _, focus = analyze_source_field(
            field,
            48000,
            positions,
            anchor_channels=[0, 1],
            witness_channels=[2],
            block_size=512,
            hop_size=512,
        )

        self.assertGreater(focus[0].anchor_weights[1], focus[0].anchor_weights[0])
        self.assertLess(focus[0].noise_ratio, 0.01)

    def test_steady_witness_texture_does_not_become_event_spam(self):
        field = np.zeros((4096, 2), dtype=np.float32)
        field[:, 1] = 0.2
        positions = {0: (0, 0, 1), 1: (1, 0, 1)}

        events, _ = analyze_source_field(
            field,
            48000,
            positions,
            anchor_channels=[0],
            witness_channels=[1],
            block_size=512,
            hop_size=256,
            transient_ratio=2.0,
        )

        self.assertLessEqual(len(events), 1)


if __name__ == "__main__":
    unittest.main()
