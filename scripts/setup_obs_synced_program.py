from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any

import numpy as np
from scipy.io import wavfile
from scipy.signal import resample_poly


DEFAULT_RUN = Path("calibration/runs/audio-program-live-20260518-180226")
DEFAULT_SCENE = "Scene"
RAW_UNSYNCED_INPUTS = (
    "Neighbor PC - Video",
    "Neighbor PC - Focusrite",
    "Neighbor PC - System Audio",
    "Desktop Audio",
    "Mic/Aux",
)


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Build synchronized LocalCastBridge OBS audio stems from the aligned "
            "program timeline and hide raw unsynchronized OBS inputs."
        )
    )
    parser.add_argument("--run", type=Path, default=DEFAULT_RUN)
    parser.add_argument("--scene", default=DEFAULT_SCENE)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=4455)
    parser.add_argument("--password", default="")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--skip-obs", action="store_true")
    parser.add_argument(
        "--disable-other-scene-items",
        action="store_true",
        help="Disable every scene item except the synchronized LocalCastBridge program sources.",
    )
    args = parser.parse_args()

    run = args.run.resolve()
    stem_dir = run / "stems"
    stem_dir.mkdir(parents=True, exist_ok=True)
    manifest = build_stems(run, stem_dir)
    manifest_path = stem_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {manifest_path}")

    if args.skip_obs:
        return 0
    configure_obs(
        stem_manifest=manifest,
        scene=args.scene,
        host=args.host,
        port=args.port,
        password=args.password,
        dry_run=args.dry_run,
        disable_other_scene_items=args.disable_other_scene_items,
    )
    return 0


def build_stems(run: Path, stem_dir: Path) -> dict[str, Any]:
    field_rate, field = read_wav_float(run / "field-program-cleaned.wav")
    field = ensure_2d(field)
    frame_count = field.shape[0]
    events_path = run / "field-program-cleaned.source-events.json"
    events_doc = json.loads(events_path.read_text(encoding="utf-8")) if events_path.exists() else {}

    witness_channels = [channel for channel in (0, 2, 3) if channel < field.shape[1]]
    witness_mix = mix_channels(field, witness_channels) if witness_channels else np.zeros(frame_count, dtype=np.float32)
    host_voice = channel_or_silence(field, 4)
    co_streamer_voice, co_streamer_voice_status = load_co_streamer_surface(
        run / "co_streamer_surfaces" / "aligned_focusrite.wav",
        field_rate,
        frame_count,
        channel_fallback=channel_or_silence(field, 5),
        placeholder_status="placeholder-silence-until-neighbor-focusrite-is-in-aligned-timeline",
        synced_status="synced-from-neighbor-focusrite-surface",
    )
    ambient = make_ambient_stem(witness_mix, host_voice, co_streamer_voice)
    transients = make_transient_stem(witness_mix, events_doc, frame_count)
    local_loopback = load_optional_program_audio(run / "ground_truth_loopback.wav", field_rate, frame_count)
    co_streamer_loopback, co_streamer_loopback_status = load_co_streamer_surface(
        run / "co_streamer_surfaces" / "aligned_loopback.wav",
        field_rate,
        frame_count,
        channel_fallback=np.zeros((frame_count, 2), dtype=np.float32),
        placeholder_status="placeholder-silence-until-neighbor-loopback-is-captured-and-aligned",
        synced_status="synced-from-neighbor-loopback-surface",
        low_signal_status="captured-but-low-confidence-neighbor-loopback-is-near-silent",
        min_rms=1e-4,
    )

    stems = [
        stem_spec(
            "LocalCastBridge - Host Voice",
            "host_voice.wav",
            host_voice,
            field_rate,
            role="dialogue",
            status="synced-from-local-focusrite-anchor",
            position_m=[0.0, 0.0, 1.2],
        ),
        stem_spec(
            "LocalCastBridge - CoStreamer Voice",
            "co_streamer_voice.wav",
            co_streamer_voice,
            field_rate,
            role="dialogue",
            status=co_streamer_voice_status,
            position_m=[0.0, 2.0, 1.2],
        ),
        stem_spec(
            "LocalCastBridge - Ambient",
            "ambient.wav",
            ambient,
            field_rate,
            role="room",
            status="synced-context-mic-field-minus-dialogue-anchors",
            position_m=None,
        ),
        stem_spec(
            "LocalCastBridge - Transients",
            "transients.wav",
            transients,
            field_rate,
            role="localized-transients",
            status="synced-event-gated-context-field",
            position_m=None,
        ),
        stem_spec(
            "LocalCastBridge - CoStreamer Loopback",
            "co_streamer_loopback.wav",
            co_streamer_loopback,
            field_rate,
            role="program-loopback",
            status=co_streamer_loopback_status,
            position_m=None,
        ),
        stem_spec(
            "LocalCastBridge - Local Loopback",
            "local_loopback.wav",
            local_loopback,
            field_rate,
            role="program-loopback",
            status="synced-ground-truth-local-loopback",
            position_m=None,
        ),
    ]

    for stem in stems:
        path = stem_dir / stem["file"]
        wavfile.write(path, field_rate, np.asarray(stem.pop("_samples"), dtype=np.float32))
        stem["path"] = str(path.resolve())

    return {
        "schema_version": "gamecult.localcast.obs_synced_program.v1",
        "run": str(run),
        "sample_rate": field_rate,
        "frame_count": frame_count,
        "duration_seconds": frame_count / field_rate,
        "timeline": "field-program-cleaned.wav",
        "unsynced_inputs_hidden": list(RAW_UNSYNCED_INPUTS),
        "stems": stems,
    }


def stem_spec(
    obs_name: str,
    filename: str,
    samples: np.ndarray,
    sample_rate: int,
    *,
    role: str,
    status: str,
    position_m: list[float] | None,
) -> dict[str, Any]:
    rendered = limit_peak(np.asarray(samples, dtype=np.float32))
    return {
        "obs_name": obs_name,
        "file": filename,
        "channels": 1 if rendered.ndim == 1 else int(rendered.shape[1]),
        "sample_rate": int(sample_rate),
        "role": role,
        "status": status,
        "position_m": position_m,
        "_samples": rendered,
    }


def configure_obs(
    *,
    stem_manifest: dict[str, Any],
    scene: str,
    host: str,
    port: int,
    password: str,
    dry_run: bool,
    disable_other_scene_items: bool,
) -> None:
    import obsws_python as obs

    client = obs.ReqClient(host=host, port=port, password=password, timeout=3)
    existing_inputs = {item["inputName"] for item in client.get_input_list().inputs}
    scene_items = scene_item_ids(client, scene)

    for raw_name in RAW_UNSYNCED_INPUTS:
        if dry_run:
            print(f"would disable/mute raw input: {raw_name}")
            continue
        mute_input(client, raw_name)
        disable_scene_item(client, scene, scene_items, raw_name)

    allowed_scene_items = {"LocalCastBridge Point Cloud"} | {
        str(stem["obs_name"]) for stem in stem_manifest["stems"]
    }

    for stem in stem_manifest["stems"]:
        settings = {
            "is_local_file": True,
            "local_file": stem["path"],
            "looping": True,
            "clear_on_media_end": False,
            "restart_on_activate": True,
            "buffering_mb": 2,
        }
        name = stem["obs_name"]
        if dry_run:
            print(f"would create/update OBS stem: {name} -> {stem['path']}")
            continue
        if name in existing_inputs:
            client.set_input_settings(name, settings, True)
            scene_items = scene_item_ids(client, scene)
            enable_scene_item(client, scene, scene_items, name)
        else:
            client.create_input(scene, name, "ffmpeg_source", settings, True)
            existing_inputs.add(name)
            scene_items = scene_item_ids(client, scene)
        client.set_input_mute(name, False)
        client.set_input_volume(name, vol_mul=1.0)
        restart_media_if_possible(client, name)
    if disable_other_scene_items:
        scene_items = scene_item_ids(client, scene)
        for source_name in sorted(scene_items):
            if source_name in allowed_scene_items:
                continue
            if dry_run:
                print(f"would disable non-program scene item: {source_name}")
                continue
            disable_scene_item(client, scene, scene_items, source_name)
    print("OBS synchronized program scene is configured")


def read_wav_float(path: Path) -> tuple[int, np.ndarray]:
    sample_rate, samples = wavfile.read(path)
    return int(sample_rate), pcm_to_float(samples)


def pcm_to_float(samples: np.ndarray) -> np.ndarray:
    array = np.asarray(samples)
    if np.issubdtype(array.dtype, np.floating):
        return array.astype(np.float32, copy=False)
    if np.issubdtype(array.dtype, np.signedinteger):
        scale = float(max(abs(np.iinfo(array.dtype).min), np.iinfo(array.dtype).max))
        return (array.astype(np.float32) / scale).clip(-1.0, 1.0)
    if np.issubdtype(array.dtype, np.unsignedinteger):
        midpoint = float(np.iinfo(array.dtype).max) / 2.0
        return ((array.astype(np.float32) - midpoint) / midpoint).clip(-1.0, 1.0)
    raise TypeError(f"unsupported WAV dtype: {array.dtype}")


def ensure_2d(samples: np.ndarray) -> np.ndarray:
    if samples.ndim == 1:
        return samples[:, None]
    if samples.ndim != 2:
        raise ValueError("expected mono or interleaved WAV samples")
    return samples


def channel_or_silence(field: np.ndarray, channel: int) -> np.ndarray:
    if channel < field.shape[1]:
        return field[:, channel].copy()
    return np.zeros(field.shape[0], dtype=np.float32)


def mix_channels(field: np.ndarray, channels: list[int]) -> np.ndarray:
    if not channels:
        return np.zeros(field.shape[0], dtype=np.float32)
    return np.mean(field[:, channels], axis=1, dtype=np.float32)


def make_ambient_stem(witness_mix: np.ndarray, *anchors: np.ndarray) -> np.ndarray:
    ambient = witness_mix.astype(np.float32, copy=True)
    for anchor in anchors:
        ambient -= 0.2 * np.asarray(anchor, dtype=np.float32)
    return ambient


def make_transient_stem(witness_mix: np.ndarray, events_doc: dict[str, Any], frame_count: int) -> np.ndarray:
    stem = np.zeros(frame_count, dtype=np.float32)
    events = events_doc.get("events", [])
    fade = 128
    for event in events:
        start = max(0, int(event.get("start_sample", event.get("startSample", 0))) - 256)
        duration = int(event.get("duration_samples", event.get("durationSamples", 1024)))
        end = min(frame_count, start + max(duration + 512, 1))
        if end <= start:
            continue
        segment = witness_mix[start:end].copy()
        fade_len = min(fade, segment.shape[0] // 2)
        if fade_len > 0:
            ramp = np.linspace(0.0, 1.0, fade_len, endpoint=False, dtype=np.float32)
            segment[:fade_len] *= ramp
            segment[-fade_len:] *= ramp[::-1]
        stem[start:end] += segment
    return stem


def load_optional_program_audio(path: Path, sample_rate: int, frame_count: int) -> np.ndarray:
    if not path.exists():
        return np.zeros((frame_count, 2), dtype=np.float32)
    source_rate, samples = read_wav_float(path)
    samples = ensure_2d(samples)
    if source_rate != sample_rate:
        divisor = math.gcd(source_rate, sample_rate)
        samples = resample_poly(samples, sample_rate // divisor, source_rate // divisor, axis=0).astype(np.float32)
    if samples.shape[0] < frame_count:
        pad = np.zeros((frame_count - samples.shape[0], samples.shape[1]), dtype=np.float32)
        samples = np.vstack([samples, pad])
    return samples[:frame_count]


def load_co_streamer_surface(
    path: Path,
    sample_rate: int,
    frame_count: int,
    *,
    channel_fallback: np.ndarray,
    placeholder_status: str,
    synced_status: str,
    low_signal_status: str | None = None,
    min_rms: float = 0.0,
) -> tuple[np.ndarray, str]:
    if not path.exists():
        return channel_fallback, placeholder_status
    loaded = load_optional_program_audio(path, sample_rate, frame_count)
    if np.asarray(channel_fallback).ndim == 1 and loaded.ndim == 2:
        loaded = np.mean(loaded, axis=1, dtype=np.float32)
    if min_rms > 0.0 and rms(loaded) < float(min_rms):
        return loaded, low_signal_status or placeholder_status
    return loaded, synced_status


def rms(samples: np.ndarray) -> float:
    samples = np.asarray(samples, dtype=np.float32)
    return float(np.sqrt(np.mean(samples * samples))) if samples.size else 0.0


def limit_peak(samples: np.ndarray, target: float = 0.98) -> np.ndarray:
    peak = float(np.max(np.abs(samples))) if samples.size else 0.0
    if peak <= target or peak <= 1e-9:
        return samples
    return samples * (target / peak)


def mute_input(client: Any, input_name: str) -> None:
    try:
        client.set_input_mute(input_name, True)
    except Exception:
        return


def scene_item_ids(client: Any, scene: str) -> dict[str, int]:
    try:
        items = client.get_scene_item_list(scene).scene_items
    except Exception:
        return {}
    return {str(item["sourceName"]): int(item["sceneItemId"]) for item in items}


def disable_scene_item(client: Any, scene: str, scene_items: dict[str, int], source_name: str) -> None:
    item_id = scene_items.get(source_name)
    if item_id is None:
        return
    try:
        client.set_scene_item_enabled(scene, item_id, False)
    except Exception:
        return


def enable_scene_item(client: Any, scene: str, scene_items: dict[str, int], source_name: str) -> None:
    item_id = scene_items.get(source_name)
    if item_id is None:
        return
    try:
        client.set_scene_item_enabled(scene, item_id, True)
    except Exception:
        return


def restart_media_if_possible(client: Any, input_name: str) -> None:
    try:
        client.trigger_media_input_action(input_name, "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART")
    except Exception:
        return


if __name__ == "__main__":
    raise SystemExit(main())
