import argparse
from pathlib import Path
import sys
import time

import numpy as np
from scipy.io import wavfile


ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from audio_field.cultcache_audio import (  # noqa: E402
    AUDIO_STREAM_STATUS_SCHEMA_ID,
    CultAudioStreamStatus,
    get_live_spatial_audio_frame,
    make_spatial_audio_frame,
    put_audio_stream_status,
    put_live_spatial_audio_frame,
)


def read_float_wav(path: Path) -> tuple[int, np.ndarray]:
    rate, data = wavfile.read(path)
    samples = data.astype(np.float32)
    if np.issubdtype(data.dtype, np.integer):
        samples /= np.iinfo(data.dtype).max
    if samples.ndim == 1:
        samples = samples[:, None]
    return int(rate), samples


def synthetic_ambix(sample_rate: int, start_sample: int, frame_count: int) -> np.ndarray:
    t = (np.arange(frame_count, dtype=np.float32) + float(start_sample)) / float(sample_rate)
    w = 0.05 * np.sin(2.0 * np.pi * 220.0 * t)
    az = 2.0 * np.pi * 0.08 * t
    y = 0.03 * np.sin(2.0 * np.pi * 330.0 * t) * np.sin(az)
    z = 0.01 * np.sin(2.0 * np.pi * 110.0 * t)
    x = 0.03 * np.sin(2.0 * np.pi * 330.0 * t) * np.cos(az)
    return np.stack([w, y, z, x], axis=1).astype(np.float32)


def source_block(samples: np.ndarray | None, sample_rate: int, start_sample: int, frame_count: int, *, loop: bool) -> np.ndarray | None:
    if samples is None:
        return synthetic_ambix(sample_rate, start_sample, frame_count)
    if samples.shape[1] != 4:
        raise SystemExit(f"AmbiX source must have 4 channels, got {samples.shape[1]}")
    if start_sample >= len(samples) and not loop:
        return None
    block = np.zeros((frame_count, 4), dtype=np.float32)
    written = 0
    cursor = start_sample
    while written < frame_count:
        if cursor >= len(samples):
            if not loop:
                break
            cursor %= len(samples)
        count = min(frame_count - written, len(samples) - cursor)
        block[written : written + count] = samples[cursor : cursor + count]
        written += count
        cursor += count
    return block


def main() -> None:
    parser = argparse.ArgumentParser(description="Diagnostic replay of AmbiX spatial audio blocks into typed CultCache state.")
    parser.add_argument("--input", type=Path, default=ROOT / "calibration" / "runs" / "audio-full-sync-20260518-165751" / "field-foa-ambix.wav")
    parser.add_argument("--cache", type=Path, default=ROOT / "calibration" / "runs" / "audio-state.msgpack")
    parser.add_argument("--status-cache", type=Path, default=ROOT / "calibration" / "runs" / "audio-stream-status.msgpack")
    parser.add_argument("--stream-name", default="Mimir AmbiX")
    parser.add_argument("--chunk-frames", type=int, default=1024)
    parser.add_argument("--duration", type=float)
    parser.add_argument("--loop", action="store_true")
    parser.add_argument("--synthetic", action="store_true")
    parser.add_argument("--smoke-readback", action="store_true")
    parser.add_argument("--max-lag-ms", type=float, default=250.0)
    args = parser.parse_args()

    source = None
    sample_rate = 48000
    if not args.synthetic:
        sample_rate, source = read_float_wav(args.input)
        if sample_rate != 48000:
            raise SystemExit(f"Expected 48 kHz AmbiX source for Aquarium/Faust stream, got {sample_rate}")

    start_monotonic = time.monotonic()
    audio_origin_ns = time.monotonic_ns()
    frame_id = 0
    start_sample = 0
    last_status = 0.0
    while True:
        now = time.monotonic()
        if args.duration is not None and now - start_monotonic >= args.duration:
            break
        realtime_start_sample = int((now - start_monotonic) * sample_rate)
        max_lag_samples = int(float(args.max_lag_ms) * sample_rate / 1000.0)
        if start_sample + args.chunk_frames < realtime_start_sample - max_lag_samples:
            start_sample = max(0, realtime_start_sample - (realtime_start_sample % args.chunk_frames))
        block = source_block(source, sample_rate, start_sample, args.chunk_frames, loop=args.loop)
        if block is None:
            break
        audio_time_ns = audio_origin_ns + int(start_sample * 1_000_000_000 / sample_rate)
        frame = make_spatial_audio_frame(
            block,
            frame_id=frame_id,
            sample_rate=sample_rate,
            start_sample=start_sample,
            audio_time_ns=audio_time_ns,
        )
        put_live_spatial_audio_frame(args.cache, frame)
        if args.smoke_readback:
            loaded = get_live_spatial_audio_frame(args.cache)
            if loaded is None or loaded.frame_id != frame_id:
                raise SystemExit("Audio CultCache smoke readback failed")
        if now - last_status >= 1.0:
            put_audio_stream_status(
                args.status_cache,
                CultAudioStreamStatus(
                    schema_version=AUDIO_STREAM_STATUS_SCHEMA_ID,
                    stream_name=args.stream_name,
                    frames_sent=frame_id + 1,
                    sample_rate=sample_rate,
                    frame_count=args.chunk_frames,
                    channels=("W", "Y", "Z", "X"),
                    updated_monotonic_ns=time.monotonic_ns(),
                    last_error="",
                ),
            )
            last_status = now
        frame_id += 1
        start_sample += args.chunk_frames
        target_time = start_monotonic + start_sample / sample_rate
        sleep_for = target_time - time.monotonic()
        if sleep_for > 0:
            time.sleep(sleep_for)

    put_audio_stream_status(
        args.status_cache,
        CultAudioStreamStatus(
            schema_version=AUDIO_STREAM_STATUS_SCHEMA_ID,
            stream_name=args.stream_name,
            frames_sent=frame_id,
            sample_rate=sample_rate,
            frame_count=args.chunk_frames,
            channels=("W", "Y", "Z", "X"),
            updated_monotonic_ns=time.monotonic_ns(),
            last_error="stopped",
        ),
    )


if __name__ == "__main__":
    main()
