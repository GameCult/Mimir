import argparse
from pathlib import Path
import sys
import time

import numpy as np
from scipy.io import wavfile


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from audio_field.cultcache_audio import make_audio_phase_field, put_live_audio_phase_field  # noqa: E402
from audio_field.active_probe import ActiveConfidenceMaintainer  # noqa: E402
from audio_field.phase_meaning import LivePhaseMeaningExtractor  # noqa: E402
from audio_field.probe_optimizer import ActiveProbeOptimizer, ProbePolicy  # noqa: E402


def read_float_wav(path: Path) -> tuple[int, np.ndarray]:
    rate, data = wavfile.read(path)
    samples = data.astype(np.float32)
    if np.issubdtype(data.dtype, np.integer):
        samples /= np.iinfo(data.dtype).max
    if samples.ndim == 1:
        samples = samples[:, None]
    return int(rate), samples


def write_phase_runtime_status(
    path: Path,
    *,
    field: Path,
    reference: Path,
    frame_id: int,
    global_confidence: float,
    needs_active_probe: bool,
    emitted_probe,
    maintain_confidence: bool,
    play_probes: bool,
) -> None:
    import json

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(
            {
                "input_mode": "wav-replay",
                "field": str(field),
                "reference": str(reference),
                "frame_id": int(frame_id),
                "global_confidence": float(global_confidence),
                "needs_active_probe": bool(needs_active_probe),
                "maintain_confidence": bool(maintain_confidence),
                "play_probes": bool(play_probes),
                "probe_feedback_mode": "open-loop-playback" if play_probes else "dry-run",
                "closed_loop_probe_capture": False,
                "last_probe": None
                if emitted_probe is None
                else {
                    "source_id": emitted_probe.request.source_id,
                    "reason": emitted_probe.request.reason,
                    "urgency": emitted_probe.request.urgency,
                    "path": str(emitted_probe.path),
                    "emitted_monotonic_ns": emitted_probe.emitted_monotonic_ns,
                },
                "updated_monotonic_ns": time.monotonic_ns(),
            },
            indent=2,
            sort_keys=True,
        ),
        encoding="utf-8",
    )


def parse_frequencies(values: list[str]) -> list[float]:
    out: list[float] = []
    for value in values:
        out.extend(float(part) for part in value.split(",") if part.strip())
    return out


def make_source_ids(count: int, explicit: list[str]) -> tuple[str, ...]:
    if explicit:
        if len(explicit) != count:
            raise SystemExit(f"expected {count} --source-id values, got {len(explicit)}")
        return tuple(explicit)
    return tuple(f"channel-{index}" for index in range(count))


def source_dicts(meaning) -> list[dict]:
    rows = []
    for source in meaning.sources:
        rows.append(
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
        )
    return rows


def main() -> None:
    parser = argparse.ArgumentParser(description="Publish live extracted phase-field meaning from reference and aligned mic WAVs.")
    parser.add_argument("--field", type=Path, required=True, help="Aligned frames-by-channel mic field WAV.")
    parser.add_argument("--reference", type=Path, required=True, help="Known program/loopback reference WAV.")
    parser.add_argument("--cache", type=Path, default=ROOT / "calibration" / "runs" / "audio-phase-field.msgpack")
    parser.add_argument("--status", type=Path, default=ROOT / "calibration" / "runs" / "audio-phase-field-status.json")
    parser.add_argument("--source-id", action="append", default=[])
    parser.add_argument("--reference-id", default="program-reference")
    parser.add_argument("--frequency", action="append", default=["250,500,1000,2000,4000,8000,12000"])
    parser.add_argument("--chunk-frames", type=int, default=4096)
    parser.add_argument("--hop-frames", type=int, default=2048)
    parser.add_argument("--duration", type=float)
    parser.add_argument("--loop", action="store_true")
    parser.add_argument("--realtime", action="store_true")
    parser.add_argument("--maintain-confidence", action="store_true")
    parser.add_argument("--target-confidence", type=float, default=0.65)
    parser.add_argument("--trigger-confidence", type=float, default=0.35)
    parser.add_argument("--min-probe-interval-frames", type=int, default=45)
    parser.add_argument("--probe-level-dbfs", type=float, default=-18.0)
    parser.add_argument("--probe-output-dir", type=Path, default=ROOT / "calibration" / "runs" / "active-probes")
    parser.add_argument("--probe-seconds", type=float, default=0.03)
    parser.add_argument("--probe-band", default="1800:9000")
    parser.add_argument("--ultrasonic-probes", action="store_true", help="Use a near-ultrasonic probe band below Nyquist.")
    parser.add_argument("--probe-pattern", choices=["single", "harmonic-dense"], default="single")
    parser.add_argument("--cram-harmonic-probes", action="store_true", help="Shortcut for dense near-ultrasonic harmonic probes.")
    parser.add_argument("--harmonic-root-hz", type=float, default=440.0)
    parser.add_argument("--harmonic-voices", type=int, default=36)
    parser.add_argument("--probe-channel", type=int, default=0)
    parser.add_argument("--probe-output-channels", type=int, default=2)
    parser.add_argument("--probe-unmasked", action="store_true", help="Allow probes even when the reference block is not masking them.")
    parser.add_argument("--play-probes", action="store_true", help="Play scheduled probes through the default output device.")
    args = parser.parse_args()

    sample_rate, field = read_float_wav(args.field)
    reference_rate, reference = read_float_wav(args.reference)
    if reference_rate != sample_rate:
        raise SystemExit(f"reference sample rate {reference_rate} does not match field sample rate {sample_rate}")
    reference_mono = reference[:, 0]
    source_ids = make_source_ids(field.shape[1], args.source_id)
    extractor = LivePhaseMeaningExtractor(
        source_ids,
        sample_rate,
        parse_frequencies(args.frequency),
        reference_id=args.reference_id,
    )
    maintainer = None
    if args.maintain_confidence:
        use_ultrasonic = args.ultrasonic_probes or args.cram_harmonic_probes
        probe_pattern = "harmonic-dense" if args.cram_harmonic_probes else args.probe_pattern
        band_start, band_end = ultrasonic_probe_band(sample_rate) if use_ultrasonic else parse_probe_band(args.probe_band)
        maintainer = ActiveConfidenceMaintainer(
            ActiveProbeOptimizer(
                ProbePolicy(
                    target_confidence=args.target_confidence,
                    trigger_confidence=args.trigger_confidence,
                    min_interval_frames=args.min_probe_interval_frames,
                    max_probe_level_dbfs=args.probe_level_dbfs,
                    prefer_masked_windows=not args.probe_unmasked,
                )
            ),
            sample_rate=sample_rate,
            output_dir=args.probe_output_dir,
            duration_seconds=args.probe_seconds,
            start_hz=band_start,
            end_hz=band_end,
            channels=args.probe_output_channels,
            channel=args.probe_channel,
            pattern=probe_pattern,
            harmonic_root_hz=args.harmonic_root_hz,
            harmonic_voices=args.harmonic_voices,
        )

    start_monotonic = time.monotonic()
    audio_origin_ns = time.monotonic_ns()
    frame_id = 0
    cursor = 0
    max_samples = min(len(reference_mono), len(field))
    last_status = 0.0
    while cursor + args.chunk_frames <= max_samples:
        if args.duration is not None and time.monotonic() - start_monotonic >= args.duration:
            break
        block_ref = reference_mono[cursor : cursor + args.chunk_frames]
        block_field = field[cursor : cursor + args.chunk_frames]
        audio_time_ns = audio_origin_ns + int(cursor * 1_000_000_000 / sample_rate)
        meaning = extractor.update(
            block_ref,
            block_field,
            frame_id=frame_id,
            start_sample=cursor,
            audio_time_ns=audio_time_ns,
        )
        put_live_audio_phase_field(
            args.cache,
            make_audio_phase_field(
                frame_id=meaning.frame_id,
                sample_rate=meaning.sample_rate,
                start_sample=meaning.start_sample,
                frame_count=meaning.frame_count,
                audio_time_ns=meaning.audio_time_ns,
                reference_id=meaning.reference_id,
                sources=source_dicts(meaning),
                global_confidence=meaning.global_confidence,
                needs_active_probe=meaning.needs_active_probe,
                active_probe_reason=meaning.active_probe_reason,
            ),
        )
        emitted = None
        if maintainer is not None:
            emitted = maintainer.update(meaning, block_ref, force_masked=args.probe_unmasked)
            if emitted is not None and args.play_probes:
                play_probe_file(emitted.path)
        if time.monotonic() - last_status >= 1.0:
            write_phase_runtime_status(
                args.status,
                field=args.field,
                reference=args.reference,
                frame_id=meaning.frame_id,
                global_confidence=meaning.global_confidence,
                needs_active_probe=meaning.needs_active_probe,
                emitted_probe=emitted,
                maintain_confidence=args.maintain_confidence,
                play_probes=args.play_probes,
            )
            last_status = time.monotonic()
        frame_id += 1
        cursor += args.hop_frames
        if cursor + args.chunk_frames > max_samples and args.loop:
            cursor = 0
        if args.realtime:
            target_time = start_monotonic + cursor / sample_rate
            sleep_for = target_time - time.monotonic()
            if sleep_for > 0:
                time.sleep(sleep_for)


def parse_probe_band(value: str) -> tuple[float, float]:
    parts = value.split(":")
    if len(parts) != 2:
        raise SystemExit(f"probe band must be start:end Hz, got {value!r}")
    return float(parts[0]), float(parts[1])


def ultrasonic_probe_band(sample_rate: int) -> tuple[float, float]:
    nyquist = 0.5 * float(sample_rate)
    end_hz = min(22_000.0, nyquist - 1200.0)
    start_hz = min(18_500.0, end_hz - 2500.0)
    if end_hz <= 16_000.0 or start_hz <= 0.0:
        raise SystemExit(f"sample rate {sample_rate} leaves no useful ultrasonic probe band")
    return start_hz, end_hz


def play_probe_file(path: Path) -> None:
    import sounddevice as sd

    rate, data = wavfile.read(path)
    sd.play(data.astype(np.float32), int(rate), blocking=True)


if __name__ == "__main__":
    main()
