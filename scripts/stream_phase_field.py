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
from audio_field.phase_meaning import LivePhaseMeaningExtractor  # noqa: E402


def read_float_wav(path: Path) -> tuple[int, np.ndarray]:
    rate, data = wavfile.read(path)
    samples = data.astype(np.float32)
    if np.issubdtype(data.dtype, np.integer):
        samples /= np.iinfo(data.dtype).max
    if samples.ndim == 1:
        samples = samples[:, None]
    return int(rate), samples


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
    parser.add_argument("--source-id", action="append", default=[])
    parser.add_argument("--reference-id", default="program-reference")
    parser.add_argument("--frequency", action="append", default=["250,500,1000,2000,4000,8000,12000"])
    parser.add_argument("--chunk-frames", type=int, default=4096)
    parser.add_argument("--hop-frames", type=int, default=2048)
    parser.add_argument("--duration", type=float)
    parser.add_argument("--loop", action="store_true")
    parser.add_argument("--realtime", action="store_true")
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

    start_monotonic = time.monotonic()
    audio_origin_ns = time.monotonic_ns()
    frame_id = 0
    cursor = 0
    max_samples = min(len(reference_mono), len(field))
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
        frame_id += 1
        cursor += args.hop_frames
        if cursor + args.chunk_frames > max_samples and args.loop:
            cursor = 0
        if args.realtime:
            target_time = start_monotonic + cursor / sample_rate
            sleep_for = target_time - time.monotonic()
            if sleep_for > 0:
                time.sleep(sleep_for)


if __name__ == "__main__":
    main()
