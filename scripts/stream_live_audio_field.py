import argparse
import json
import math
from pathlib import Path
import queue
import sys
import time

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from audio_field.active_probe import ActiveConfidenceMaintainer  # noqa: E402
from audio_field.cultcache_audio import (  # noqa: E402
    DEFAULT_MIC_FIELD_CHANNELS,
    make_audio_phase_field,
    make_mic_field_frame,
    put_live_audio_phase_field,
    put_live_mic_field_frame,
)
from audio_field.phase_meaning import LivePhaseMeaningExtractor  # noqa: E402
from audio_field.probe_optimizer import ActiveProbeOptimizer, ProbePolicy  # noqa: E402
from localcast.sensor_fusion import camera_sensor_id_for_microphone, phase_source_id_for_microphone  # noqa: E402


HOST_SOURCE_BY_MIC_ID = {
    "mic_focusrite_local": "host-focusrite",
    "mic_focusrite_neighbor": "co-streamer-focusrite",
}


def load_profile(path: Path) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def source_id_for_microphone(mic: dict) -> str | None:
    return str(mic.get("phaseSourceId") or mic.get("sourceId") or HOST_SOURCE_BY_MIC_ID.get(str(mic.get("id", ""))) or phase_source_id_for_microphone(mic) or "")


def local_microphones(profile: dict, machine: str) -> list[dict]:
    return [mic for mic in profile.get("microphones", []) if mic.get("machine") == machine and source_id_for_microphone(mic)]


def summarize_device(device: dict) -> dict:
    return {
        "index": int(device["index"]),
        "name": str(device["name"]),
        "hostapi": str(device.get("hostapi_name", "")),
        "inputs": int(device.get("max_input_channels", 0)),
        "outputs": int(device.get("max_output_channels", 0)),
        "default_samplerate": float(device.get("default_samplerate", 0.0)),
    }


def find_devices(sd, spec: dict, direction: str) -> list[dict]:
    hostapis = sd.query_hostapis()
    devices = sd.query_devices()
    query = str(spec.get("query") or "").lower()
    hostapi_name = str(spec.get("hostApi") or "").lower()
    needed_channels = int(spec.get("channels") or 1)
    channel_key = "max_input_channels" if direction == "input" else "max_output_channels"
    matches = []
    for device in devices:
        if int(device[channel_key]) < needed_channels:
            continue
        hostapi = hostapis[device["hostapi"]]["name"]
        if query and query not in str(device["name"]).lower():
            continue
        if hostapi_name and hostapi_name not in str(hostapi).lower():
            continue
        matches.append({**device, "hostapi_name": hostapi})
    return matches


def match_indexed_device(sd, spec: dict, direction: str) -> dict:
    matches = find_devices(sd, spec, direction)
    match_index = int(spec.get("matchIndex", 0))
    if match_index < 0 or match_index >= len(matches):
        raise RuntimeError(f"No {direction} device match {match_index} for {spec!r}; found {len(matches)}")
    return matches[match_index]


def local_device_groups(sd, mics: list[dict]) -> tuple[dict[str, dict], list[dict]]:
    groups: dict[str, dict] = {}
    missing = []
    for mic in mics:
        try:
            device = match_indexed_device(sd, mic.get("device", {}), "input")
        except RuntimeError as exc:
            missing.append({"micId": mic.get("id"), "sourceId": source_id_for_microphone(mic), "error": str(exc)})
            continue
        channel = int(mic.get("device", {}).get("channel", 0))
        key = str(device["index"])
        groups.setdefault(key, {"device": device, "mics": [], "channels": channel + 1})
        groups[key]["mics"].append(mic)
        groups[key]["channels"] = max(int(groups[key]["channels"]), channel + 1)
    return groups, missing


class InputGroupBuffer:
    def __init__(self) -> None:
        self.queue: queue.Queue[np.ndarray] = queue.Queue()
        self.leftover = np.zeros((0, 0), dtype=np.float32)

    def push(self, block: np.ndarray) -> None:
        self.queue.put(np.asarray(block, dtype=np.float32).copy())

    def read(self, frames: int, channels: int, timeout: float = 1.0) -> np.ndarray:
        if self.leftover.shape[1] != channels:
            self.leftover = np.zeros((0, channels), dtype=np.float32)
        chunks = [self.leftover]
        total = int(self.leftover.shape[0])
        deadline = time.monotonic() + float(timeout)
        while total < frames and time.monotonic() < deadline:
            try:
                item = self.queue.get(timeout=max(0.01, min(0.1, deadline - time.monotonic())))
            except queue.Empty:
                continue
            if item.ndim == 1:
                item = item[:, None]
            if item.shape[1] < channels:
                padded = np.zeros((item.shape[0], channels), dtype=np.float32)
                padded[:, : item.shape[1]] = item
                item = padded
            chunks.append(item[:, :channels])
            total += int(item.shape[0])
        merged = np.concatenate(chunks, axis=0) if chunks else np.zeros((0, channels), dtype=np.float32)
        if merged.shape[0] < frames:
            padded = np.zeros((frames, channels), dtype=np.float32)
            padded[: merged.shape[0]] = merged
            self.leftover = np.zeros((0, channels), dtype=np.float32)
            return padded
        block = merged[:frames]
        self.leftover = merged[frames:]
        return block


def make_source_rows(meaning) -> list[dict]:
    return [
        {
            "sourceId": source.source_id,
            "channel": source.channel,
            "delaySamples": source.smoothed_delay_samples,
            "delayMs": source.delay_ms,
            "distanceDeltaMeters": source.distance_delta_m,
            "coherence": source.coherence,
            "fitErrorRadians": source.fit_error_radians,
            "confidence": source.confidence,
            "referenceBleed": source.reference_bleed,
            "suppressionWeight": source.suppression_weight,
            "correctionEnergy": source.correction_energy,
        }
        for source in meaning.sources
    ]


def write_status(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({**payload, "updated_monotonic_ns": time.monotonic_ns()}, indent=2, sort_keys=True), encoding="utf-8")


def open_loopback_recorder(query: str, sample_rate: int, channels: int):
    import soundcard as sc

    matches = [
        mic
        for mic in sc.all_microphones(include_loopback=True)
        if "loopback" in repr(mic).lower() and query.lower() in mic.name.lower()
    ]
    if not matches:
        raise RuntimeError(f"No loopback device matching {query!r}")
    return matches[0].recorder(samplerate=sample_rate, channels=channels)


def play_probe_file(path: Path, *, device: int | None = None, output_rate: int | None = None) -> None:
    from scipy.io import wavfile
    from scipy import signal
    import sounddevice as sd

    rate, data = wavfile.read(path)
    data = np.asarray(data, dtype=np.float32)
    playback_rate = int(output_rate or rate)
    if playback_rate != int(rate):
        divisor = math.gcd(int(rate), playback_rate)
        data = signal.resample_poly(data, playback_rate // divisor, int(rate) // divisor, axis=0).astype(np.float32)
    sd.play(data, playback_rate, device=device, blocking=False)


def main() -> None:
    parser = argparse.ArgumentParser(description="Capture local live mics into Faust/phase CultCache fields.")
    parser.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.json")
    parser.add_argument("--fallback-profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    parser.add_argument("--machine", default="local")
    parser.add_argument("--mic-cache", type=Path, default=ROOT / "calibration" / "runs" / "audio-mic-field.msgpack")
    parser.add_argument("--phase-cache", type=Path, default=ROOT / "calibration" / "runs" / "audio-phase-field.msgpack")
    parser.add_argument("--status", type=Path, default=ROOT / "calibration" / "runs" / "live-audio-field-status.json")
    parser.add_argument("--phase-status", type=Path, default=ROOT / "calibration" / "runs" / "audio-phase-field-status.json")
    parser.add_argument("--sample-rate", type=int)
    parser.add_argument("--chunk-frames", type=int, default=1024)
    parser.add_argument("--duration", type=float)
    parser.add_argument("--loopback-query", default="Scarlett")
    parser.add_argument("--loopback-channels", type=int, default=2)
    parser.add_argument("--no-loopback", action="store_true")
    parser.add_argument("--maintain-confidence", action="store_true")
    parser.add_argument("--target-confidence", type=float, default=0.72)
    parser.add_argument("--trigger-confidence", type=float, default=0.45)
    parser.add_argument("--min-probe-interval-frames", type=int, default=1)
    parser.add_argument("--probe-level-dbfs", type=float, default=-24.0)
    parser.add_argument("--probe-seconds", type=float, default=0.08)
    parser.add_argument("--probe-output-dir", type=Path, default=ROOT / "calibration" / "runs" / "active-probes")
    parser.add_argument("--probe-artifact-slots", type=int, default=512)
    parser.add_argument("--probe-manifest-max-bytes", type=int, default=1_000_000)
    parser.add_argument("--probe-output-query", default="Scarlett")
    parser.add_argument("--probe-output-hostapi", default="WASAPI")
    parser.add_argument("--probe-output-match-index", type=int, default=0)
    parser.add_argument("--probe-start-hz", type=float, default=18500.0)
    parser.add_argument("--probe-end-hz", type=float, default=22000.0)
    parser.add_argument("--harmonic-root-hz", type=float, default=440.0)
    parser.add_argument("--harmonic-voices", type=int, default=48)
    parser.add_argument("--play-probes", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    profile_path = args.profile if args.profile.exists() else args.fallback_profile
    profile = load_profile(profile_path)
    sample_rate = int(args.sample_rate or profile.get("sampleRate", 48000))
    channels = DEFAULT_MIC_FIELD_CHANNELS
    source_index = {source_id: index for index, source_id in enumerate(channels)}
    mics = local_microphones(profile, args.machine)

    import sounddevice as sd

    groups, missing = local_device_groups(sd, mics)
    status_base = {
        "input_mode": "live-local-capture",
        "profile": str(profile_path),
        "sample_rate": sample_rate,
        "channels": list(channels),
        "local_mics": [
            {
                "micId": mic.get("id"),
                "sourceId": source_id_for_microphone(mic),
                "cameraSensorId": camera_sensor_id_for_microphone(mic),
                "device": mic.get("device"),
            }
            for mic in mics
        ],
        "missing": missing,
        "device_groups": [
            {"device": summarize_device(group["device"]), "channels": int(group["channels"]), "mics": [mic.get("id") for mic in group["mics"]]}
            for group in groups.values()
        ],
    }
    if args.dry_run:
        write_status(args.status, {**status_base, "running": False, "dry_run": True})
        print(json.dumps(status_base, indent=2))
        return

    buffers = {key: InputGroupBuffer() for key in groups}
    streams = []
    for key, group in groups.items():
        buffer = buffers[key]

        def callback(indata, frame_count, time_info, status, *, target=buffer):
            target.push(indata)

        streams.append(
            sd.InputStream(
                device=group["device"]["index"],
                channels=int(group["channels"]),
                samplerate=sample_rate,
                dtype="float32",
                callback=callback,
            )
        )

    loopback_context = None
    loopback_recorder = None
    loopback_error = ""
    if not args.no_loopback:
        try:
            loopback_context = open_loopback_recorder(args.loopback_query, sample_rate, args.loopback_channels)
            loopback_recorder = loopback_context.__enter__()
        except Exception as exc:
            loopback_error = repr(exc)
            loopback_context = None
            loopback_recorder = None
    probe_output_device = None
    probe_output_error = ""
    if args.play_probes:
        try:
            probe_output_device = match_indexed_device(
                sd,
                {
                    "query": args.probe_output_query,
                    "hostApi": args.probe_output_hostapi,
                    "channels": 2,
                    "matchIndex": args.probe_output_match_index,
                },
                "output",
            )
        except Exception as exc:
            probe_output_error = repr(exc)

    extractor = LivePhaseMeaningExtractor(channels, sample_rate, [250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 12000.0])
    maintainer = None
    if args.maintain_confidence:
        live_source_ids = {source_id_for_microphone(mic) for group in groups.values() for mic in group["mics"]}
        maintainer = ActiveConfidenceMaintainer(
            ActiveProbeOptimizer(
                ProbePolicy(
                    target_confidence=args.target_confidence,
                    trigger_confidence=args.trigger_confidence,
                    min_interval_frames=args.min_probe_interval_frames,
                    max_probe_level_dbfs=args.probe_level_dbfs,
                    prefer_masked_windows=False,
                )
            ),
            sample_rate=sample_rate,
            output_dir=args.probe_output_dir,
            duration_seconds=args.probe_seconds,
            start_hz=args.probe_start_hz,
            end_hz=args.probe_end_hz,
            channels=2,
            channel=0,
            pattern="harmonic-dense",
            harmonic_root_hz=args.harmonic_root_hz,
            harmonic_voices=args.harmonic_voices,
            max_artifacts=args.probe_artifact_slots,
            manifest_max_bytes=args.probe_manifest_max_bytes,
            eligible_source_ids={source_id for source_id in live_source_ids if source_id},
        )
    start = time.monotonic()
    origin_ns = time.monotonic_ns()
    frame_id = 0
    last_status = 0.0
    last_probe = None
    probe_playback_error = ""
    try:
        for stream in streams:
            stream.start()
        while True:
            now = time.monotonic()
            if args.duration is not None and now - start >= args.duration:
                break
            field = np.zeros((args.chunk_frames, len(channels)), dtype=np.float32)
            for key, group in groups.items():
                block = buffers[key].read(args.chunk_frames, int(group["channels"]))
                for mic in group["mics"]:
                    source_id = source_id_for_microphone(mic)
                    if source_id not in source_index:
                        continue
                    device_channel = int(mic.get("device", {}).get("channel", 0))
                    if device_channel < block.shape[1]:
                        field[:, source_index[source_id]] = block[:, device_channel]

            if loopback_recorder is not None:
                reference_block = loopback_recorder.record(numframes=args.chunk_frames)
                reference = np.asarray(reference_block, dtype=np.float32)
                if reference.ndim == 2:
                    reference = np.mean(reference, axis=1)
            else:
                reference = np.zeros(args.chunk_frames, dtype=np.float32)

            audio_time_ns = origin_ns + int(frame_id * args.chunk_frames * 1_000_000_000 / sample_rate)
            put_live_mic_field_frame(
                args.mic_cache,
                make_mic_field_frame(field, frame_id=frame_id, sample_rate=sample_rate, start_sample=frame_id * args.chunk_frames, audio_time_ns=audio_time_ns, channels=channels),
            )
            meaning = extractor.update(reference, field, frame_id=frame_id, start_sample=frame_id * args.chunk_frames, audio_time_ns=audio_time_ns)
            put_live_audio_phase_field(
                args.phase_cache,
                make_audio_phase_field(
                    frame_id=meaning.frame_id,
                    sample_rate=meaning.sample_rate,
                    start_sample=meaning.start_sample,
                    frame_count=meaning.frame_count,
                    audio_time_ns=meaning.audio_time_ns,
                    reference_id="local-loopback-live",
                    sources=make_source_rows(meaning),
                    global_confidence=meaning.global_confidence,
                    needs_active_probe=meaning.needs_active_probe,
                    active_probe_reason=meaning.active_probe_reason,
                ),
            )
            emitted = None
            if maintainer is not None:
                emitted = maintainer.update(meaning, reference, force_masked=True)
                if emitted is not None:
                    last_probe = {
                        "source_id": emitted.request.source_id,
                        "reason": emitted.request.reason,
                        "urgency": emitted.request.urgency,
                        "path": str(emitted.path),
                        "emitted_monotonic_ns": emitted.emitted_monotonic_ns,
                    }
                if emitted is not None and args.play_probes:
                    try:
                        play_probe_file(
                            emitted.path,
                            device=None if probe_output_device is None else int(probe_output_device["index"]),
                            output_rate=None
                            if probe_output_device is None
                            else int(round(float(probe_output_device["default_samplerate"]))),
                        )
                        probe_playback_error = ""
                    except Exception as exc:
                        probe_playback_error = repr(exc)
            if now - last_status >= 1.0:
                live_payload = {
                    **status_base,
                    "running": True,
                    "dry_run": False,
                    "frame_id": frame_id,
                    "closed_loop_probe_capture": loopback_recorder is not None,
                    "loopback_query": args.loopback_query,
                    "loopback_error": loopback_error,
                    "probe_output": None if probe_output_device is None else summarize_device(probe_output_device),
                    "probe_output_error": probe_output_error,
                    "probe_playback_error": probe_playback_error,
                    "global_confidence": meaning.global_confidence,
                    "needs_active_probe": meaning.needs_active_probe,
                    "maintain_confidence": args.maintain_confidence,
                    "play_probes": args.play_probes,
                    "last_probe": last_probe,
                }
                write_status(args.status, live_payload)
                write_status(
                    args.phase_status,
                    {
                        "input_mode": "live-local-capture",
                        "field": "sounddevice-local-mics",
                        "reference": "soundcard-loopback" if loopback_recorder is not None else "missing-loopback-zero-reference",
                        "frame_id": frame_id,
                        "global_confidence": meaning.global_confidence,
                        "needs_active_probe": meaning.needs_active_probe,
                        "maintain_confidence": args.maintain_confidence,
                        "play_probes": args.play_probes,
                        "probe_feedback_mode": "closed-loop-capture" if loopback_recorder is not None else "missing-loopback",
                        "closed_loop_probe_capture": loopback_recorder is not None,
                        "probe_playback_error": probe_playback_error,
                        "last_probe": last_probe,
                    },
                )
                last_status = now
            frame_id += 1
    finally:
        for stream in streams:
            try:
                stream.stop()
                stream.close()
            except Exception:
                pass
        if loopback_context is not None:
            try:
                loopback_context.__exit__(None, None, None)
            except Exception:
                pass
        write_status(args.status, {**status_base, "running": False, "stopped": True})


if __name__ == "__main__":
    main()
