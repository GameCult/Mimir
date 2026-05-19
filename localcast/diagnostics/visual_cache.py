from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import sys
import time
from typing import Any

import numpy as np

from localcast.sensor_fusion.render_bridge import RenderFramePacket, RenderPointPacket


ROOT = Path(__file__).resolve().parents[2]
CULTCACHE_PY_SRC = ROOT.parent / "cultcache-py" / "src"
if CULTCACHE_PY_SRC.exists() and str(CULTCACHE_PY_SRC) not in sys.path:
    sys.path.insert(0, str(CULTCACHE_PY_SRC))

from cultcache_py import CultCache, SingleFileMessagePackBackingStore, define_document_type  # noqa: E402


LIVE_RENDER_FRAME_KEY = "localcast.visual.render-frame.live"
RENDER_FRAME_SCHEMA_ID = "gamecult.localcast.visual.render_frame.v1"
STREAM_STATUS_KEY = "localcast.visual.stream-status.live"
STREAM_STATUS_SCHEMA_ID = "gamecult.localcast.visual.stream_status.v1"
CLAP_EVENTS_KEY = "localcast.calibration.clap-events.live"
CLAP_EVENTS_SCHEMA_ID = "gamecult.localcast.calibration.clap_events.v1"


@dataclass(frozen=True)
class CultRenderPoint:
    stable_key: str
    xyz: tuple[float, float, float]
    radius_m: float
    color_rgba: tuple[float, float, float, float]
    confidence: float
    source_timestamp_ns: int


@dataclass(frozen=True)
class CultRenderFrame:
    schema_version: str
    frame_id: int
    created_monotonic_ns: int
    source_time_min_ns: int
    source_time_max_ns: int
    present_time_ns: int
    audio_alignment_time_ns: int
    spout_sender_name: str
    target_width: int
    target_height: int
    points: tuple[CultRenderPoint, ...]


@dataclass(frozen=True)
class CultStreamStatus:
    sender_name: str
    frames_sent: int
    point_count: int
    frame_source: str
    updated_monotonic_ns: int
    last_error: str


@dataclass(frozen=True)
class CultClapEvent:
    stable_key: str
    position_m: tuple[float, float, float]
    acoustic_oracle_ns: int
    visual_observed_ns: int
    timing_uncertainty_us: float
    visual_confidence: float
    acoustic_confidence: float
    camera_peaks: tuple[dict[str, Any], ...]


@dataclass(frozen=True)
class CultClapEventsFrame:
    schema_version: str
    frame_id: int
    created_monotonic_ns: int
    events: tuple[CultClapEvent, ...]


def _pack(value: Any) -> bytes:
    import msgpack

    return msgpack.packb(value, use_bin_type=True)


def _unpack(payload: bytes) -> Any:
    import msgpack

    return msgpack.unpackb(payload, raw=False)


def _encode_point(point: CultRenderPoint) -> list[Any]:
    return [
        point.stable_key,
        list(point.xyz),
        point.radius_m,
        list(point.color_rgba),
        point.confidence,
        point.source_timestamp_ns,
    ]


def _decode_point(raw: Any) -> CultRenderPoint:
    return CultRenderPoint(
        stable_key=str(raw[0]),
        xyz=(float(raw[1][0]), float(raw[1][1]), float(raw[1][2])),
        radius_m=float(raw[2]),
        color_rgba=(float(raw[3][0]), float(raw[3][1]), float(raw[3][2]), float(raw[3][3])),
        confidence=float(raw[4]),
        source_timestamp_ns=int(raw[5]),
    )


def _encode_render_frame(frame: CultRenderFrame) -> list[Any]:
    return [
        frame.schema_version,
        frame.frame_id,
        frame.created_monotonic_ns,
        frame.source_time_min_ns,
        frame.source_time_max_ns,
        frame.present_time_ns,
        frame.audio_alignment_time_ns,
        frame.spout_sender_name,
        frame.target_width,
        frame.target_height,
        [_encode_point(point) for point in frame.points],
    ]


def _decode_render_frame(raw: Any) -> CultRenderFrame:
    return CultRenderFrame(
        schema_version=str(raw[0]),
        frame_id=int(raw[1]),
        created_monotonic_ns=int(raw[2]),
        source_time_min_ns=int(raw[3]),
        source_time_max_ns=int(raw[4]),
        present_time_ns=int(raw[5]),
        audio_alignment_time_ns=int(raw[6]),
        spout_sender_name=str(raw[7]),
        target_width=int(raw[8]),
        target_height=int(raw[9]),
        points=tuple(_decode_point(point) for point in raw[10]),
    )


def _encode_status(status: CultStreamStatus) -> list[Any]:
    return [
        status.sender_name,
        status.frames_sent,
        status.point_count,
        status.frame_source,
        status.updated_monotonic_ns,
        status.last_error,
    ]


def _decode_status(raw: Any) -> CultStreamStatus:
    return CultStreamStatus(
        sender_name=str(raw[0]),
        frames_sent=int(raw[1]),
        point_count=int(raw[2]),
        frame_source=str(raw[3]),
        updated_monotonic_ns=int(raw[4]),
        last_error=str(raw[5]),
    )


def _encode_clap_event(event: CultClapEvent) -> list[Any]:
    return [
        event.stable_key,
        list(event.position_m),
        event.acoustic_oracle_ns,
        event.visual_observed_ns,
        event.timing_uncertainty_us,
        event.visual_confidence,
        event.acoustic_confidence,
        list(event.camera_peaks),
    ]


def _decode_clap_event(raw: Any) -> CultClapEvent:
    return CultClapEvent(
        stable_key=str(raw[0]),
        position_m=(float(raw[1][0]), float(raw[1][1]), float(raw[1][2])),
        acoustic_oracle_ns=int(raw[2]),
        visual_observed_ns=int(raw[3]),
        timing_uncertainty_us=float(raw[4]),
        visual_confidence=float(raw[5]),
        acoustic_confidence=float(raw[6]),
        camera_peaks=tuple(dict(item) for item in raw[7]),
    )


def _encode_clap_events_frame(frame: CultClapEventsFrame) -> list[Any]:
    return [
        frame.schema_version,
        frame.frame_id,
        frame.created_monotonic_ns,
        [_encode_clap_event(event) for event in frame.events],
    ]


def _decode_clap_events_frame(raw: Any) -> CultClapEventsFrame:
    return CultClapEventsFrame(
        schema_version=str(raw[0]),
        frame_id=int(raw[1]),
        created_monotonic_ns=int(raw[2]),
        events=tuple(_decode_clap_event(item) for item in raw[3]),
    )


RENDER_FRAME_DOCUMENT = define_document_type(
    "localcast.visual.render_frame",
    encode=_encode_render_frame,
    decode=_decode_render_frame,
    name=lambda frame: LIVE_RENDER_FRAME_KEY if isinstance(frame, CultRenderFrame) else None,
    payload_encoder=_pack,
    payload_decoder=_unpack,
)

STREAM_STATUS_DOCUMENT = define_document_type(
    "localcast.visual.stream_status",
    encode=_encode_status,
    decode=_decode_status,
    name=lambda status: STREAM_STATUS_KEY if isinstance(status, CultStreamStatus) else None,
    payload_encoder=_pack,
    payload_decoder=_unpack,
)

CLAP_EVENTS_DOCUMENT = define_document_type(
    "localcast.calibration.clap_events",
    encode=_encode_clap_events_frame,
    decode=_decode_clap_events_frame,
    name=lambda frame: CLAP_EVENTS_KEY if isinstance(frame, CultClapEventsFrame) else None,
    payload_encoder=_pack,
    payload_decoder=_unpack,
)


def _retry_cache_io(operation):
    last_error: Exception | None = None
    for _ in range(40):
        try:
            return operation()
        except (PermissionError, EOFError, ValueError) as exc:
            last_error = exc
            time.sleep(0.01)
    assert last_error is not None
    raise last_error


def _open_visual_cache_once(path: Path) -> CultCache:
    cache = (
        CultCache.builder()
        .register_document_type(RENDER_FRAME_DOCUMENT)
        .register_document_type(STREAM_STATUS_DOCUMENT)
        .register_document_type(CLAP_EVENTS_DOCUMENT)
        .add_generic_store(SingleFileMessagePackBackingStore(path))
        .build()
    )
    cache.pull_all_backing_stores()
    return cache


def open_visual_cache(path: Path) -> CultCache:
    return _retry_cache_io(lambda: _open_visual_cache_once(path))


def render_frame_to_cult(frame: RenderFramePacket) -> CultRenderFrame:
    return CultRenderFrame(
        schema_version=RENDER_FRAME_SCHEMA_ID,
        frame_id=frame.frame_id,
        created_monotonic_ns=frame.created_monotonic_ns,
        source_time_min_ns=frame.source_time_min_ns,
        source_time_max_ns=frame.source_time_max_ns,
        present_time_ns=frame.present_time_ns,
        audio_alignment_time_ns=frame.audio_alignment_time_ns,
        spout_sender_name=frame.spout_sender_name,
        target_width=frame.target_width,
        target_height=frame.target_height,
        points=tuple(
            CultRenderPoint(
                stable_key=point.stable_key,
                xyz=(float(point.xyz[0]), float(point.xyz[1]), float(point.xyz[2])),
                radius_m=point.radius_m,
                color_rgba=point.color_rgba,
                confidence=point.confidence,
                source_timestamp_ns=point.source_timestamp_ns,
            )
            for point in frame.points
        ),
    )


def cult_render_frame_to_packet(frame: CultRenderFrame) -> RenderFramePacket:
    return RenderFramePacket(
        schema="localcast.sensor_fusion.render_frame.v1",
        frame_id=frame.frame_id,
        created_monotonic_ns=frame.created_monotonic_ns,
        source_time_min_ns=frame.source_time_min_ns,
        source_time_max_ns=frame.source_time_max_ns,
        present_time_ns=frame.present_time_ns,
        audio_alignment_time_ns=frame.audio_alignment_time_ns,
        spout_sender_name=frame.spout_sender_name,
        target_width=frame.target_width,
        target_height=frame.target_height,
        points=tuple(
            RenderPointPacket(
                stable_key=point.stable_key,
                xyz=np.asarray(point.xyz, dtype=np.float64),
                radius_m=point.radius_m,
                color_rgba=point.color_rgba,
                confidence=point.confidence,
                source_timestamp_ns=point.source_timestamp_ns,
            )
            for point in frame.points
        ),
    )


def put_live_render_frame(path: Path, frame: RenderFramePacket) -> None:
    def operation() -> None:
        cache = open_visual_cache(path)
        cache.put(RENDER_FRAME_DOCUMENT, LIVE_RENDER_FRAME_KEY, render_frame_to_cult(frame))

    _retry_cache_io(operation)


def get_live_render_frame(path: Path) -> RenderFramePacket | None:
    def operation() -> RenderFramePacket | None:
        cache = open_visual_cache(path)
        frame = cache.get(RENDER_FRAME_DOCUMENT, LIVE_RENDER_FRAME_KEY)
        return None if frame is None else cult_render_frame_to_packet(frame)

    return _retry_cache_io(operation)


def put_stream_status(path: Path, status: CultStreamStatus) -> None:
    def operation() -> None:
        cache = open_visual_cache(path)
        cache.put(STREAM_STATUS_DOCUMENT, STREAM_STATUS_KEY, status)

    _retry_cache_io(operation)


def make_clap_events_frame(*, frame_id: int, events: list[Any] | tuple[Any, ...]) -> CultClapEventsFrame:
    return CultClapEventsFrame(
        schema_version=CLAP_EVENTS_SCHEMA_ID,
        frame_id=int(frame_id),
        created_monotonic_ns=time.monotonic_ns(),
        events=tuple(
            CultClapEvent(
                stable_key=str(event.stable_key),
                position_m=tuple(float(value) for value in event.position_m),
                acoustic_oracle_ns=int(event.acoustic_oracle_ns),
                visual_observed_ns=int(event.visual_observed_ns),
                timing_uncertainty_us=float(event.timing_uncertainty_us),
                visual_confidence=float(event.visual_confidence),
                acoustic_confidence=float(event.acoustic_confidence),
                camera_peaks=tuple(
                    {
                        "sensorId": peak.sensor_id,
                        "timestampNs": int(peak.timestamp_ns),
                        "uv": [float(peak.uv[0]), float(peak.uv[1])],
                        "score": float(peak.score),
                    }
                    for peak in event.camera_peaks
                ),
            )
            for event in events
        ),
    )


def put_live_clap_events(path: Path, frame: CultClapEventsFrame) -> None:
    def operation() -> None:
        cache = open_visual_cache(path)
        cache.put(CLAP_EVENTS_DOCUMENT, CLAP_EVENTS_KEY, frame)

    _retry_cache_io(operation)


def get_live_clap_events(path: Path) -> CultClapEventsFrame | None:
    def operation() -> CultClapEventsFrame | None:
        cache = open_visual_cache(path)
        return cache.get(CLAP_EVENTS_DOCUMENT, CLAP_EVENTS_KEY)

    return _retry_cache_io(operation)
