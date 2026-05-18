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


def read_last_jsonl(path: Path):
    if not path.exists():
        return None
    lines = [line for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    if not lines:
        return None
    return json.loads(lines[-1])


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
    elif summary["type"] == "localcast.audio.mic_field":
        summary.update(
            {
                "frameId": payload[1],
                "audioTimeNs": payload[3],
                "sampleRate": payload[4],
                "startSample": payload[5],
                "frameCount": payload[6],
                "channels": payload[7] if len(payload) > 7 else [],
                "graphId": payload[9] if len(payload) > 9 else "",
            }
        )
    elif summary["type"] in ("localcast.visual.stream_status", "localcast.audio.stream_status"):
        summary["payloadHead"] = head
    elif summary["type"] == "localcast.calibration.clap_events":
        summary.update(
            {
                "frameId": payload[1],
                "eventCount": len(payload[3]) if len(payload) > 3 else 0,
                "latestEvent": None
                if len(payload) <= 3 or not payload[3]
                else {
                    "stableKey": payload[3][-1][0],
                    "positionM": payload[3][-1][1],
                    "acousticOracleNs": payload[3][-1][2],
                    "visualObservedNs": payload[3][-1][3],
                    "timingUncertaintyUs": payload[3][-1][4],
                    "cameraCount": len(payload[3][-1][7]) if len(payload[3][-1]) > 7 else 0,
                },
            }
        )
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
            "audio-mic-field.msgpack",
            "clap-events.msgpack",
            "active-probes/active-probes.jsonl",
            "visual-lod-cache.json",
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
        "micFieldHead": msgpack_head(runs / "audio-mic-field.msgpack"),
        "clapHead": msgpack_head(runs / "clap-events.msgpack"),
        "lastActiveProbe": read_last_jsonl(runs / "active-probes" / "active-probes.jsonl"),
    }
    print(json.dumps(payload, indent=2, default=str))


if __name__ == "__main__":
    main()
