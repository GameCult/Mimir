import tempfile
import unittest
from pathlib import Path

import numpy as np

from audio_field.active_probe import ActiveConfidenceMaintainer, is_masked_window, make_probe_chirplet
from audio_field.phase_meaning import PhaseFieldMeaning, SourcePhaseMeaning
from audio_field.probe_optimizer import ActiveProbeOptimizer, ProbePolicy
from scripts.stream_phase_field import ultrasonic_probe_band


class ActiveProbeTests(unittest.TestCase):
    def test_low_confidence_phase_meaning_emits_probe_manifest(self):
        meaning = PhaseFieldMeaning(
            frame_id=10,
            audio_time_ns=1,
            sample_rate=48000,
            start_sample=0,
            frame_count=1024,
            reference_id="program",
            sources=(
                SourcePhaseMeaning("good", 0, 0.0, 0.0, 0.0, 0.0, 0.9, 0.0, 0.8, 0.1, 0.0, 0.0),
                SourcePhaseMeaning("weak", 1, 0.0, 0.0, 0.0, 0.0, 0.2, 1.0, 0.1, 0.1, 0.0, 0.0),
            ),
            global_confidence=0.45,
            needs_active_probe=True,
            active_probe_reason="weak",
        )
        reference = np.full(1024, 0.05, dtype=np.float32)

        with tempfile.TemporaryDirectory() as tmp:
            maintainer = ActiveConfidenceMaintainer(
                ActiveProbeOptimizer(ProbePolicy(trigger_confidence=0.35, min_interval_frames=1)),
                sample_rate=48000,
                output_dir=Path(tmp),
            )
            emitted = maintainer.update(meaning, reference, force_masked=True)

            self.assertIsNotNone(emitted)
            self.assertEqual("weak", emitted.request.source_id)
            self.assertTrue(emitted.path.exists())
            self.assertTrue((Path(tmp) / "active-probes.jsonl").exists())

    def test_probe_chirplet_respects_level_and_channel(self):
        chirp = make_probe_chirplet(48000, duration_seconds=0.02, start_hz=1000, end_hz=4000, level_dbfs=-20, channels=2, channel=1)

        self.assertEqual((960, 2), chirp.shape)
        self.assertAlmostEqual(0.0, float(np.max(np.abs(chirp[:, 0]))), delta=1e-9)
        self.assertLessEqual(float(np.max(np.abs(chirp[:, 1]))), 0.101)

    def test_masked_window_requires_enough_reference_energy(self):
        self.assertFalse(is_masked_window(np.zeros(1024, dtype=np.float32)))
        self.assertTrue(is_masked_window(np.full(1024, 0.05, dtype=np.float32)))

    def test_ultrasonic_probe_band_stays_below_nyquist(self):
        start_hz, end_hz = ultrasonic_probe_band(48000)

        self.assertGreaterEqual(start_hz, 18000.0)
        self.assertLess(end_hz, 24000.0)


if __name__ == "__main__":
    unittest.main()
