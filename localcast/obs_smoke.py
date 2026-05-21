from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import json
from pathlib import Path
from statistics import median
from typing import Any, Iterable


NS_PER_MS = 1_000_000
NS_PER_SECOND = 1_000_000_000


@dataclass(frozen=True)
class EndpointPlan:
    name: str
    kind: str
    port: int
    listener_url: str
    expected_latency_ms: float


@dataclass(frozen=True)
class SmokeEvent:
    endpoint: str
    stage: str
    monotonic_ns: int
    wall_time_ns: int | None = None
    media_time_ns: int | None = None
    event_id: str | None = None
    confidence: float = 1.0
    note: str = ""


def load_config_endpoints(config_path: Path) -> list[EndpointPlan]:
    config = json.loads(config_path.read_text(encoding="utf-8-sig"))
    receiver = config.get("receiver", {})
    base_port = int(receiver.get("basePort", 5100))
    expected_latency_ms = float(receiver.get("srtLatencyMicros", 120000)) / 1000.0
    passphrase = str(receiver.get("passphrase", "") or "")
    secret_suffix = "&passphrase=SET&pbkeylen=16" if passphrase else ""

    endpoints: list[EndpointPlan] = []
    video = config.get("video") or {}
    if video.get("enabled", False):
        port = base_port + int(video.get("portOffset", 0))
        endpoints.append(
            EndpointPlan(
                name="video",
                kind="video",
                port=port,
                listener_url=f"srt://0.0.0.0:{port}?mode=listener&latency={int(expected_latency_ms * 1000)}&timeout=5000000{secret_suffix}",
                expected_latency_ms=expected_latency_ms,
            )
        )

    for audio in config.get("audioSources", []) or []:
        name = str(audio.get("name", "")).strip()
        if not name:
            continue
        port = base_port + int(audio.get("portOffset", 0))
        endpoints.append(
            EndpointPlan(
                name=f"audio-{name}",
                kind="audio",
                port=port,
                listener_url=f"srt://0.0.0.0:{port}?mode=listener&latency={int(expected_latency_ms * 1000)}&timeout=5000000{secret_suffix}",
                expected_latency_ms=expected_latency_ms,
            )
        )
    return endpoints


def event_from_json(raw: dict[str, Any]) -> SmokeEvent:
    return SmokeEvent(
        endpoint=str(raw["endpoint"]),
        stage=str(raw["stage"]),
        monotonic_ns=int(raw["monotonicNs"]),
        wall_time_ns=None if raw.get("wallTimeNs") is None else int(raw["wallTimeNs"]),
        media_time_ns=None if raw.get("mediaTimeNs") is None else int(raw["mediaTimeNs"]),
        event_id=None if raw.get("eventId") is None else str(raw["eventId"]),
        confidence=float(raw.get("confidence", 1.0)),
        note=str(raw.get("note", "")),
    )


def read_events_jsonl(path: Path) -> list[SmokeEvent]:
    events: list[SmokeEvent] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        try:
            events.append(event_from_json(json.loads(line)))
        except Exception as exc:  # pragma: no cover - message quality path
            raise ValueError(f"{path}:{line_number}: invalid smoke event: {exc}") from exc
    return events


def write_event_template(path: Path, endpoints: Iterable[EndpointPlan]) -> None:
    rows: list[dict[str, Any]] = []
    base = 1_000_000_000_000
    for index, endpoint in enumerate(endpoints):
        event_id = f"{endpoint.name}-flash-001" if endpoint.kind == "video" else f"{endpoint.name}-chirp-001"
        start = base + index * 10 * NS_PER_SECOND
        rows.extend(
            [
                {
                    "endpoint": endpoint.name,
                    "stage": "sender_capture",
                    "eventId": event_id,
                    "monotonicNs": start,
                    "wallTimeNs": None,
                    "mediaTimeNs": 0,
                    "confidence": 1.0,
                    "note": "Replace with sender-side flash/chirp capture timestamp.",
                },
                {
                    "endpoint": endpoint.name,
                    "stage": "srt_receive",
                    "eventId": event_id,
                    "monotonicNs": start + int(endpoint.expected_latency_ms * NS_PER_MS),
                    "wallTimeNs": None,
                    "mediaTimeNs": int(endpoint.expected_latency_ms * NS_PER_MS),
                    "confidence": 0.8,
                    "note": "Replace with first packet or probe observation at receiver.",
                },
                {
                    "endpoint": endpoint.name,
                    "stage": "obs_present",
                    "eventId": event_id,
                    "monotonicNs": start + int(endpoint.expected_latency_ms * NS_PER_MS * 1.5),
                    "wallTimeNs": None,
                    "mediaTimeNs": int(endpoint.expected_latency_ms * NS_PER_MS),
                    "confidence": 0.8,
                    "note": "Replace with OBS recording/preview observation.",
                },
            ]
        )
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(json.dumps(row, sort_keys=True) + "\n" for row in rows), encoding="utf-8")


def summarize_events(
    endpoints: Iterable[EndpointPlan],
    events: Iterable[SmokeEvent],
    *,
    max_abs_drift_ppm: float = 100.0,
    max_latency_ms: float = 500.0,
) -> dict[str, Any]:
    endpoint_plans = {endpoint.name: endpoint for endpoint in endpoints}
    events_by_endpoint: dict[str, list[SmokeEvent]] = {name: [] for name in endpoint_plans}
    for event in events:
        events_by_endpoint.setdefault(event.endpoint, []).append(event)

    summaries = []
    for name, endpoint in endpoint_plans.items():
        rows = sorted(events_by_endpoint.get(name, []), key=lambda item: item.monotonic_ns)
        stages = sorted({row.stage for row in rows})
        latencies_ms = _matched_latencies_ms(rows)
        drift_ppm = _endpoint_drift_ppm(rows)
        confidence = _endpoint_confidence(
            rows,
            latencies_ms=latencies_ms,
            drift_ppm=drift_ppm,
            max_abs_drift_ppm=max_abs_drift_ppm,
            max_latency_ms=max_latency_ms,
        )
        status = "ok" if confidence >= 0.8 else "suspect" if confidence >= 0.5 else "fail"
        summaries.append(
            {
                "endpoint": name,
                "kind": endpoint.kind,
                "port": endpoint.port,
                "listenerUrl": endpoint.listener_url,
                "expectedLatencyMs": endpoint.expected_latency_ms,
                "observedStages": stages,
                "eventCount": len(rows),
                "endToEndLatencyMs": _latency_summary(latencies_ms),
                "endpointDriftPpm": drift_ppm,
                "confidence": round(confidence, 3),
                "status": status,
            }
        )

    overall_confidence = min((item["confidence"] for item in summaries), default=0.0)
    failed = [item["endpoint"] for item in summaries if item["status"] != "ok"]
    return {
        "schema": "gamecult.localcast.obs_v1_smoke_ledger.v1",
        "createdWallTime": datetime.now(timezone.utc).isoformat(),
        "gate": {
            "pluginOrNativeReceiverExpansionAllowed": not failed,
            "reason": "Every planned OBS-facing endpoint must have sender_capture, srt_receive, and obs_present evidence with bounded latency and drift.",
        },
        "thresholds": {
            "maxAbsDriftPpm": max_abs_drift_ppm,
            "maxLatencyMs": max_latency_ms,
            "requiredStages": ["sender_capture", "srt_receive", "obs_present"],
        },
        "endpoints": summaries,
        "overallConfidence": round(overall_confidence, 3),
        "status": "ok" if not failed and summaries else "fail",
        "blockedEndpoints": failed,
    }


def _matched_latencies_ms(events: list[SmokeEvent]) -> list[float]:
    by_id: dict[str, dict[str, SmokeEvent]] = {}
    for event in events:
        if not event.event_id:
            continue
        by_id.setdefault(event.event_id, {})[event.stage] = event
    latencies: list[float] = []
    for stages in by_id.values():
        sender = stages.get("sender_capture")
        observed = stages.get("obs_present")
        if sender is None or observed is None:
            continue
        latencies.append((observed.monotonic_ns - sender.monotonic_ns) / NS_PER_MS)
    return latencies


def _endpoint_drift_ppm(events: list[SmokeEvent]) -> float | None:
    timed = [event for event in events if event.media_time_ns is not None and event.stage in {"srt_receive", "obs_present"}]
    timed.sort(key=lambda item: item.monotonic_ns)
    if len(timed) < 2:
        return None
    first = timed[0]
    last = timed[-1]
    wall_delta = last.monotonic_ns - first.monotonic_ns
    media_delta = int(last.media_time_ns) - int(first.media_time_ns)
    if wall_delta <= 0:
        return None
    return round(((media_delta - wall_delta) / wall_delta) * 1_000_000.0, 3)


def _latency_summary(latencies_ms: list[float]) -> dict[str, float | int | None]:
    if not latencies_ms:
        return {"count": 0, "median": None, "min": None, "max": None}
    return {
        "count": len(latencies_ms),
        "median": round(median(latencies_ms), 3),
        "min": round(min(latencies_ms), 3),
        "max": round(max(latencies_ms), 3),
    }


def _endpoint_confidence(
    events: list[SmokeEvent],
    *,
    latencies_ms: list[float],
    drift_ppm: float | None,
    max_abs_drift_ppm: float,
    max_latency_ms: float,
) -> float:
    if not events:
        return 0.0
    stages = {event.stage for event in events}
    required = {"sender_capture", "srt_receive", "obs_present"}
    stage_score = len(stages & required) / len(required)
    event_score = min(1.0, len(events) / 6.0)
    observation_score = max(0.0, min(1.0, sum(max(0.0, min(1.0, event.confidence)) for event in events) / len(events)))
    latency_score = 0.0
    if latencies_ms:
        worst = max(latencies_ms)
        latency_score = max(0.0, min(1.0, 1.0 - (worst / max_latency_ms)))
    drift_score = 0.0
    if drift_ppm is not None:
        drift_score = max(0.0, min(1.0, 1.0 - (abs(drift_ppm) / max_abs_drift_ppm)))
    return max(0.0, min(1.0, stage_score * 0.35 + event_score * 0.15 + observation_score * 0.15 + latency_score * 0.2 + drift_score * 0.15))
