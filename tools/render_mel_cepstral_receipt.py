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
        raw = handle.readframes(handle.getnframes())

    if width == 2:
        data = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
    elif width == 4:
        data = np.frombuffer(raw, dtype=np.float32).astype(np.float32)
    else:
        raise ValueError(f"unsupported sample width: {width}")

    if channels > 1:
        data = data.reshape((-1, channels)).mean(axis=1)
    return sample_rate, data


def hertz_to_mel(hertz: np.ndarray | float) -> np.ndarray | float:
    return 2595.0 * np.log10(1.0 + np.asarray(hertz) / 700.0)


def mel_to_hertz(mel: np.ndarray | float) -> np.ndarray | float:
    return 700.0 * (np.power(10.0, np.asarray(mel) / 2595.0) - 1.0)


def stft_power(samples: np.ndarray, fft_size: int, hop: int) -> np.ndarray:
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
        output[:, frame] = np.maximum(np.abs(spectrum) ** 2, 1.0e-18)
    return output


def mel_filterbank(sample_rate: int, fft_size: int, bands: int, min_hz: float, max_hz: float) -> tuple[np.ndarray, np.ndarray]:
    min_mel = float(hertz_to_mel(min_hz))
    max_mel = float(hertz_to_mel(max_hz))
    points_hz = mel_to_hertz(np.linspace(min_mel, max_mel, bands + 2))
    bins = np.floor((fft_size + 1) * points_hz / sample_rate).astype(int)
    bins = np.clip(bins, 0, fft_size // 2)

    bank = np.zeros((bands, fft_size // 2 + 1), dtype=np.float32)
    for band in range(bands):
        left, center, right = bins[band], bins[band + 1], bins[band + 2]
        if center <= left:
            center = left + 1
        if right <= center:
            right = center + 1
        for index in range(left, min(center, bank.shape[1])):
            bank[band, index] = (index - left) / max(1, center - left)
        for index in range(center, min(right, bank.shape[1])):
            bank[band, index] = (right - index) / max(1, right - center)

    centers = points_hz[1:-1]
    return bank, centers


def dct_matrix(output_count: int, input_count: int) -> np.ndarray:
    scale = math.sqrt(2.0 / input_count)
    matrix = np.empty((output_count, input_count), dtype=np.float32)
    for k in range(output_count):
        for n in range(input_count):
            matrix[k, n] = scale * math.cos(math.pi / input_count * (n + 0.5) * k)
    matrix[0, :] *= 1.0 / math.sqrt(2.0)
    return matrix


def colorize(surface: np.ndarray, floor: float | None = None, ceiling: float | None = None) -> Image.Image:
    finite = surface[np.isfinite(surface)]
    if finite.size == 0:
        floor = 0.0
        ceiling = 1.0
    else:
        if floor is None:
            floor = float(np.percentile(finite, 5.0))
        if ceiling is None:
            ceiling = float(np.percentile(finite, 99.2))
    if ceiling <= floor:
        ceiling = floor + 1.0

    norm = np.clip((surface - floor) / (ceiling - floor), 0.0, 1.0)[::-1, :]
    r = np.clip((norm * 2.15 - 0.20) * 255, 0, 255)
    g = np.clip(np.sin(norm * math.pi) * 255, 0, 255)
    b = np.clip((1.0 - norm * 1.30) * 175, 0, 175)
    return Image.fromarray(np.stack([r, g, b], axis=2).astype(np.uint8), mode="RGB")


def select_active_window(samples: np.ndarray, sample_rate: int, duration: float) -> int:
    window = max(1, int(round(duration * sample_rate)))
    if len(samples) <= window:
        return 0
    hop = max(1, window // 12)
    best_offset = 0
    best_energy = -1.0
    for offset in range(0, len(samples) - window + 1, hop):
        chunk = samples[offset : offset + window]
        energy = float(np.mean(chunk * chunk))
        if energy > best_energy:
            best_energy = energy
            best_offset = offset
    return best_offset


def write_features(
    path: Path,
    log_mel: np.ndarray,
    cepstrum: np.ndarray,
    centers_hz: np.ndarray,
    sample_rate: int,
    hop: int,
) -> None:
    weights = np.exp(log_mel)
    energy = weights.sum(axis=0)
    centroid = (weights * centers_hz[:, None]).sum(axis=0) / np.maximum(energy, 1.0e-12)
    bandwidth = np.sqrt(((centers_hz[:, None] - centroid[None, :]) ** 2 * weights).sum(axis=0) / np.maximum(energy, 1.0e-12))

    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        header = ["frame", "time_seconds", "log_energy", "centroid_hz", "bandwidth_hz"]
        header.extend(f"cepstrum_{index}" for index in range(cepstrum.shape[0]))
        writer.writerow(header)
        for frame in range(log_mel.shape[1]):
            row = [
                frame,
                f"{frame * hop / sample_rate:.6f}",
                f"{math.log(max(float(energy[frame]), 1.0e-12)):.6f}",
                f"{float(centroid[frame]):.3f}",
                f"{float(bandwidth[frame]):.3f}",
            ]
            row.extend(f"{float(value):.6f}" for value in cepstrum[:, frame])
            writer.writerow(row)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--fft", type=int, default=2048)
    parser.add_argument("--hop", type=int, default=96)
    parser.add_argument("--mel-bands", type=int, default=64)
    parser.add_argument("--cepstra", type=int, default=16)
    parser.add_argument("--min-hz", type=float, default=800.0)
    parser.add_argument("--max-hz", type=float, default=18000.0)
    parser.add_argument("--start", type=float, default=-1.0)
    parser.add_argument("--duration", type=float, default=4.0)
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    sample_rate, samples = read_wav_mono(args.input)
    duration = max(0.05, args.duration)
    if args.start >= 0.0:
        start = int(round(args.start * sample_rate))
    else:
        start = select_active_window(samples, sample_rate, duration)
    start = max(0, min(start, len(samples)))
    samples = samples[start : start + max(1, int(round(duration * sample_rate)))]

    power = stft_power(samples, args.fft, args.hop)
    bank, centers = mel_filterbank(sample_rate, args.fft, args.mel_bands, args.min_hz, min(args.max_hz, sample_rate * 0.5))
    mel_power = np.maximum(bank @ power, 1.0e-18)
    log_mel = np.log(mel_power)
    cepstrum = dct_matrix(args.cepstra, args.mel_bands) @ log_mel

    stem = args.input.stem
    log_mel_png = args.output_dir / f"{stem}-log-mel.png"
    cepstrum_png = args.output_dir / f"{stem}-cepstrum.png"
    features_csv = args.output_dir / f"{stem}-mel-cepstral-features.csv"

    width = max(900, log_mel.shape[1] * 4)
    colorize(log_mel).resize((width, 640)).save(log_mel_png)
    colorize(cepstrum).resize((width, 420)).save(cepstrum_png)
    write_features(features_csv, log_mel, cepstrum, centers, sample_rate, args.hop)

    print(f"mel-cepstral input={args.input} sampleRate={sample_rate} startSeconds={start / sample_rate:.6f} samples={len(samples)}")
    print(f"logMel={log_mel_png}")
    print(f"cepstrum={cepstrum_png}")
    print(f"features={features_csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
