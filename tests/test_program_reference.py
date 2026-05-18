import unittest

import numpy as np

from audio_field.program_reference import suppress_program_reference


class ProgramReferenceTests(unittest.TestCase):
    def test_reference_bleed_is_suppressed_from_channel(self):
        sample_rate = 48000
        t = np.arange(sample_rate, dtype=np.float32) / sample_rate
        reference = 0.2 * np.sin(2 * np.pi * 440 * t) + 0.1 * np.sin(2 * np.pi * 1400 * t)
        voice = 0.03 * np.sin(2 * np.pi * 220 * t)
        field = np.zeros((sample_rate, 2), dtype=np.float32)
        field[:, 0] = voice + 0.7 * reference
        field[:, 1] = voice

        cleaned, reports = suppress_program_reference(
            field,
            reference,
            sample_rate,
            channels=[0],
            nperseg=2048,
            noverlap=1536,
            subtraction_strength=1.0,
        )

        self.assertEqual((sample_rate, 2), cleaned.shape)
        self.assertLess(reports[0].output_rms, reports[0].input_rms)
        self.assertLess(float(np.sqrt(np.mean((cleaned[:, 0] - voice) ** 2))), 0.01)
        np.testing.assert_allclose(cleaned[:, 1], field[:, 1])
        self.assertGreater(len(reports[0].phase_mapping), 0)


if __name__ == "__main__":
    unittest.main()
