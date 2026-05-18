import unittest

import numpy as np

from audio_field.room_suppression import RoomSuppressionConfig, suppress_room_field


class RoomSuppressionTests(unittest.TestCase):
    def test_witness_only_click_is_suppressed_without_killing_anchor(self):
        field = np.zeros((4096, 3), dtype=np.float32)
        t = np.arange(4096, dtype=np.float32) / 48000.0
        field[:, 0] = 0.05 * np.sin(2 * np.pi * 220 * t)
        field[1600:1604, 1] = 1.0
        field[1600:1604, 2] = -0.8

        cleaned, report = suppress_room_field(
            field,
            anchor_channels=[0],
            witness_channels=[1, 2],
            config=RoomSuppressionConfig(block_size=512, hop_size=256, transient_ratio=2.0, max_witness_attenuation_db=-24.0),
        )

        self.assertGreater(report.transient_blocks, 0)
        self.assertLess(float(np.max(np.abs(cleaned[1600:1604, 1]))), 0.2)
        self.assertGreater(float(np.max(np.abs(cleaned[:, 0]))), 0.03)

    def test_no_witnesses_returns_copy(self):
        field = np.ones((16, 2), dtype=np.float32)

        cleaned, report = suppress_room_field(field, anchor_channels=[0], witness_channels=[])

        np.testing.assert_allclose(cleaned, field)
        self.assertEqual(0, report.transient_blocks)


if __name__ == "__main__":
    unittest.main()
