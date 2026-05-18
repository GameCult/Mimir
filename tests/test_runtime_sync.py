import unittest

from audio_field.runtime_sync import ChirpletObservation, RuntimeSyncEstimator


class RuntimeSyncTests(unittest.TestCase):
    def test_low_score_freezes_previous_state(self):
        estimator = RuntimeSyncEstimator(min_score=0.5, smoothing=0.5)
        estimator.update(ChirpletObservation("mic", 1, 100.0, 0.25, 0.9, 1.0001))

        state = estimator.update(ChirpletObservation("mic", 2, 500.0, 2.0, 0.1, 1.2))

        self.assertEqual(100.0, state.delay_samples)
        self.assertEqual(0.25, state.phase_radians)
        self.assertEqual(1.0001, state.rate_scale)
        self.assertEqual(0.1, state.confidence)
        self.assertEqual(1, state.last_frame_index)

    def test_high_score_smooths_delay_phase_and_rate(self):
        estimator = RuntimeSyncEstimator(min_score=0.1, smoothing=0.25)
        estimator.update(ChirpletObservation("mic", 1, 100.0, 0.0, 0.9, 1.0))

        state = estimator.update(ChirpletObservation("mic", 2, 108.0, 0.4, 0.8, 1.0004))

        self.assertAlmostEqual(102.0, state.delay_samples)
        self.assertAlmostEqual(0.1, state.phase_radians)
        self.assertAlmostEqual(1.0001, state.rate_scale)
        self.assertEqual(2, state.last_frame_index)


if __name__ == "__main__":
    unittest.main()
