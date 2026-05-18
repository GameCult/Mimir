from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import sys
import time
from typing import Any

import numpy as np


ROOT = Path(__file__).resolve().parents[1]
CULTCACHE_PY_SRC = ROOT.parent / "cultcache-py" / "src"
if CULTCACHE_PY_SRC.exists() and str(CULTCACHE_PY_SRC) not in sys.path:
    sys.path.insert(0, str(CULTCACHE_PY_SRC))

from cultcache_py import CultCache, SingleFileMessagePackBackingStore, define_document_type  # noqa: E402


LIVE_SPATIAL_AUDIO_KEY = "localcast.audio.spatial-frame.live"
SPATIAL_AUDIO_SCHEMA_ID = "gamecult.localcast.audio.spatial_frame.v1"
AUDIO_STREAM_STATUS_KEY = "localcast.audio.stream-status.live"
AUDIO_STREAM_STATUS_SCHEMA_ID = "gamecult.localcast.audio.stream_status.v1"
LIVE_SOURCE_EVENTS_KEY = "localcast.audio.source-events.live"
SOURCE_EVENTS_SCHEMA_ID = "gamecult.localcast.audio.source_events.v1"
LIVE_PHASE_FIELD_KEY = "localcast.audio.phase-field.live"
PHASE_FIELD_SCHEMA_ID = "gamecult.localcast.audio.phase_field.v1"


@dataclass(frozen=True)
class CultSpatialAudioFrame:
    schema_version: str
    frame_id: int
    created_monotonic_ns: int
    audio_time_ns: int
    sample_rate: int
    start_sample: int
    frame_count: int
    channel_order: str
    normalization: str
    channels: tuple[str, ...]
    sample_format: str
    interleaved_pcm_f32le: bytes


@dataclass(frozen=True)
class CultAudioStreamStatus:
    schema_version: str
    stream_name: str
    frames_sent: int
    sample_rate: int
    frame_count: int
    channels: tuple[str, ...]
    updated_monotonic_ns: int
    last_error: str


@dataclass(frozen=True)
class CultAudioSourceEvents:
    schema_version: str
    frame_id: int
    audio_time_ns: int
    sample_rate: int
    start_sample: int
    frame_count: int
    events: tuple[dict[str, Any], ...]
    voice_focus: tuple[dict[str, Any], ...]


@dataclass(frozen=True)
class CultAudioPhaseField:
    schema_version: str
    frame_id: int
    audio_time_ns: int
    sample_rate: int
    start_sample: int
    frame_count: int
    reference_id: str
    sources: tuple[dict[str, Any], ...]
    global_confidence: float
    needs_active_probe: bool
    active_probe_reason: str


def _pack(value: Any) -> bytes:
    import msgpack

    return msgpack.packb(value, use_bin_type=True)


def _unpack(payload: bytes) -> Any:
    import msgpack

    return msgpack.unpackb(payload, raw=False)


def _encode_audio_frame(frame: CultSpatialAudioFrame) -> list[Any]:
    return [
        frame.schema_version,
        frame.frame_id,
        frame.created_monotonic_ns,
        frame.audio_time_ns,
        frame.sample_rate,
        frame.start_sample,
        frame.frame_count,
        frame.channel_order,
        frame.normalization,
        list(frame.channels),
        frame.sample_format,
        frame.interleaved_pcm_f32le,
    ]


def _decode_audio_frame(raw: Any) -> CultSpatialAudioFrame:
    return CultSpatialAudioFrame(
        schema_version=str(raw[0]),
        frame_id=int(raw[1]),
        created_monotonic_ns=int(raw[2]),
        audio_time_ns=int(raw[3]),
        sample_rate=int(raw[4]),
        start_sample=int(raw[5]),
        frame_count=int(raw[6]),
        channel_order=str(raw[7]),
        normalization=str(raw[8]),
        channels=tuple(str(channel) for channel in raw[9]),
        sample_format=str(raw[10]),
        interleaved_pcm_f32le=bytes(raw[11]),
    )


def _encode_audio_status(status: CultAudioStreamStatus) -> list[Any]:
    return [
        status.schema_version,
        status.stream_name,
        status.frames_sent,
        status.sample_rate,
        status.frame_count,
        list(status.channels),
        status.updated_monotonic_ns,
        status.last_error,
    ]


def _decode_audio_status(raw: Any) -> CultAudioStreamStatus:
    return CultAudioStreamStatus(
        schema_version=str(raw[0]),
        stream_name=str(raw[1]),
        frames_sent=int(raw[2]),
        sample_rate=int(raw[3]),
        frame_count=int(raw[4]),
        channels=tuple(str(channel) for channel in raw[5]),
        updated_monotonic_ns=int(raw[6]),
        last_error=str(raw[7]),
    )


def _encode_source_events(events: CultAudioSourceEvents) -> list[Any]:
    return [
        events.schema_version,
        events.frame_id,
        events.audio_time_ns,
        events.sample_rate,
        events.start_sample,
        events.frame_count,
        list(events.events),
        list(events.voice_focus),
    ]


def _decode_source_events(raw: Any) -> CultAudioSourceEvents:
    return CultAudioSourceEvents(
        schema_version=str(raw[0]),
        frame_id=int(raw[1]),
        audio_time_ns=int(raw[2]),
        sample_rate=int(raw[3]),
        start_sample=int(raw[4]),
        frame_count=int(raw[5]),
        events=tuple(dict(item) for item in raw[6]),
        voice_focus=tuple(dict(item) for item in raw[7]),
    )


def _encode_phase_field(field: CultAudioPhaseField) -> list[Any]:
    return [
        field.schema_version,
        field.frame_id,
        field.audio_time_ns,
        field.sample_rate,
        field.start_sample,
        field.frame_count,
        field.reference_id,
        list(field.sources),
        field.global_confidence,
        field.needs_active_probe,
        field.active_probe_reason,
    ]


def _decode_phase_field(raw: Any) -> CultAudioPhaseField:
    return CultAudioPhaseField(
        schema_version=str(raw[0]),
        frame_id=int(raw[1]),
        audio_time_ns=int(raw[2]),
        sample_rate=int(raw[3]),
        start_sample=int(raw[4]),
        frame_count=int(raw[5]),
        reference_id=str(raw[6]),
        sources=tuple(dict(item) for item in raw[7]),
        global_confidence=float(raw[8]),
        needs_active_probe=bool(raw[9]),
        active_probe_reason=str(raw[10]),
    )


SPATIAL_AUDIO_FRAME_DOCUMENT = define_document_type(
    "localcast.audio.spatial_frame",
    encode=_encode_audio_frame,
    decode=_decode_audio_frame,
    name=lambda frame: LIVE_SPATIAL_AUDIO_KEY if isinstance(frame, CultSpatialAudioFrame) else None,
    payload_encoder=_pack,
    payload_decoder=_unpack,
)

AUDIO_STREAM_STATUS_DOCUMENT = define_document_type(
    "localcast.audio.stream_status",
    encode=_encode_audio_status,
    decode=_decode_audio_status,
    name=lambda status: AUDIO_STREAM_STATUS_KEY if isinstance(status, CultAudioStreamStatus) else None,
    payload_encoder=_pack,
    payload_decoder=_unpack,
)

SOURCE_EVENTS_DOCUMENT = define_document_type(
    "localcast.audio.source_events",
    encode=_encode_source_events,
    decode=_decode_source_events,
    name=lambda events: LIVE_SOURCE_EVENTS_KEY if isinstance(events, CultAudioSourceEvents) else None,
    payload_encoder=_pack,
    payload_decoder=_unpack,
)

PHASE_FIELD_DOCUMENT = define_document_type(
    "localcast.audio.phase_field",
    encode=_encode_phase_field,
    decode=_decode_phase_field,
    name=lambda field: LIVE_PHASE_FIELD_KEY if isinstance(field, CultAudioPhaseField) else None,
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


def _open_audio_cache_once(path: Path) -> CultCache:
    cache = (
        CultCache.builder()
        .register_document_type(SPATIAL_AUDIO_FRAME_DOCUMENT)
        .register_document_type(AUDIO_STREAM_STATUS_DOCUMENT)
        .register_document_type(SOURCE_EVENTS_DOCUMENT)
        .register_document_type(PHASE_FIELD_DOCUMENT)
        .add_generic_store(SingleFileMessagePackBackingStore(path))
        .build()
    )
    cache.pull_all_backing_stores()
    return cache


def open_audio_cache(path: Path) -> CultCache:
    return _retry_cache_io(lambda: _open_audio_cache_once(path))


def make_spatial_audio_frame(
    block: np.ndarray,
    *,
    frame_id: int,
    sample_rate: int,
    start_sample: int,
    audio_time_ns: int,
    channel_order: str = "ACN",
    normalization: str = "SN3D",
    channels: tuple[str, ...] = ("W", "Y", "Z", "X"),
) -> CultSpatialAudioFrame:
    pcm = np.asarray(block, dtype=np.float32)
    if pcm.ndim != 2:
        raise ValueError("spatial audio block must be a 2D frames-by-channels array")
    if pcm.shape[1] != len(channels):
        raise ValueError(f"expected {len(channels)} channels, got {pcm.shape[1]}")
    return CultSpatialAudioFrame(
        schema_version=SPATIAL_AUDIO_SCHEMA_ID,
        frame_id=frame_id,
        created_monotonic_ns=time.monotonic_ns(),
        audio_time_ns=audio_time_ns,
        sample_rate=int(sample_rate),
        start_sample=int(start_sample),
        frame_count=int(pcm.shape[0]),
        channel_order=channel_order,
        normalization=normalization,
        channels=channels,
        sample_format="f32le-interleaved",
        interleaved_pcm_f32le=np.ascontiguousarray(pcm, dtype="<f4").tobytes(),
    )


def frame_to_numpy(frame: CultSpatialAudioFrame) -> np.ndarray:
    if frame.sample_format != "f32le-interleaved":
        raise ValueError(f"unsupported sample format: {frame.sample_format}")
    pcm = np.frombuffer(frame.interleaved_pcm_f32le, dtype="<f4")
    return pcm.reshape((frame.frame_count, len(frame.channels))).copy()


def put_live_spatial_audio_frame(path: Path, frame: CultSpatialAudioFrame) -> None:
    def operation() -> None:
        cache = open_audio_cache(path)
        cache.put(SPATIAL_AUDIO_FRAME_DOCUMENT, LIVE_SPATIAL_AUDIO_KEY, frame)

    _retry_cache_io(operation)


def get_live_spatial_audio_frame(path: Path) -> CultSpatialAudioFrame | None:
    def operation() -> CultSpatialAudioFrame | None:
        cache = open_audio_cache(path)
        return cache.get(SPATIAL_AUDIO_FRAME_DOCUMENT, LIVE_SPATIAL_AUDIO_KEY)

    return _retry_cache_io(operation)


def put_audio_stream_status(path: Path, status: CultAudioStreamStatus) -> None:
    def operation() -> None:
        cache = open_audio_cache(path)
        cache.put(AUDIO_STREAM_STATUS_DOCUMENT, AUDIO_STREAM_STATUS_KEY, status)

    _retry_cache_io(operation)


def make_audio_source_events(
    *,
    frame_id: int,
    sample_rate: int,
    start_sample: int,
    frame_count: int,
    audio_time_ns: int,
    events: list[dict[str, Any]],
    voice_focus: list[dict[str, Any]],
) -> CultAudioSourceEvents:
    return CultAudioSourceEvents(
        schema_version=SOURCE_EVENTS_SCHEMA_ID,
        frame_id=int(frame_id),
        audio_time_ns=int(audio_time_ns),
        sample_rate=int(sample_rate),
        start_sample=int(start_sample),
        frame_count=int(frame_count),
        events=tuple(dict(item) for item in events),
        voice_focus=tuple(dict(item) for item in voice_focus),
    )


def put_live_audio_source_events(path: Path, events: CultAudioSourceEvents) -> None:
    def operation() -> None:
        cache = open_audio_cache(path)
        cache.put(SOURCE_EVENTS_DOCUMENT, LIVE_SOURCE_EVENTS_KEY, events)

    _retry_cache_io(operation)


def get_live_audio_source_events(path: Path) -> CultAudioSourceEvents | None:
    def operation() -> CultAudioSourceEvents | None:
        cache = open_audio_cache(path)
        return cache.get(SOURCE_EVENTS_DOCUMENT, LIVE_SOURCE_EVENTS_KEY)

    return _retry_cache_io(operation)


def make_audio_phase_field(
    *,
    frame_id: int,
    sample_rate: int,
    start_sample: int,
    frame_count: int,
    audio_time_ns: int,
    reference_id: str,
    sources: list[dict[str, Any]] | tuple[dict[str, Any], ...],
    global_confidence: float,
    needs_active_probe: bool,
    active_probe_reason: str = "",
) -> CultAudioPhaseField:
    return CultAudioPhaseField(
        schema_version=PHASE_FIELD_SCHEMA_ID,
        frame_id=int(frame_id),
        audio_time_ns=int(audio_time_ns),
        sample_rate=int(sample_rate),
        start_sample=int(start_sample),
        frame_count=int(frame_count),
        reference_id=str(reference_id),
        sources=tuple(dict(item) for item in sources),
        global_confidence=float(global_confidence),
        needs_active_probe=bool(needs_active_probe),
        active_probe_reason=str(active_probe_reason),
    )


def put_live_audio_phase_field(path: Path, field: CultAudioPhaseField) -> None:
    def operation() -> None:
        cache = open_audio_cache(path)
        cache.put(PHASE_FIELD_DOCUMENT, LIVE_PHASE_FIELD_KEY, field)

    _retry_cache_io(operation)


def get_live_audio_phase_field(path: Path) -> CultAudioPhaseField | None:
    def operation() -> CultAudioPhaseField | None:
        cache = open_audio_cache(path)
        return cache.get(PHASE_FIELD_DOCUMENT, LIVE_PHASE_FIELD_KEY)

    return _retry_cache_io(operation)
