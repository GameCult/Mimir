import json
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))


def read_json(path: Path):
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def msgpack_head(path: Path):
    if not path.exists():
        return None
    import msgpack

    outer = msgpack.unpackb(path.read_bytes(), raw=False)
    if not outer:
        return None
    payload = msgpack.unpackb(outer[0]["payload"], raw=False)
    head = payload[:8] if isinstance(payload, list) else payload
    summary = {
        "key": outer[0].get("key"),
        "type": outer[0].get("type"),
        "storedAt": outer[0].get("storedAt"),
    }
    if not isinstance(payload, list):
        summary["payload"] = str(payload)[:120]
        return summary
    summary["schema"] = payload[0] if payload else None
    if summary["type"] == "localcast.visual.render_frame":
        summary.update(
            {
                "frameId": payload[1],
                "presentTimeNs": payload[5],
                "audioAlignmentTimeNs": payload[6],
                "pointCount": len(payload[10]) if len(payload) > 10 else 0,
            }
        )
    elif summary["type"] == "localcast.audio.spatial_frame":
        summary.update(
            {
                "frameId": payload[1],
                "audioTimeNs": payload[3],
                "sampleRate": payload[4],
                "startSample": payload[5],
                "frameCount": payload[6],
                "channels": payload[9] if len(payload) > 9 else [],
            }
        )
    elif summary["type"] == "localcast.audio.source_events":
        summary.update(
            {
                "frameId": payload[1],
                "sampleRate": payload[3],
                "startSample": payload[4],
                "frameCount": payload[5],
                "eventCount": len(payload[6]) if len(payload) > 6 else 0,
                "focusCount": len(payload[7]) if len(payload) > 7 else 0,
            }
        )
    elif summary["type"] == "localcast.audio.phase_field":
        summary.update(
            {
                "frameId": payload[1],
                "sampleRate": payload[3],
                "startSample": payload[4],
                "frameCount": payload[5],
                "referenceId": payload[6],
                "sourceCount": len(payload[7]) if len(payload) > 7 else 0,
                "globalConfidence": payload[8] if len(payload) > 8 else 0.0,
                "needsActiveProbe": payload[9] if len(payload) > 9 else True,
            }
        )
    elif summary["type"] in ("localcast.visual.stream_status", "localcast.audio.stream_status"):
        summary["payloadHead"] = head
    return summary


def file_freshness(path: Path, now: float) -> dict:
    if not path.exists():
        return {"exists": False}
    age = max(0.0, now - path.stat().st_mtime)
    return {"exists": True, "ageSeconds": age, "bytes": path.stat().st_size}


def main() -> None:
    runs = ROOT / "calibration" / "runs"
    now = time.time()
    files = {
        name: file_freshness(runs / name, now)
        for name in [
            "visual-state.msgpack",
            "audio-state.msgpack",
            "audio-events.msgpack",
            "visual-stream-status.msgpack",
            "audio-stream-status.msgpack",
            "audio-phase-field.msgpack",
            "stream-spout-status.json",
            "av-sync-status.json",
        ]
    }
    payload = {
        "createdWallTime": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "files": files,
        "avSync": read_json(runs / "av-sync-status.json"),
        "spout": read_json(runs / "stream-spout-status.json"),
        "visualHead": msgpack_head(runs / "visual-state.msgpack"),
        "audioHead": msgpack_head(runs / "audio-state.msgpack"),
        "eventsHead": msgpack_head(runs / "audio-events.msgpack"),
        "phaseFieldHead": msgpack_head(runs / "audio-phase-field.msgpack"),
    }
    print(json.dumps(payload, indent=2, default=str))


if __name__ == "__main__":
    main()
