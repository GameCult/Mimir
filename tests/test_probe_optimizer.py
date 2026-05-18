import unittest

from audio_field.probe_optimizer import ActiveProbeOptimizer, ProbePolicy
from audio_field.runtime_sync import SyncState


class ActiveProbeOptimizerTests(unittest.TestCase):
    def test_schedules_weakest_source_when_masked(self):
        optimizer = ActiveProbeOptimizer(ProbePolicy(trigger_confidence=0.4, min_interval_frames=10))

        request = optimizer.choose_probe(
            100,
            {
                "good": SyncState(confidence=0.8),
                "weak": SyncState(confidence=0.2),
            },
            masked_window=True,
        )

        self.assertIsNotNone(request)
        self.assertEqual("weak", request.source_id)
        self.assertGreater(request.urgency, 0.0)

    def test_respects_probe_spacing(self):
        optimizer = ActiveProbeOptimizer(ProbePolicy(trigger_confidence=0.4, min_interval_frames=10))
        states = {"weak": SyncState(confidence=0.2)}

        self.assertIsNotNone(optimizer.choose_probe(100, states, masked_window=True))
        self.assertIsNone(optimizer.choose_probe(105, states, masked_window=True))

    def test_waits_for_masked_window_when_configured(self):
        optimizer = ActiveProbeOptimizer(ProbePolicy(trigger_confidence=0.4, prefer_masked_windows=True))

        request = optimizer.choose_probe(100, {"weak": SyncState(confidence=0.2)}, masked_window=False)

        self.assertIsNone(request)


if __name__ == "__main__":
    unittest.main()
