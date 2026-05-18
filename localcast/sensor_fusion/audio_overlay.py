from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import sys

import numpy as np

from .render_bridge import RenderFramePacket, RenderPointPacket


ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from audio_field.cultcache_audio import CultAudioSourceEvents, CultSpatialAudioFrame  # noqa: E402


@dataclass(frozen=True)
class AudioVisualSyncStatus:
    visual_frame_id: int
    visual_present_time_ns: int
    audio_frame_id: int | None
    audio_time_ns: int | None
    audio_delta_ns: int | None
    source_event_count: int
    overlay_event_count: int
    synchronized: bool


def overlay_audio_events(
    frame: RenderFramePacket,
    events: CultAudioSourceEvents | None,
    audio_frame: CultSpatialAudioFrame | None,
    *,
    window_ns: int = 120_000_000,
    radius_m: float = 0.045,
    max_events: int = 128,
) -> tuple[RenderFramePacket, AudioVisualSyncStatus]:
    if events is None:
        return frame, sync_status(frame, None, 0, 0, False)
    overlay = select_events_for_visual_frame(frame, events, audio_frame, int(window_ns), int(max_events))
    points = list(frame.points)
    for event in overlay:
        confidence = max(0.0, min(1.0, float(event.get("confidence", 0.5))))
        energy = max(0.0, float(event.get("energy", 0.0)))
        alpha = max(0.25, min(1.0, 0.35 + confidence * 0.65))
        intensity = max(0.25, min(1.0, 0.45 + energy * 8000.0))
        points.append(
            RenderPointPacket(
                stable_key=f"audio-event-{event.get('eventId', len(points))}",
                xyz=np.asarray(event.get("positionMeters", [0.0, 0.0, 1.2]), dtype=np.float64),
                radius_m=radius_m * (0.75 + confidence),
                color_rgba=(1.0, 0.35 + 0.45 * intensity, 0.15, alpha),
                confidence=confidence,
                source_timestamp_ns=event_time_ns(event, events, audio_frame),
            )
        )
    return (
        RenderFramePacket(
            schema=frame.schema,
            frame_id=frame.frame_id,
            created_monotonic_ns=frame.created_monotonic_ns,
            source_time_min_ns=frame.source_time_min_ns,
            source_time_max_ns=frame.source_time_max_ns,
            present_time_ns=frame.present_time_ns,
            audio_alignment_time_ns=frame.audio_alignment_time_ns,
            spout_sender_name=frame.spout_sender_name,
            target_width=frame.target_width,
            target_height=frame.target_height,
            points=tuple(points),
        ),
        sync_status(frame, audio_frame, len(events.events), len(overlay), True),
    )


def select_events_for_visual_frame(
    frame: RenderFramePacket,
    events: CultAudioSourceEvents,
    audio_frame: CultSpatialAudioFrame | None,
    window_ns: int,
    max_events: int,
) -> list[dict]:
    if audio_frame is None:
        return list(events.events[:max_events])
    selected = []
    half_window = max(1, window_ns // 2)
    for event in events.events:
        when_ns = event_time_ns(event, events, audio_frame)
        if abs(when_ns - frame.audio_alignment_time_ns) <= half_window:
            selected.append((abs(when_ns - frame.audio_alignment_time_ns), dict(event)))
    selected.sort(key=lambda item: item[0])
    return [event for _, event in selected[:max_events]]


def event_time_ns(event: dict, events: CultAudioSourceEvents, audio_frame: CultSpatialAudioFrame | None) -> int:
    start_sample = int(event.get("startSample", 0))
    if audio_frame is None:
        return events.audio_time_ns + int((start_sample - events.start_sample) * 1_000_000_000 / events.sample_rate)
    event_span = max(1, int(events.frame_count))
    audio_mod = audio_frame.start_sample % event_span
    event_mod = start_sample % event_span
    delta_samples = event_mod - audio_mod
    half_span = event_span // 2
    if delta_samples > half_span:
        delta_samples -= event_span
    elif delta_samples < -half_span:
        delta_samples += event_span
    return audio_frame.audio_time_ns + int(delta_samples * 1_000_000_000 / audio_frame.sample_rate)


def sync_status(
    frame: RenderFramePacket,
    audio_frame: CultSpatialAudioFrame | None,
    source_event_count: int,
    overlay_event_count: int,
    synchronized: bool,
) -> AudioVisualSyncStatus:
    audio_delta_ns = None if audio_frame is None else frame.audio_alignment_time_ns - audio_frame.audio_time_ns
    return AudioVisualSyncStatus(
        visual_frame_id=frame.frame_id,
        visual_present_time_ns=frame.present_time_ns,
        audio_frame_id=None if audio_frame is None else audio_frame.frame_id,
        audio_time_ns=None if audio_frame is None else audio_frame.audio_time_ns,
        audio_delta_ns=audio_delta_ns,
        source_event_count=source_event_count,
        overlay_event_count=overlay_event_count,
        synchronized=bool(synchronized and audio_frame is not None),
    )

