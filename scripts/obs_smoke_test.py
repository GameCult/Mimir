import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.obs_smoke import load_config_endpoints, read_events_jsonl, summarize_events, write_event_template


def main() -> None:
    parser = argparse.ArgumentParser(description="Create the bounded OBS v1 smoke-test witness ledger.")
    parser.add_argument("--config", default="config/localcast.json")
    parser.add_argument("--events", default="calibration/runs/obs-v1-smoke-events.jsonl")
    parser.add_argument("--ledger", default="calibration/runs/obs-v1-smoke-ledger.json")
    parser.add_argument("--max-abs-drift-ppm", type=float, default=100.0)
    parser.add_argument("--max-latency-ms", type=float, default=500.0)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("plan", help="Print planned OBS-facing endpoints from the Mimir config.")
    sub.add_parser("template-events", help="Write an editable JSONL event template for one smoke pass.")
    sub.add_parser("summarize", help="Summarize recorded events into the smoke-test ledger.")
    args = parser.parse_args()

    config_path = Path(args.config)
    endpoints = load_config_endpoints(config_path)

    if args.command == "plan":
        print(
            json.dumps(
                {
                    "config": str(config_path),
                    "endpoints": [endpoint.__dict__ for endpoint in endpoints],
                    "requiredStages": ["sender_capture", "srt_receive", "obs_present"],
                },
                indent=2,
            )
        )
        return

    events_path = Path(args.events)
    if args.command == "template-events":
        write_event_template(events_path, endpoints)
        print(f"Wrote smoke event template: {events_path}")
        return

    ledger_path = Path(args.ledger)
    events = read_events_jsonl(events_path)
    ledger = summarize_events(
        endpoints,
        events,
        max_abs_drift_ppm=args.max_abs_drift_ppm,
        max_latency_ms=args.max_latency_ms,
    )
    ledger_path.parent.mkdir(parents=True, exist_ok=True)
    ledger_path.write_text(json.dumps(ledger, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(ledger, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
