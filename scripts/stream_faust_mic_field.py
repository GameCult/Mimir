import argparse
from pathlib import Path
import sys
import time

import numpy as np
from scipy.io import wavfile


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from audio_field.cultcache_audio import (  # noqa: E402
    DEFAULT_MIC_FIELD_CHANNELS,
    get_live_mic_field_frame,
    make_mic_field_frame,
    put_live_mic_field_frame,
)


def read_float_wav(path: Path) -> tuple[int, np.ndarray]:
    rate, data = wavfile.read(path)
    samples = data.astype(np.float32)
    if np.issubdtype(data.dtype, np.integer):
        samples /= np.iinfo(data.dtype).max
    if samples.ndim == 1:
        samples = samples[:, None]
    return int(rate), samples


def source_block(samples: np.ndarray, start_sample: int, frame_count: int, *, loop: bool) -> np.ndarray | None:
    if start_sample >= len(samples) and not loop:
        return None
    block = np.zeros((frame_count, samples.shape[1]), dtype=np.float32)
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
    parser = argparse.ArgumentParser(description="Publish aligned mic channels as a Faust-ready voice-separation input field.")
    parser.add_argument("--input", type=Path, default=ROOT / "calibration" / "runs" / "audio-program-live-20260518-180226" / "field-program-cleaned.wav")
    parser.add_argument("--cache", type=Path, default=ROOT / "calibration" / "runs" / "audio-mic-field.msgpack")
    parser.add_argument("--graph-id", default="localcast.faust.voice_separation.v1")
    parser.add_argument("--channel", action="append")
    parser.add_argument("--chunk-frames", type=int, default=1024)
    parser.add_argument("--duration", type=float)
    parser.add_argument("--loop", action="store_true")
    parser.add_argument("--realtime", action="store_true")
    parser.add_argument("--smoke-readback", action="store_true")
    args = parser.parse_args()

    sample_rate, source = read_float_wav(args.input)
    channels = tuple(args.channel) if args.channel else DEFAULT_MIC_FIELD_CHANNELS
    if len(channels) != source.shape[1]:
        raise SystemExit(f"input has {source.shape[1]} channels but {len(channels)} channel ids were provided")

    start_monotonic = time.monotonic()
    audio_origin_ns = time.monotonic_ns()
    frame_id = 0
    start_sample = 0
    while True:
        now = time.monotonic()
        if args.duration is not None and now - start_monotonic >= args.duration:
            break
        block = source_block(source, start_sample, args.chunk_frames, loop=args.loop)
        if block is None:
            break
        audio_time_ns = audio_origin_ns + int(start_sample * 1_000_000_000 / sample_rate)
        frame = make_mic_field_frame(
            block,
            frame_id=frame_id,
            sample_rate=sample_rate,
            start_sample=start_sample,
            audio_time_ns=audio_time_ns,
            channels=channels,
            graph_id=args.graph_id,
        )
        put_live_mic_field_frame(args.cache, frame)
        if args.smoke_readback:
            loaded = get_live_mic_field_frame(args.cache)
            if loaded is None or loaded.frame_id != frame_id:
                raise SystemExit("Mic-field CultCache smoke readback failed")
        frame_id += 1
        start_sample += args.chunk_frames
        if args.realtime:
            target_time = start_monotonic + start_sample / sample_rate
            sleep_for = target_time - time.monotonic()
            if sleep_for > 0:
                time.sleep(sleep_for)


if __name__ == "__main__":
    main()
