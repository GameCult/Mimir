import tempfile
import unittest
from pathlib import Path

import numpy as np

from audio_field.active_probe import ActiveConfidenceMaintainer, harmonic_frequencies, is_masked_window, make_harmonic_probe_texture, make_probe_chirplet
from audio_field.phase_meaning import PhaseFieldMeaning, SourcePhaseMeaning
from audio_field.probe_optimizer import ActiveProbeOptimizer, ProbePolicy
from audio_field.probe_bands import ultrasonic_probe_band


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

    def test_probe_artifacts_use_bounded_slots(self):
        reference = np.full(1024, 0.05, dtype=np.float32)

        with tempfile.TemporaryDirectory() as tmp:
            maintainer = ActiveConfidenceMaintainer(
                ActiveProbeOptimizer(ProbePolicy(trigger_confidence=0.35, min_interval_frames=1)),
                sample_rate=48000,
                output_dir=Path(tmp),
                max_artifacts=2,
            )
            paths = []
            for frame_id in range(5):
                meaning = PhaseFieldMeaning(
                    frame_id=frame_id,
                    audio_time_ns=frame_id,
                    sample_rate=48000,
                    start_sample=frame_id * 1024,
                    frame_count=1024,
                    reference_id="program",
                    sources=(SourcePhaseMeaning("weak", 0, 0.0, 0.0, 0.0, 0.0, 0.2, 1.0, 0.1, 0.1, 0.0, 0.0),),
                    global_confidence=0.2,
                    needs_active_probe=True,
                    active_probe_reason="weak",
                )
                emitted = maintainer.update(meaning, reference, force_masked=True)
                self.assertIsNotNone(emitted)
                paths.append(emitted.path.name)

            self.assertEqual(2, len(list(Path(tmp).glob("probe-slot-*.wav"))))
            self.assertEqual("probe-slot-0000-weak.wav", paths[0])
            self.assertEqual("probe-slot-0000-weak.wav", paths[-1])

    def test_probe_maintainer_ignores_ineligible_placeholder_sources(self):
        meaning = PhaseFieldMeaning(
            frame_id=10,
            audio_time_ns=1,
            sample_rate=48000,
            start_sample=0,
            frame_count=1024,
            reference_id="program",
            sources=(
                SourcePhaseMeaning("placeholder", 0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.1, 0.0, 0.0),
                SourcePhaseMeaning("live", 1, 0.0, 0.0, 0.0, 0.0, 0.6, 1.0, 0.6, 0.1, 0.0, 0.0),
            ),
            global_confidence=0.3,
            needs_active_probe=True,
            active_probe_reason="weak",
        )
        reference = np.full(1024, 0.05, dtype=np.float32)

        with tempfile.TemporaryDirectory() as tmp:
            maintainer = ActiveConfidenceMaintainer(
                ActiveProbeOptimizer(ProbePolicy(trigger_confidence=0.35, min_interval_frames=1)),
                sample_rate=48000,
                output_dir=Path(tmp),
                eligible_source_ids={"live"},
            )

            emitted = maintainer.update(meaning, reference, force_masked=True)

            self.assertIsNone(emitted)
            self.assertFalse((Path(tmp) / "active-probes.jsonl").exists())

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

    def test_harmonic_texture_packs_many_chirps_under_level_cap(self):
        texture = make_harmonic_probe_texture(
            48000,
            duration_seconds=0.08,
            start_hz=18500.0,
            end_hz=22000.0,
            level_dbfs=-18.0,
            voices=36,
        )

        self.assertEqual((3840, 2), texture.shape)
        self.assertLessEqual(float(np.max(np.abs(texture))), 0.127)
        self.assertGreater(float(np.sqrt(np.mean(texture[:, 0] ** 2))), 0.005)

    def test_harmonic_frequencies_follow_root(self):
        freqs = harmonic_frequencies(440.0, 18500.0, 22000.0)

        self.assertIn(18920.0, freqs)
        self.assertIn(22000.0, freqs)


if __name__ == "__main__":
    unittest.main()
