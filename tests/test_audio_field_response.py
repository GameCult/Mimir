import importlib.util
import unittest
from pathlib import Path

import numpy as np


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "audio_field.py"
spec = importlib.util.spec_from_file_location("audio_field_script", SCRIPT)
audio_field_script = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audio_field_script)


class ResponseCompensationTests(unittest.TestCase):
    def test_frequency_response_applies_gain_curve_without_length_change(self):
        sample_rate = 48000
        t = np.arange(sample_rate, dtype=np.float32) / sample_rate
        low = np.sin(2.0 * np.pi * 200.0 * t)
        high = np.sin(2.0 * np.pi * 6000.0 * t) * 0.1
        samples = (low + high).astype(np.float32)

        bins = np.fft.rfftfreq(len(samples), 1.0 / sample_rate)
        gain = np.ones_like(bins, dtype=np.float32)
        gain[bins > 3000.0] = 4.0

        corrected = audio_field_script.apply_frequency_response(samples, gain)

        self.assertEqual(samples.shape, corrected.shape)
        before = np.abs(np.fft.rfft(samples))
        after = np.abs(np.fft.rfft(corrected))
        low_bin = int(np.argmin(np.abs(bins - 200.0)))
        high_bin = int(np.argmin(np.abs(bins - 6000.0)))
        self.assertGreater(after[high_bin] / before[high_bin], 3.5)
        self.assertAlmostEqual(after[low_bin] / before[low_bin], 1.0, places=1)

    def test_extract_window_pads_negative_start(self):
        data = np.array([1.0, 2.0, 3.0], dtype=np.float32)
        window = audio_field_script.extract_window(data, -2, 5)

        np.testing.assert_allclose(window, np.array([0.0, 0.0, 1.0, 2.0, 3.0], dtype=np.float32))


if __name__ == "__main__":
    unittest.main()
