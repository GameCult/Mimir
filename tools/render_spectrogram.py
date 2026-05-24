import argparse
import csv
import math
import wave
from pathlib import Path

import numpy as np
from PIL import Image


def read_wav_mono(path: Path) -> tuple[int, np.ndarray]:
    with wave.open(str(path), "rb") as handle:
        sample_rate = handle.getframerate()
        channels = handle.getnchannels()
        width = handle.getsampwidth()
        frames = handle.getnframes()
        raw = handle.readframes(frames)

    if width == 2:
        data = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
    elif width == 4:
        data = np.frombuffer(raw, dtype=np.float32).astype(np.float32)
    else:
        raise ValueError(f"unsupported sample width: {width}")

    if channels > 1:
        data = data.reshape((-1, channels)).mean(axis=1)
    return sample_rate, data


def stft(samples: np.ndarray, fft_size: int, hop: int) -> np.ndarray:
    if len(samples) < fft_size:
        samples = np.pad(samples, (0, fft_size - len(samples)))
    frame_count = 1 + max(0, (len(samples) - fft_size) // hop)
    window = np.hanning(fft_size).astype(np.float32)
    output = np.empty((fft_size // 2 + 1, frame_count), dtype=np.float32)
    for frame in range(frame_count):
        offset = frame * hop
        segment = samples[offset : offset + fft_size]
        if len(segment) < fft_size:
            segment = np.pad(segment, (0, fft_size - len(segment)))
        spectrum = np.fft.rfft(segment * window)
        output[:, frame] = np.abs(spectrum)
    return output


def colorize(db: np.ndarray, floor_db: float, ceiling_db: float) -> Image.Image:
    norm = np.clip((db - floor_db) / (ceiling_db - floor_db), 0.0, 1.0)
    norm = norm[::-1, :]
    r = np.clip((norm * 2.2 - 0.25) * 255, 0, 255)
    g = np.clip(np.sin(norm * math.pi) * 255, 0, 255)
    b = np.clip((1.0 - norm * 1.25) * 160, 0, 160)
    rgb = np.stack([r, g, b], axis=2).astype(np.uint8)
    return Image.fromarray(rgb, mode="RGB")


def write_peak_csv(path: Path, magnitudes: np.ndarray, sample_rate: int, fft_size: int, hop: int, peak_count: int) -> None:
    freqs = np.fft.rfftfreq(fft_size, 1.0 / sample_rate)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["frame", "time_seconds", "rank", "frequency_hz", "magnitude_db"])
        for frame in range(magnitudes.shape[1]):
            column = magnitudes[:, frame]
            indexes = np.argpartition(column, -peak_count)[-peak_count:]
            indexes = indexes[np.argsort(column[indexes])[::-1]]
            for rank, index in enumerate(indexes, start=1):
                writer.writerow([
                    frame,
                    f"{frame * hop / sample_rate:.6f}",
                    rank,
                    f"{freqs[index]:.3f}",
                    f"{20.0 * math.log10(max(float(column[index]), 1.0e-9)):.3f}",
                ])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--fft", type=int, default=2048)
    parser.add_argument("--hop", type=int, default=128)
    parser.add_argument("--floor-db", type=float, default=-95.0)
    parser.add_argument("--ceiling-db", type=float, default=-18.0)
    parser.add_argument("--peaks", type=int, default=8)
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    sample_rate, samples = read_wav_mono(args.input)
    magnitudes = stft(samples, args.fft, args.hop)
    db = 20.0 * np.log10(np.maximum(magnitudes, 1.0e-9))

    stem = args.input.stem
    linear_png = args.output_dir / f"{stem}-spectrogram-linear.png"
    log_png = args.output_dir / f"{stem}-spectrogram-log.png"
    peaks_csv = args.output_dir / f"{stem}-spectral-peaks.csv"

    colorize(db, args.floor_db, args.ceiling_db).resize((max(800, db.shape[1] * 3), 640)).save(linear_png)

    # Log-frequency display by sampling rows at exponentially spaced bins.
    rows = 512
    max_bin = db.shape[0] - 1
    log_bins = np.geomspace(1, max_bin, rows).astype(int)
    log_db = db[log_bins, :]
    colorize(log_db, args.floor_db, args.ceiling_db).resize((max(800, db.shape[1] * 3), 640)).save(log_png)
    write_peak_csv(peaks_csv, magnitudes, sample_rate, args.fft, args.hop, args.peaks)

    print(f"spectrogram input={args.input} sampleRate={sample_rate} samples={len(samples)}")
    print(f"linear={linear_png}")
    print(f"log={log_png}")
    print(f"peaks={peaks_csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
