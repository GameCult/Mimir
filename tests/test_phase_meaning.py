import unittest

import numpy as np

from audio_field.phase_meaning import LivePhaseMeaningExtractor


class PhaseMeaningTests(unittest.TestCase):
    def test_extractor_turns_phase_evidence_into_actionable_state(self):
        sample_rate = 48000
        delay = 10
        t = np.arange(8192, dtype=np.float32) / sample_rate
        reference = (
            np.sin(2 * np.pi * 500 * t)
            + 0.7 * np.sin(2 * np.pi * 1000 * t)
            + 0.4 * np.sin(2 * np.pi * 2000 * t)
        ).astype(np.float32)
        observed = np.pad(reference, (delay, 0))[: len(reference)]
        field = np.stack([observed, 0.05 * np.sin(2 * np.pi * 300 * t).astype(np.float32)], axis=1)
        extractor = LivePhaseMeaningExtractor(("anchor", "witness"), sample_rate, [500, 1000, 2000])

        meaning = extractor.update(reference, field, frame_id=4, start_sample=2048, audio_time_ns=99)

        self.assertEqual(4, meaning.frame_id)
        self.assertEqual(2, len(meaning.sources))
        self.assertGreater(meaning.sources[0].confidence, meaning.sources[1].confidence)
        self.assertGreater(meaning.sources[0].suppression_weight, 0.2)
        self.assertAlmostEqual(delay, meaning.sources[0].delay_samples, delta=2.0)
        self.assertTrue(meaning.needs_active_probe)


if __name__ == "__main__":
    unittest.main()
