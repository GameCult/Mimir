from __future__ import annotations

import argparse
import collections
import datetime as dt
import json
import statistics
import struct
from pathlib import Path
from typing import Any, Iterable


def load_jsonl(path: Path) -> Iterable[dict[str, Any]]:
    with path.open("r", encoding="utf-8", errors="ignore") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                value = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(value, dict):
                yield value


def load_jsonl_tail(path: Path, max_bytes: int) -> Iterable[dict[str, Any]]:
    size = path.stat().st_size
    with path.open("rb") as handle:
        handle.seek(max(0, size - max_bytes))
        data = handle.read()
    text = data.decode("utf-8", errors="ignore")
    lines = text.splitlines()
    if size > max_bytes and lines:
        lines = lines[1:]
    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            yield value


def number(value: Any, fallback: float = 0.0) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return fallback


def field(data: dict[str, Any], *names: str, fallback: Any = None) -> Any:
    for name in names:
        if name in data:
            return data[name]
    return fallback


def percentile(values: list[float], rank: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round((len(ordered) - 1) * rank)))
    return ordered[index]


def maybe_mean(values: list[float]) -> float | None:
    return statistics.mean(values) if values else None


def full_frame_rate(values: list[float]) -> float | None:
    return sum(1 for ratio in values if ratio == 1.0) / len(values) if values else None


def parse_wall_clock(value: Any) -> dt.datetime | None:
    if not isinstance(value, str) or not value:
        return None
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.UTC)
    return parsed.astimezone(dt.UTC)


def latest_monotonic_well_run(wells: list[dict[str, Any]]) -> list[dict[str, Any]]:
    suffix: list[dict[str, Any]] = []
    previous: float | None = None
    for item in wells:
        sequence = number(item.get("sequence"), -1.0)
        if previous is not None and sequence < previous:
            suffix = []
        suffix.append(item)
        previous = sequence
    return suffix


def summarize_well(path: Path, all_runs: bool = False) -> dict[str, Any]:
    rows = list(load_jsonl(path))
    all_wells = [item for item in rows if item.get("document") == "mimir.well_snapshot.v1"]
    wells = all_wells if all_runs else latest_monotonic_well_run(all_wells)
    run_start = parse_wall_clock(wells[0].get("wallClockUtc")) if wells else None
    capture_pages = 0
    stream_frames = 0
    body_statuses: collections.Counter[str] = collections.Counter()
    body_bytes: list[float] = []
    capture_sample_counts: list[float] = []

    for item in rows:
        if run_start is not None:
            item_wall = parse_wall_clock(item.get("wallClockUtc"))
            if item_wall is None or item_wall < run_start:
                continue
        document = item.get("document")
        if document == "mimir.well_capture_page.v1":
            capture_pages += 1
            samples = item.get("samples") if isinstance(item.get("samples"), list) else []
            capture_sample_counts.append(float(len(samples)))
            for sample in samples:
                if not isinstance(sample, dict):
                    continue
                body = sample.get("body") if isinstance(sample.get("body"), dict) else {}
                body_statuses[str(body.get("status", "unknown"))] += 1
                body_bytes.append(number(body.get("byteLength"), number(sample.get("ByteLength"))))
        elif document == "mimir.cultmesh_stream_frame.v1":
            stream_frames += 1

    readiness_all: list[tuple[float, float]] = []
    readiness_after_120: list[float] = []
    readiness_after_300: list[float] = []
    min_overlaps_after_120: list[float] = []
    edge_skews_after_120: list[float] = []
    statuses_after_120: collections.Counter[str] = collections.Counter()
    per_source_after_120: dict[str, collections.Counter[str]] = collections.defaultdict(collections.Counter)
    delays: list[float] = []

    for item in wells:
        elapsed = number(item.get("elapsedSeconds"))
        frame = item.get("synchronizedFrame") if isinstance(item.get("synchronizedFrame"), dict) else {}
        slices = frame.get("slices") if isinstance(frame.get("slices"), list) else []
        delay = number(field(frame, "PresentationDelayMs", "presentationDelayMs"))
        if delay:
            delays.append(delay)
        if slices:
            ready = sum(1 for slice_item in slices if field(slice_item, "Status", "status") == "Ready")
            ratio = ready / len(slices)
            readiness_all.append((elapsed, ratio))
            if elapsed >= 120.0:
                readiness_after_120.append(ratio)
            if elapsed >= 300.0:
                readiness_after_300.append(ratio)
        if elapsed >= 120.0:
            for slice_item in slices:
                status = str(field(slice_item, "Status", "status", fallback="unknown"))
                source_id = str(field(slice_item, "SourceId", "sourceId", fallback="unknown"))
                statuses_after_120[status] += 1
                per_source_after_120[source_id][status] += 1

            domains = item.get("clockDomains", {}).get("domains", [])
            overlaps = [number(field(domain, "OverlapNs", "overlapNs")) / 1_000_000.0 for domain in domains]
            edges = [
                number(field(domain, "MinLatestEdgeNs", "minLatestEdgeNs"))
                for domain in domains
                if number(field(domain, "MinLatestEdgeNs", "minLatestEdgeNs")) > 0
            ]
            if overlaps:
                min_overlaps_after_120.append(min(overlaps))
            if len(edges) >= 2:
                edge_skews_after_120.append((max(edges) - min(edges)) / 1_000_000.0)

    last = wells[-1] if wells else {}
    pressure = last.get("streamPressure") if isinstance(last.get("streamPressure"), dict) else {}
    publish = pressure.get("publish") if isinstance(pressure.get("publish"), dict) else {}
    poll = pressure.get("poll") if isinstance(pressure.get("poll"), dict) else {}
    visual = last.get("visualCalibration", {}).get("cameras", [])

    return {
        "runMode": "all-runs" if all_runs else "latest-monotonic-run",
        "allWellSnapshots": len(all_wells),
        "wellSnapshots": len(wells),
        "capturePages": capture_pages,
        "streamFrames": stream_frames,
        "lastSequence": last.get("sequence", 0),
        "lastElapsedSeconds": number(last.get("elapsedSeconds")),
        "ingestedSamples": number(last.get("ingestedSamples")),
        "presentationDelayMs": {
            "samples": len(delays),
            "last": delays[-1] if delays else 0.0,
            "min": min(delays) if delays else 0.0,
            "median": statistics.median(delays) if delays else 0.0,
            "p95": percentile(delays, 0.95),
            "max": max(delays) if delays else 0.0,
            "recent": delays[-12:],
        },
        "readiness": {
            "allMean": statistics.mean([ratio for _, ratio in readiness_all]) if readiness_all else 0.0,
            "after120Samples": len(readiness_after_120),
            "after120Mean": maybe_mean(readiness_after_120),
            "after300Samples": len(readiness_after_300),
            "after300Mean": maybe_mean(readiness_after_300),
            "after120FullFrameRate": full_frame_rate(readiness_after_120),
            "after300FullFrameRate": full_frame_rate(readiness_after_300),
            "statusesAfter120": dict(statuses_after_120),
            "perSourceAfter120": {key: dict(value) for key, value in sorted(per_source_after_120.items())},
        },
        "overlapAfter120Ms": {
            "medianMinOverlap": statistics.median(min_overlaps_after_120) if min_overlaps_after_120 else 0.0,
            "p10MinOverlap": percentile(min_overlaps_after_120, 0.10),
            "minMinOverlap": min(min_overlaps_after_120) if min_overlaps_after_120 else 0.0,
            "medianEdgeSkew": statistics.median(edge_skews_after_120) if edge_skews_after_120 else 0.0,
            "p90EdgeSkew": percentile(edge_skews_after_120, 0.90),
        },
        "streamPressure": {
            "pollAverageMs": number(poll.get("averageMilliseconds")),
            "pollMaxMs": number(poll.get("maxMilliseconds")),
            "zeroPollIterations": int(number(poll.get("zeroPollIterations"))),
            "publishAverageMs": number(publish.get("averageMilliseconds")),
            "publishMaxMs": number(publish.get("maxMilliseconds")),
            "publishedBytes": int(number(publish.get("bytes"))),
            "lastDocument": publish.get("lastDocument", ""),
            "lastBytes": int(number(publish.get("lastBytes"))),
        },
        "captureBodies": {
            "medianSampleCount": statistics.median(capture_sample_counts) if capture_sample_counts else 0.0,
            "bodyStatuses": dict(body_statuses),
            "medianBodyBytes": statistics.median(body_bytes) if body_bytes else 0.0,
            "maxBodyBytes": max(body_bytes) if body_bytes else 0.0,
        },
        "visualCalibration": [
            {
                "sourceId": field(camera, "SourceId", "sourceId", fallback="unknown"),
                "score": number(field(camera, "BestScore", "bestScore")),
                "leds": int(number(field(camera, "BestDetectedLedCount", "bestDetectedLedCount"))),
                "usable": bool(field(camera, "BestUsableForCalibration", "bestUsableForCalibration", fallback=False)),
                "state": field(camera, "State", "state", fallback="unknown"),
            }
            for camera in visual
            if isinstance(camera, dict)
        ],
    }


def summarize_move(path: Path | None, tail_bytes: int) -> dict[str, Any]:
    if path is None or not path.exists():
        return {}
    lines = list(load_jsonl_tail(path, tail_bytes))
    if not lines:
        return {}
    last = lines[-1]
    live_score = last.get("live_score") if isinstance(last.get("live_score"), dict) else {}
    voices = live_score.get("voices") if isinstance(live_score.get("voices"), list) else []
    targets = live_score.get("move_targets") if isinstance(live_score.get("move_targets"), list) else []
    active = [voice for voice in voices if isinstance(voice, dict) and voice.get("active")]
    confidence_values = []
    for item in lines:
        live = item.get("live_score") if isinstance(item.get("live_score"), dict) else {}
        if isinstance(live.get("confidence"), (int, float)):
            confidence_values.append(number(live.get("confidence")))
        elif isinstance(item.get("score_confidence"), (int, float)):
            confidence_values.append(number(item.get("score_confidence")))
    bpm_confidences = [number(item.get("bpm_confidence")) for item in lines if isinstance(item.get("bpm_confidence"), (int, float))]
    note_counts: collections.Counter[str] = collections.Counter()
    emitted_audio_frames = 0
    move_lit_frames = 0
    for item in lines:
        live = item.get("live_score") if isinstance(item.get("live_score"), dict) else {}
        for voice in live.get("voices", []) if isinstance(live.get("voices"), list) else []:
            if isinstance(voice, dict) and voice.get("active"):
                note_counts[str(voice.get("note_name", "?"))] += 1
        if number(item.get("emitted_audio_events")) > 0:
            emitted_audio_frames += 1
        moves = item.get("moves") if isinstance(item.get("moves"), dict) else {}
        if any(isinstance(value, list) and any(number(channel) > 0 for channel in value) for value in moves.values()):
            move_lit_frames += 1
    return {
        "tailEvents": len(lines),
        "tailBytes": tail_bytes,
        "bpm": number(last.get("bpm")),
        "bpmConfidence": number(last.get("bpm_confidence")),
        "scoreConfidenceMedian": statistics.median(confidence_values) if confidence_values else 0.0,
        "scoreConfidenceMax": max(confidence_values) if confidence_values else 0.0,
        "bpmConfidenceMedian": statistics.median(bpm_confidences) if bpm_confidences else 0.0,
        "bpmConfidenceMax": max(bpm_confidences) if bpm_confidences else 0.0,
        "key": f"{last.get('key_name', '?')} {last.get('key_mode', '?')}",
        "chord": last.get("chord_name", "?"),
        "voiceCount": len(voices),
        "activeVoiceCount": len(active),
        "moveTargetCount": len(targets),
        "noteHistogram": dict(note_counts.most_common(12)),
        "emittedAudioFrameCount": emitted_audio_frames,
        "moveLitFrameCount": move_lit_frames,
        "activeVoices": [
            {
                "source": voice.get("source", "unknown"),
                "note": voice.get("note_name", "?"),
                "confidence": number(voice.get("confidence")),
                "role": voice.get("role", "voice"),
            }
            for voice in active
        ],
        "targets": [
            {
                "move": int(number(target.get("move_index"))),
                "source": target.get("source", "unknown"),
                "note": target.get("note_name", "?"),
                "priority": number(target.get("calibration_priority")),
            }
            for target in targets
            if isinstance(target, dict)
        ],
    }


def audio_segment_peak(path: Path, start_sample: int, end_sample: int) -> float:
    if start_sample < 0 or end_sample <= start_sample or not path.exists():
        return 0.0
    byte_start = start_sample * 4
    byte_count = (end_sample - start_sample) * 4
    if byte_start >= path.stat().st_size:
        return 0.0
    with path.open("rb") as handle:
        handle.seek(byte_start)
        data = handle.read(min(byte_count, path.stat().st_size - byte_start))
    if len(data) < 4:
        return 0.0
    sample_count = len(data) // 4
    samples = struct.unpack("<" + "f" * sample_count, data[: sample_count * 4])
    return max((abs(sample) for sample in samples), default=0.0)


def summarize_paired_pulses(path: Path | None, max_led_error_ms: float) -> dict[str, Any]:
    if path is None or not path.exists():
        return {}

    rows = [
        item
        for item in load_jsonl(path)
        if item.get("document") == "mimir.psmove_usb_audio_visual_pulse_event.v1"
    ]
    if not rows:
        return {}

    schedules: dict[str, dict[str, Any]] = {}
    visual_phases: dict[str, dict[str, dict[str, Any]]] = collections.defaultdict(dict)
    render_results: list[str] = []
    orphan_visual: list[str] = []
    audio_peaks: dict[str, float] = {}

    for item in rows:
        phase = str(item.get("phase", ""))
        event_id = item.get("EventId") or item.get("eventId")
        if phase == "schedule" and isinstance(event_id, str):
            schedules[event_id] = item
        elif phase in {"on", "off"} and isinstance(event_id, str):
            visual_phases[event_id][phase] = item
        elif phase == "render-complete":
            render_results.append(str(item.get("result", "")))

    for event_id in visual_phases:
        if event_id not in schedules:
            orphan_visual.append(event_id)

    missing_visual = [
        event_id
        for event_id in schedules
        if "on" not in visual_phases.get(event_id, {}) or "off" not in visual_phases.get(event_id, {})
    ]

    schedule_errors_ms: list[float] = []
    for event_id, phase_map in visual_phases.items():
        scheduled = schedules.get(event_id)
        on = phase_map.get("on")
        if not scheduled or not on:
            continue
        planned_ms = number(scheduled.get("OffsetSeconds")) * 1000.0
        actual_ms = (number(on.get("startedNs")) - number(on.get("trainStartNs"))) / 1_000_000.0
        schedule_errors_ms.append(actual_ms - planned_ms)

    for event_id, scheduled in schedules.items():
        audio_path = Path(str(scheduled.get("audioPath", "")))
        if not audio_path.is_absolute():
            audio_path = path.parent / audio_path
        audio_peaks[event_id] = audio_segment_peak(
            audio_path,
            int(number(scheduled.get("AudioStartSample"))),
            int(number(scheduled.get("AudioEndSample"))),
        )

    silent_audio = [event_id for event_id, peak in audio_peaks.items() if peak <= 0.000001]
    render_ok = any(result.startswith("ok",) for result in render_results)
    max_abs_error = max((abs(value) for value in schedule_errors_ms), default=0.0)
    passed = (
        bool(schedules)
        and render_ok
        and not orphan_visual
        and not missing_visual
        and not silent_audio
        and max_abs_error <= max_led_error_ms
    )

    return {
        "path": str(path),
        "events": len(schedules),
        "renderOk": render_ok,
        "visualOnCount": sum(1 for phases in visual_phases.values() if "on" in phases),
        "visualOffCount": sum(1 for phases in visual_phases.values() if "off" in phases),
        "orphanVisualEvents": orphan_visual,
        "missingVisualEvents": missing_visual,
        "silentAudioEvents": silent_audio,
        "audioPeakMin": min(audio_peaks.values()) if audio_peaks else 0.0,
        "audioPeakMax": max(audio_peaks.values()) if audio_peaks else 0.0,
        "ledScheduleErrorMsMedian": statistics.median(schedule_errors_ms) if schedule_errors_ms else 0.0,
        "ledScheduleErrorMsMaxAbs": max_abs_error,
        "ledScheduleErrorLimitMs": max_led_error_ms,
        "passed": passed,
    }


def hypotheses(summary: dict[str, Any]) -> list[str]:
    result: list[str] = []
    readiness = summary["readiness"]
    overlap = summary["overlapAfter120Ms"]
    pressure = summary["streamPressure"]
    after300_full_frame_rate = readiness.get("after300FullFrameRate")
    if isinstance(after300_full_frame_rate, (int, float)) and after300_full_frame_rate >= 0.95:
        result.append(
            "After warm-up, frame readiness is high; fixed 2500 ms delay should be reduced by an adaptive controller until misses reappear."
        )
    if overlap["p10MinOverlap"] <= 25.0:
        result.append(
            "ASIO blocks expose a very thin per-domain overlap; audio should be aligned by Faust delay/resampling, not by asking the presentation planner for seconds of holdback."
        )
    if pressure["publishMaxMs"] > 50.0 or pressure["lastBytes"] > 1_000_000:
        result.append(
            "Capture-page publication creates burst pressure; body paging/stream frames need their own compact lane before latency experiments can trust publish timing."
        )
    weak_visual = [item["sourceId"] for item in summary["visualCalibration"] if item["score"] < 0.55]
    if weak_visual:
        result.append(
            "Visual calibration is not yet usable for pose truth on: " + ", ".join(weak_visual) + "; treat feature tracks as timing/motion witnesses until calibration score improves."
        )
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarize Mimir Well shakedown metrics.")
    parser.add_argument("--well-log", required=True, type=Path)
    parser.add_argument("--move-log", type=Path)
    parser.add_argument("--paired-pulse-log", type=Path)
    parser.add_argument("--paired-pulse-max-led-error-ms", type=float, default=5.0)
    parser.add_argument("--fail-on-paired-pulse-error", action="store_true")
    parser.add_argument("--move-tail-bytes", type=int, default=4 * 1024 * 1024)
    parser.add_argument("--all-runs", action="store_true", help="Summarize the full ledger instead of the latest monotonic Well sequence.")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    summary = summarize_well(args.well_log, all_runs=args.all_runs)
    move = summarize_move(args.move_log, args.move_tail_bytes)
    paired_pulses = summarize_paired_pulses(args.paired_pulse_log, args.paired_pulse_max_led_error_ms)
    output = {
        "well": summary,
        "move": move,
        "pairedPulses": paired_pulses,
        "hypotheses": hypotheses(summary),
    }
    if args.json:
        print(json.dumps(output, indent=2))
        return 0

    print(f"Well snapshots: {summary['wellSnapshots']} capturePages: {summary['capturePages']} streamFrames: {summary['streamFrames']}")
    print(f"Last seq: {summary['lastSequence']} elapsed: {summary['lastElapsedSeconds']:.1f}s ingested: {summary['ingestedSamples']:.0f}")
    print(f"Presentation delay ms: {summary['presentationDelayMs']}")
    print("Readiness:")
    for key, value in summary["readiness"].items():
        print(f"  {key}: {value}")
    print("Overlap after 120s ms:")
    for key, value in summary["overlapAfter120Ms"].items():
        print(f"  {key}: {value}")
    print("Stream pressure:")
    for key, value in summary["streamPressure"].items():
        print(f"  {key}: {value}")
    if move:
        print(f"Move score: bpm={move['bpm']:.1f} c={move['bpmConfidence']:.2f} key={move['key']} chord={move['chord']} voices={move['activeVoiceCount']}/{move['voiceCount']}")
        print(f"  tail score confidence median/max: {move['scoreConfidenceMedian']:.3f}/{move['scoreConfidenceMax']:.3f}")
        print(f"  tail bpm confidence median/max: {move['bpmConfidenceMedian']:.3f}/{move['bpmConfidenceMax']:.3f}")
        print(f"  tail emitted audio frames: {move['emittedAudioFrameCount']} move-lit frames: {move['moveLitFrameCount']}")
        print(f"  tail notes: {move['noteHistogram']}")
    if paired_pulses:
        print(
            "Paired pulses: "
            f"passed={paired_pulses['passed']} events={paired_pulses['events']} "
            f"renderOk={paired_pulses['renderOk']} visual={paired_pulses['visualOnCount']}/{paired_pulses['visualOffCount']} "
            f"audioPeak={paired_pulses['audioPeakMin']:.6f}-{paired_pulses['audioPeakMax']:.6f} "
            f"ledErrorMedian/maxAbs/limit={paired_pulses['ledScheduleErrorMsMedian']:.3f}/{paired_pulses['ledScheduleErrorMsMaxAbs']:.3f}/{paired_pulses['ledScheduleErrorLimitMs']:.3f}ms"
        )
        if paired_pulses["orphanVisualEvents"] or paired_pulses["missingVisualEvents"] or paired_pulses["silentAudioEvents"]:
            print(f"  orphan visual: {paired_pulses['orphanVisualEvents']}")
            print(f"  missing visual: {paired_pulses['missingVisualEvents']}")
            print(f"  silent audio: {paired_pulses['silentAudioEvents']}")
    print("Hypotheses:")
    for item in output["hypotheses"]:
        print(f"- {item}")
    if args.fail_on_paired_pulse_error and paired_pulses and not paired_pulses["passed"]:
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
