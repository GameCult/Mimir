import json
import tempfile
from pathlib import Path
import unittest

from localcast.obs_smoke import EndpointPlan, load_config_endpoints, summarize_events, write_event_template, read_events_jsonl


class ObsSmokeTests(unittest.TestCase):
    def test_loads_endpoint_plan_from_localcast_config(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "localcast.json"
            path.write_text(
                json.dumps(
                    {
                        "receiver": {"basePort": 5100, "srtLatencyMicros": 120000},
                        "video": {"enabled": True, "portOffset": 0},
                        "audioSources": [{"name": "voice", "portOffset": 2}],
                    }
                ),
                encoding="utf-8",
            )

            endpoints = load_config_endpoints(path)

        self.assertEqual([item.name for item in endpoints], ["video", "audio-voice"])
        self.assertEqual(endpoints[0].port, 5100)
        self.assertEqual(endpoints[1].listener_url, "srt://0.0.0.0:5102?mode=listener&latency=120000&timeout=5000000")

    def test_summarizes_latency_drift_and_confidence(self):
        endpoint = EndpointPlan(
            name="video",
            kind="video",
            port=5100,
            listener_url="srt://0.0.0.0:5100?mode=listener&latency=120000&timeout=5000000",
            expected_latency_ms=120.0,
        )
        events = [
            {"endpoint": "video", "stage": "sender_capture", "eventId": "a", "monotonicNs": 1_000_000_000, "mediaTimeNs": 0},
            {"endpoint": "video", "stage": "srt_receive", "eventId": "a", "monotonicNs": 1_120_000_000, "mediaTimeNs": 120_000_000},
            {"endpoint": "video", "stage": "obs_present", "eventId": "a", "monotonicNs": 1_180_000_000, "mediaTimeNs": 180_000_000},
            {"endpoint": "video", "stage": "sender_capture", "eventId": "b", "monotonicNs": 2_000_000_000, "mediaTimeNs": 1_000_000_000},
            {"endpoint": "video", "stage": "srt_receive", "eventId": "b", "monotonicNs": 2_120_000_000, "mediaTimeNs": 1_120_000_000},
            {"endpoint": "video", "stage": "obs_present", "eventId": "b", "monotonicNs": 2_180_000_000, "mediaTimeNs": 1_180_000_000},
        ]

        ledger = summarize_events([endpoint], [read_events_jsonl_item(item) for item in events])
        summary = ledger["endpoints"][0]

        self.assertEqual(ledger["status"], "ok")
        self.assertEqual(summary["status"], "ok")
        self.assertEqual(summary["endToEndLatencyMs"]["median"], 180.0)
        self.assertEqual(summary["endpointDriftPpm"], 0.0)
        self.assertGreaterEqual(summary["confidence"], 0.8)

    def test_missing_obs_stage_blocks_gate(self):
        endpoint = EndpointPlan(
            name="audio-voice",
            kind="audio",
            port=5102,
            listener_url="srt://0.0.0.0:5102?mode=listener",
            expected_latency_ms=120.0,
        )

        ledger = summarize_events(
            [endpoint],
            [
                read_events_jsonl_item(
                    {
                        "endpoint": "audio-voice",
                        "stage": "sender_capture",
                        "eventId": "a",
                        "monotonicNs": 1_000_000_000,
                    }
                ),
                read_events_jsonl_item(
                    {
                        "endpoint": "audio-voice",
                        "stage": "srt_receive",
                        "eventId": "a",
                        "monotonicNs": 1_120_000_000,
                    }
                ),
            ],
        )

        self.assertEqual(ledger["status"], "fail")
        self.assertEqual(ledger["blockedEndpoints"], ["audio-voice"])
        self.assertFalse(ledger["gate"]["pluginOrNativeReceiverExpansionAllowed"])

    def test_writes_event_template_as_jsonl(self):
        endpoint = EndpointPlan("video", "video", 5100, "srt://0.0.0.0:5100?mode=listener", 120.0)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "events.jsonl"
            write_event_template(path, [endpoint])
            events = read_events_jsonl(path)

        self.assertEqual(len(events), 3)
        self.assertEqual({event.stage for event in events}, {"sender_capture", "srt_receive", "obs_present"})


def read_events_jsonl_item(item):
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "events.jsonl"
        path.write_text(json.dumps(item) + "\n", encoding="utf-8")
        return read_events_jsonl(path)[0]


if __name__ == "__main__":
    unittest.main()
