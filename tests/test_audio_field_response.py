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

    def test_probe_train_alternates_speakers_and_stamps_events(self):
        profile = {
            "sampleRate": 48000,
            "calibration": {
                "sweepSeconds": 0.1,
                "sweepStartHz": 200.0,
                "sweepEndHz": 2000.0,
                "levelDbfs": -24.0,
            },
            "speakers": [
                {"id": "left", "channel": 0},
                {"id": "right", "channel": 1},
            ],
        }

        train, chirp, events = audio_field_script.make_probe_train(
            profile,
            48000,
            seconds=1.1,
            chirp_seconds=0.1,
            interval_seconds=0.25,
            channels=2,
            start_padding_seconds=0.1,
        )

        self.assertEqual((52800, 2), train.shape)
        self.assertEqual(4800, len(chirp))
        self.assertEqual(["left", "right", "left", "right"], [event["speakerId"] for event in events])
        self.assertGreater(float(np.max(np.abs(train[:, 0]))), 0.0)
        self.assertGreater(float(np.max(np.abs(train[:, 1]))), 0.0)

    def test_dense_probe_train_uses_many_band_limited_events(self):
        profile = {
            "sampleRate": 48000,
            "calibration": {
                "sweepSeconds": 0.03,
                "sweepStartHz": 200.0,
                "sweepEndHz": 2000.0,
                "levelDbfs": -24.0,
            },
            "speakers": [
                {"id": "left", "channel": 0},
                {"id": "right", "channel": 1},
            ],
        }

        train, _, events = audio_field_script.make_probe_train(
            profile,
            48000,
            seconds=1.0,
            chirp_seconds=0.03,
            interval_seconds=1.0,
            chirps_per_second=20,
            channels=2,
            start_padding_seconds=0.05,
            bands=[(200.0, 700.0), (900.0, 1800.0)],
            level_db_offset=-24.0,
        )

        self.assertGreaterEqual(len(events), 18)
        self.assertEqual({(200.0, 700.0), (900.0, 1800.0)}, {(e["sweepStartHz"], e["sweepEndHz"]) for e in events})
        self.assertLessEqual(float(np.max(np.abs(train))), 0.95)

    def test_probe_summary_reports_jitter_and_distance_equivalent(self):
        observations = [
            {"speakerId": "left", "eventIndex": 0, "delaySamplesFromLoopback": 48, "score": 0.5},
            {"speakerId": "left", "eventIndex": 1, "delaySamplesFromLoopback": 50, "score": 0.7},
            {"speakerId": "left", "eventIndex": 2, "delaySamplesFromLoopback": 52, "score": 0.6},
        ]

        summary = audio_field_script.summarize_probe_observations(observations, 48000, 343.0)

        self.assertEqual(1, len(summary))
        self.assertEqual("left", summary[0]["speakerId"])
        self.assertEqual(50.0, summary[0]["medianDelaySamples"])
        self.assertAlmostEqual(1000.0 * 50.0 / 48000.0, summary[0]["medianDelayMs"])
        self.assertAlmostEqual(343.0 * 50.0 / 48000.0, summary[0]["distanceEquivalentMeters"])

    def test_event_chirp_uses_event_band(self):
        profile = {
            "sampleRate": 48000,
            "calibration": {
                "sweepSeconds": 0.03,
                "sweepStartHz": 200.0,
                "sweepEndHz": 2000.0,
                "levelDbfs": -24.0,
            },
        }

        chirp = audio_field_script.make_event_chirp(
            profile,
            48000,
            {"chirpSeconds": 0.05, "sweepStartHz": 5000.0, "sweepEndHz": 9000.0},
        )

        self.assertEqual(2400, len(chirp))
        self.assertGreater(float(np.max(np.abs(chirp))), 0.0)


if __name__ == "__main__":
    unittest.main()
