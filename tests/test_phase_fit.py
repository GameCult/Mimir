import unittest

import numpy as np

from audio_field.phase_fit import IterativeFrequencyPhaseMapper, SmoothPhaseField, estimate_phase_delay


class PhaseFitTests(unittest.TestCase):
    def test_phase_delay_estimates_fractional_shift(self):
        sample_rate = 48000
        delay_samples = 12
        t = np.arange(4096, dtype=np.float32) / sample_rate
        reference = (
            np.sin(2 * np.pi * 500 * t)
            + 0.7 * np.sin(2 * np.pi * 1000 * t)
            + 0.4 * np.sin(2 * np.pi * 2000 * t)
        ).astype(np.float32)
        observed = np.pad(reference, (delay_samples, 0))[: len(reference)]

        estimate = estimate_phase_delay(reference, observed, sample_rate, [500, 1000, 2000], max_abs_delay_ms=2.0)

        self.assertAlmostEqual(delay_samples, estimate.delay_samples, delta=2.0)
        self.assertEqual(3, len(estimate.bands))

    def test_smooth_phase_field_limits_steps(self):
        field = SmoothPhaseField(smoothing=1.0, max_step_samples=5.0)

        self.assertEqual(10.0, field.update("mic", 10.0, 1.0))
        self.assertEqual(15.0, field.update("mic", 30.0, 1.0))

    def test_iterative_mapper_learns_phase_correction(self):
        sample_rate = 48000
        t = np.arange(4096, dtype=np.float32) / sample_rate
        reference = np.sin(2 * np.pi * 1000 * t).astype(np.float32)
        observed = np.pad(reference, (8, 0))[: len(reference)]
        estimate = estimate_phase_delay(reference, observed, sample_rate, [500, 1000, 2000], max_abs_delay_ms=2.0)
        mapper = IterativeFrequencyPhaseMapper([500, 1000, 2000], sample_rate, learning_rate=0.5)

        correction = mapper.update("mic", estimate, confidence=0.8)

        self.assertEqual((3,), correction.shape)
        self.assertTrue(np.all(np.isfinite(correction)))


if __name__ == "__main__":
    unittest.main()
