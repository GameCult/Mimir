#!/usr/bin/env python3
"""Estimate PS Move light latency from a camera plus Scarlett mic witness.

This is a diagnostic receipt generator, not a runtime timing authority.
It records a camera while Starfire plays a chirp train into the room and
Nightwing flashes the PS Move sphere on the same cadence. The analysis detects
chirp peaks in the Scarlett cardioid mic and temporal brightening peaks in the
camera feed, then reports the visual-minus-audio offset seen by those witnesses.
"""

from __future__ import annotations

import argparse
import base64
import json
import math
import os
import shutil
import struct
import subprocess
import sys
import time
import wave
import re
from pathlib import Path

import numpy as np
from PIL import Image


DEFAULT_FFMPEG = (
    r"C:\Users\Meta\AppData\Local\Microsoft\WinGet\Packages"
    r"\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe"
    r"\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe"
)
DEFAULT_FFPLAY = str(Path(DEFAULT_FFMPEG).with_name("ffplay.exe"))
DEFAULT_FFPROBE = str(Path(DEFAULT_FFMPEG).with_name("ffprobe.exe"))
DEFAULT_KIYO_PRO_VIDEO = (
    r"@device_pnp_\\?\usb#vid_1532&pid_0e05&mi_00#9&3c07f79&0&0000"
    r"#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"
)
DEFAULT_PS3_EYE_0_VIDEO = "PS3 Eye Universal"
DEFAULT_PS3_EYE_1_VIDEO = "PS3 Eye Universal2"
DEFAULT_SCARLETT_CARDIOID = "Analogue 1 + 2 (2- Focusrite USB Audio)"
DEFAULT_KIYO_PRO_MIC = "Microphone (7- Razer Kiyo Pro)"

VIDEO_PRESETS = {
    "ps3-eye-0": (DEFAULT_PS3_EYE_0_VIDEO, 320, 240, 60),
    "ps3-eye-1": (DEFAULT_PS3_EYE_1_VIDEO, 320, 240, 60),
    "kiyo-pro": (DEFAULT_KIYO_PRO_VIDEO, 640, 480, 30),
}

AUDIO_PRESETS = {
    "scarlett-cardioid": DEFAULT_SCARLETT_CARDIOID,
    "kiyo-pro": DEFAULT_KIYO_PRO_MIC,
}


REMOTE_LED = r"""
import os
import sys
import time

hidraw = sys.argv[1]
events = []
for part in sys.argv[2].split(","):
    if not part:
        continue
    fields = part.split(":")
    events.append((float(fields[0]), int(fields[1]), int(fields[2]), int(fields[3])))
pulse_seconds = float(sys.argv[3])
start_delay = float(sys.argv[4])
log = os.path.expanduser("~/.local/state/gamecult/codex-ssh-activity.log")
os.makedirs(os.path.dirname(log), exist_ok=True)
with open(log, "a", encoding="utf-8") as f:
    f.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S%z')} Codex: PS Move latency LED pulse train armed.\n")

def write_rgb(r, g, b):
    with open(hidraw, "wb", buffering=0) as device:
        device.write(bytes([0x06, 0, r, g, b, 0, 0, 0, 0]))

write_rgb(0, 0, 0)
sys.stdin.readline()
start = time.perf_counter() + start_delay
for offset, r, g, b in events:
    wait = start + offset - time.perf_counter()
    if wait > 0:
        time.sleep(wait)
    write_rgb(r, g, b)
    time.sleep(pulse_seconds)
    write_rgb(0, 0, 0)
write_rgb(0, 0, 0)
"""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", default=DEFAULT_FFMPEG)
    parser.add_argument("--ffplay", default=DEFAULT_FFPLAY)
    parser.add_argument("--ffprobe", default=DEFAULT_FFPROBE)
    parser.add_argument("--video-preset", choices=sorted(VIDEO_PRESETS), default="ps3-eye-0")
    parser.add_argument("--audio-preset", choices=sorted(AUDIO_PRESETS), default="scarlett-cardioid")
    parser.add_argument("--video-device")
    parser.add_argument("--audio-device")
    parser.add_argument("--ssh-target", default="nightwing")
    parser.add_argument("--hidraw", default="/dev/hidraw1")
    parser.add_argument("--out-dir", default="artifacts/runtime/move-latency")
    parser.add_argument("--duration", type=float, default=8.0)
    parser.add_argument("--lead", type=float, default=1.2)
    parser.add_argument("--interval", type=float, default=0.8)
    parser.add_argument("--pulses", type=int, default=6)
    parser.add_argument("--schedule", choices=("uniform", "debruijn"), default="debruijn")
    parser.add_argument("--alphabet-size", type=int, default=8)
    parser.add_argument("--order", type=int, default=3)
    parser.add_argument("--chirp-ms", type=float, default=80.0)
    parser.add_argument("--pulse-ms", type=float, default=90.0)
    parser.add_argument("--led-start-delay-ms", type=float, default=520.0)
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--width", type=int)
    parser.add_argument("--height", type=int)
    parser.add_argument("--framerate", type=int)
    args = parser.parse_args()
    preset_device, preset_width, preset_height, preset_framerate = VIDEO_PRESETS[args.video_preset]
    args.video_device = args.video_device or preset_device
    args.audio_device = args.audio_device or AUDIO_PRESETS[args.audio_preset]
    args.width = args.width or preset_width
    args.height = args.height or preset_height
    args.framerate = args.framerate or preset_framerate
    return args


def build_debruijn(alphabet_size: int, order: int) -> list[int]:
    alphabet_size = max(2, int(alphabet_size))
    order = max(1, int(order))
    sequence: list[int] = []
    a = [0] * (alphabet_size * order)

    def db(t: int, p: int) -> None:
        if t > order:
            if order % p == 0:
                sequence.extend(a[1 : p + 1])
            return
        a[t] = a[t - p]
        db(t + 1, p)
        for j in range(a[t - p] + 1, alphabet_size):
            a[t] = j
            db(t + 1, t)

    db(1, 1)
    return sequence


def rotate_to_distinct_opening(symbols: list[int], order: int) -> list[int]:
    if len(symbols) <= order:
        return symbols
    windows: set[tuple[int, ...]] = set()
    for index in range(len(symbols)):
        window = tuple(symbols[(index + offset) % len(symbols)] for offset in range(order))
        if window in windows:
            continue
        windows.add(window)
        if len(set(window)) == min(order, len(set(symbols))):
            return symbols[index:] + symbols[:index]
    return symbols


def symbol_chirp(symbol: int, args: argparse.Namespace) -> tuple[float, float, tuple[int, int, int]]:
    # Eight log-spaced-ish lanes in the PS Move/camera-friendly audio band.
    lane = symbol % max(2, args.alphabet_size)
    base = 1450.0 * (2.0 ** (lane / 7.0))
    glide = 1.20 if ((symbol // max(2, args.alphabet_size)) & 1) == 0 else 0.84
    hue = (lane / max(1, args.alphabet_size)) % 1.0
    sector = int(hue * 6.0)
    x = int(255 * (1.0 - abs((hue * 6.0) % 2.0 - 1.0)))
    colors = [(255, x, 0), (x, 255, 0), (0, 255, x), (0, x, 255), (x, 0, 255), (255, 0, x)]
    return base, base * glide, colors[sector % 6]


def build_events(args: argparse.Namespace) -> list[dict[str, float | int | tuple[int, int, int]]]:
    if args.schedule == "uniform":
        symbols = list(range(args.pulses))
        offsets = [args.lead + index * args.interval for index in range(args.pulses)]
    else:
        sequence = rotate_to_distinct_opening(build_debruijn(args.alphabet_size, args.order), args.order)
        symbols = sequence[: args.pulses]
        offsets = []
        cursor = args.lead
        for symbol in symbols:
            offsets.append(cursor)
            # Time-domain coding: symbol-dependent gap rides on top of the cadence.
            cursor += args.interval + 0.035 * ((symbol % 4) - 1.5)
    events = []
    for index, (symbol, offset) in enumerate(zip(symbols, offsets)):
        start_hz, end_hz, color = symbol_chirp(int(symbol), args)
        events.append({"index": index, "symbol": int(symbol), "offset": float(offset), "start_hz": start_hz, "end_hz": end_hz, "color": color})
    return events


def render_chirp_template(start_hz: float, end_hz: float, args: argparse.Namespace) -> np.ndarray:
    chirp_len = int(args.chirp_ms * 0.001 * args.sample_rate)
    t = np.arange(chirp_len, dtype=np.float32) / args.sample_rate
    k = (end_hz - start_hz) / max(1e-6, args.chirp_ms * 0.001)
    phase = 2.0 * math.pi * (start_hz * t + 0.5 * k * t * t)
    chirp = np.sin(phase).astype(np.float32)
    chirp *= np.hanning(chirp_len).astype(np.float32)
    chirp *= 0.45
    return chirp


def generate_chirp(path: Path, args: argparse.Namespace, events: list[dict[str, float | int | tuple[int, int, int]]]) -> np.ndarray:
    sample_rate = args.sample_rate
    total = int(args.duration * sample_rate)
    audio = np.zeros(total, dtype=np.float32)
    chirp_len = int(args.chirp_ms * 0.001 * sample_rate)
    prototype = np.zeros(chirp_len, dtype=np.float32)
    for event in events:
        chirp = render_chirp_template(float(event["start_hz"]), float(event["end_hz"]), args)
        if int(event["index"]) == 0:
            prototype = chirp.copy()
        offset = float(event["offset"])
        start = int(offset * sample_rate)
        end = min(total, start + chirp_len)
        audio[start:end] += chirp[: end - start]
    pcm = np.clip(audio, -1.0, 1.0)
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes((pcm * 32767).astype("<i2").tobytes())
    return prototype


def run_capture(args: argparse.Namespace, out_dir: Path, chirp_path: Path, events: list[dict[str, float | int | tuple[int, int, int]]]) -> Path:
    capture_path = out_dir / "capture.mkv"
    ffmpeg_cmd = [
        args.ffmpeg,
        "-hide_banner",
        "-y",
        "-thread_queue_size",
        "1024",
        "-rtbufsize",
        "512M",
        "-f",
        "dshow",
        "-video_size",
        f"{args.width}x{args.height}",
        "-framerate",
        str(args.framerate),
        "-i",
        f"video={args.video_device}",
        "-thread_queue_size",
        "1024",
        "-f",
        "dshow",
        "-i",
        f"audio={args.audio_device}",
        "-t",
        f"{args.duration:.3f}",
        "-map",
        "0:v:0",
        "-map",
        "1:a:0",
        "-c:v",
        "ffv1",
        "-level",
        "3",
        "-c:a",
        "pcm_s16le",
        "-ac",
        "1",
        "-ar",
        str(args.sample_rate),
        str(capture_path),
    ]
    ffmpeg = subprocess.Popen(ffmpeg_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    time.sleep(1.0)

    remote_b64 = base64.b64encode(REMOTE_LED.encode("utf-8")).decode("ascii")
    event_csv = ",".join(
        f"{float(event['offset']):.6f}:{int(event['color'][0])}:{int(event['color'][1])}:{int(event['color'][2])}"
        for event in events
    )
    pulse_seconds = args.pulse_ms * 0.001
    remote_cmd = (
        "tmp=$(mktemp); "
        f"printf '%s' '{remote_b64}' | base64 -d > \"$tmp\"; "
        f"python3 \"$tmp\" '{args.hidraw}' '{event_csv}' '{pulse_seconds:.6f}' '{args.led_start_delay_ms * 0.001:.6f}'; "
        "status=$?; rm -f \"$tmp\"; exit $status"
    )
    led = subprocess.Popen(["ssh", "-o", "BatchMode=yes", args.ssh_target, remote_cmd], stdin=subprocess.PIPE, text=True)
    time.sleep(0.5)
    audio = subprocess.Popen([args.ffplay, "-nodisp", "-autoexit", "-loglevel", "quiet", str(chirp_path)])
    if led.stdin is not None:
        led.stdin.write("go\n")
        led.stdin.flush()
        led.stdin.close()

    audio.wait(timeout=args.duration + 3)
    led.wait(timeout=args.duration + 5)
    _, stderr = ffmpeg.communicate(timeout=args.duration + 10)
    (out_dir / "ffmpeg-capture.err.log").write_text(stderr, encoding="utf-8", errors="replace")
    if ffmpeg.returncode != 0:
        raise RuntimeError(f"ffmpeg capture failed with {ffmpeg.returncode}; see {out_dir / 'ffmpeg-capture.err.log'}")
    return capture_path


def extract_capture(args: argparse.Namespace, out_dir: Path, capture_path: Path) -> tuple[Path, Path, list[float]]:
    audio_path = out_dir / "timing-mic.wav"
    frames_dir = out_dir / "frames"
    if frames_dir.exists():
        shutil.rmtree(frames_dir)
    frames_dir.mkdir(parents=True)
    subprocess.run(
        [args.ffmpeg, "-hide_banner", "-y", "-i", str(capture_path), "-map", "0:a:0", "-ac", "1", "-ar", str(args.sample_rate), str(audio_path)],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    subprocess.run(
        [args.ffmpeg, "-hide_banner", "-y", "-i", str(capture_path), "-map", "0:v:0", "-vsync", "0", str(frames_dir / "frame_%06d.png")],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    probe = subprocess.run(
        [
            args.ffprobe,
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "frame=best_effort_timestamp_time",
            "-of",
            "json",
            str(capture_path),
        ],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    data = json.loads(probe.stdout)
    times = [float(frame["best_effort_timestamp_time"]) for frame in data.get("frames", []) if "best_effort_timestamp_time" in frame]
    return audio_path, frames_dir, times


def detect_audio_peaks(
    audio_path: Path,
    chirp: np.ndarray,
    args: argparse.Namespace,
    events: list[dict[str, float | int | tuple[int, int, int]]] | None = None,
) -> list[float]:
    with wave.open(str(audio_path), "rb") as wav:
        sample_rate = wav.getframerate()
        pcm = wav.readframes(wav.getnframes())
    samples = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
    if args.schedule == "debruijn" and events:
        peaks: list[float] = []
        search_radius = int(max(0.45, args.interval * 0.65) * sample_rate)
        timing_bias = int(0.32 * sample_rate)
        for event in events:
            template = render_chirp_template(float(event["start_hz"]), float(event["end_hz"]), args)
            template -= float(np.mean(template))
            template /= max(1e-6, float(np.linalg.norm(template)))
            predicted = int((float(event["offset"]) + timing_bias / sample_rate) * sample_rate)
            lo = max(0, predicted - search_radius)
            hi = min(samples.size, predicted + search_radius + template.size)
            if hi - lo <= template.size:
                continue
            corr = np.correlate(samples[lo:hi], template, mode="valid")
            corr = np.maximum(corr, 0.0)
            index = int(np.argmax(corr))
            if corr[index] <= max(0.01, float(np.percentile(corr, 95)) * 0.35):
                continue
            peaks.append((lo + index) / sample_rate)
        return sorted(peaks)
    template = chirp.astype(np.float32)
    template -= float(np.mean(template))
    template /= max(1e-6, float(np.linalg.norm(template)))
    corr = np.correlate(samples, template, mode="valid")
    corr = np.maximum(corr, 0.0)
    min_gap = int(args.interval * 0.55 * sample_rate)
    peaks: list[int] = []
    work = corr.copy()
    for _ in range(args.pulses):
        index = int(np.argmax(work))
        if work[index] <= max(0.02, float(np.percentile(corr, 99)) * 0.2):
            break
        peaks.append(index)
        lo = max(0, index - min_gap)
        hi = min(work.size, index + min_gap)
        work[lo:hi] = 0.0
    return sorted(index / sample_rate for index in peaks)


def detect_video_peaks(frames_dir: Path, frame_times: list[float], args: argparse.Namespace) -> tuple[list[float], list[float]]:
    frame_paths = sorted(frames_dir.glob("frame_*.png"))
    scores: list[float] = []
    previous: np.ndarray | None = None
    for path in frame_paths:
        image = Image.open(path).convert("L")
        arr = np.asarray(image, dtype=np.float32)
        if previous is None:
            scores.append(0.0)
        else:
            positive_delta = np.maximum(arr - previous, 0.0)
            compact_flash = float(np.percentile(positive_delta, 99.90))
            bright_tail = float(np.percentile(arr, 99.85))
            scores.append(compact_flash + bright_tail * 0.02)
        previous = arr
    if not scores:
        return [], []
    times = frame_times[: len(scores)]
    if len(times) < len(scores):
        times = [index / args.framerate for index in range(len(scores))]
    baseline = float(np.percentile(scores, 20))
    signal = np.asarray(scores, dtype=np.float32) - baseline
    signal = np.maximum(signal, 0.0)
    threshold = max(float(np.percentile(signal, 92)), float(np.max(signal)) * 0.40)
    min_gap = max(1, int(args.interval * 0.55 * args.framerate))
    peaks: list[int] = []
    work = signal.copy()
    for _ in range(args.pulses):
        index = int(np.argmax(work))
        if work[index] <= threshold:
            break
        peaks.append(index)
        lo = max(0, index - min_gap)
        hi = min(work.size, index + min_gap)
        work[lo:hi] = 0.0
    return sorted(times[index] for index in peaks), scores


def pair_peaks(audio_peaks: list[float], video_peaks: list[float], args: argparse.Namespace) -> list[tuple[float, float, float]]:
    if not audio_peaks or not video_peaks:
        return []
    candidates = sorted(video - audio for audio in audio_peaks for video in video_peaks)
    tolerance = max(0.06, args.interval * 0.16)
    best_offset = candidates[0]
    best_key: tuple[int, float, float] | None = None
    for candidate in candidates:
        residuals = [min(abs((audio + candidate) - video) for video in video_peaks) for audio in audio_peaks]
        inliers = [residual for residual in residuals if residual <= tolerance]
        if not inliers:
            continue
        key = (len(inliers), -float(np.median(inliers)), -abs(candidate))
        if best_key is None or key > best_key:
            best_key = key
            best_offset = candidate
    pairs: list[tuple[float, float, float]] = []
    used_video: set[int] = set()
    for audio_peak in audio_peaks:
        nearest_index = min(
            (index for index in range(len(video_peaks)) if index not in used_video),
            key=lambda index: abs(video_peaks[index] - (audio_peak + best_offset)),
            default=None,
        )
        if nearest_index is None:
            break
        video_peak = video_peaks[nearest_index]
        if abs(video_peak - (audio_peak + best_offset)) <= tolerance:
            used_video.add(nearest_index)
            pairs.append((audio_peak, video_peak, video_peak - audio_peak))
    return pairs


def fit_schedule(
    peaks: list[float],
    events: list[dict[str, float | int | tuple[int, int, int]]],
    args: argparse.Namespace,
) -> tuple[float | None, list[dict[str, float | int | None]]]:
    if not peaks or not events:
        return None, []
    offsets = [float(event["offset"]) for event in events]
    candidates = sorted(peak - offset for peak in peaks for offset in offsets)
    tolerance = max(0.08, args.interval * 0.18)
    best_shift: float | None = None
    best_key: tuple[int, float, float] | None = None
    for candidate in candidates:
        residuals = [min(abs((offset + candidate) - peak) for peak in peaks) for offset in offsets]
        inliers = [residual for residual in residuals if residual <= tolerance]
        if not inliers:
            continue
        key = (len(inliers), -float(np.median(inliers)), -abs(candidate))
        if best_key is None or key > best_key:
            best_key = key
            best_shift = candidate
    if best_shift is None:
        return None, []
    used_peaks: set[int] = set()
    matches: list[dict[str, float | int | None]] = []
    for event in events:
        expected = float(event["offset"]) + best_shift
        nearest_index = min(
            (index for index in range(len(peaks)) if index not in used_peaks),
            key=lambda index: abs(peaks[index] - expected),
            default=None,
        )
        if nearest_index is None or abs(peaks[nearest_index] - expected) > tolerance:
            matches.append({"index": int(event["index"]), "symbol": int(event["symbol"]), "expected_s": expected, "observed_s": None, "residual_ms": None})
            continue
        used_peaks.add(nearest_index)
        matches.append(
            {
                "index": int(event["index"]),
                "symbol": int(event["symbol"]),
                "expected_s": expected,
                "observed_s": peaks[nearest_index],
                "residual_ms": (peaks[nearest_index] - expected) * 1000.0,
            }
        )
    return best_shift, matches


def parse_dshow_start_delta_ms(out_dir: Path) -> float | None:
    log_path = out_dir / "ffmpeg-capture.err.log"
    if not log_path.exists():
        return None
    text = log_path.read_text(encoding="utf-8", errors="replace")
    starts = [float(match.group(1)) for match in re.finditer(r"Duration: N/A, start: ([0-9.]+)", text)]
    if len(starts) < 2:
        return None
    return (starts[0] - starts[1]) * 1000.0


def summarize(
    audio_peaks: list[float],
    video_peaks: list[float],
    out_dir: Path,
    scores: list[float],
    args: argparse.Namespace,
    events: list[dict[str, float | int | tuple[int, int, int]]],
) -> dict:
    pairs = pair_peaks(audio_peaks, video_peaks, args)
    offsets_ms = [pair[2] * 1000.0 for pair in pairs]
    audio_shift, audio_matches = fit_schedule(audio_peaks, events, args)
    video_shift, video_matches = fit_schedule(video_peaks, events, args)
    schedule_visual_minus_audio_ms = None
    if audio_shift is not None and video_shift is not None:
        schedule_visual_minus_audio_ms = (video_shift - audio_shift) * 1000.0
    enough_visual = len(video_peaks) >= max(2, int(math.ceil(args.pulses * 0.65)))
    enough_audio = len(audio_peaks) >= max(2, int(math.ceil(args.pulses * 0.65)))
    enough_pairs = len(pairs) >= max(2, int(math.ceil(min(len(audio_peaks), len(video_peaks), args.pulses) * 0.50)))
    receipt_valid = enough_visual and enough_audio and enough_pairs
    summary = {
        "video_preset": args.video_preset,
        "video_device": args.video_device,
        "video_mode": f"{args.width}x{args.height}@{args.framerate}",
        "audio_preset": args.audio_preset,
        "audio_device": args.audio_device,
        "led_start_delay_ms": args.led_start_delay_ms,
        "schedule": args.schedule,
        "alphabet_size": args.alphabet_size,
        "order": args.order,
        "events": events,
        "audio_peaks_s": audio_peaks,
        "video_peaks_s": video_peaks,
        "paired_peaks": [
            {"audio_s": audio, "video_s": video, "visual_minus_audio_ms": offset * 1000.0}
            for audio, video, offset in pairs
        ],
        "paired_count": len(pairs),
        "audio_schedule_shift_ms": audio_shift * 1000.0 if audio_shift is not None else None,
        "video_schedule_shift_ms": video_shift * 1000.0 if video_shift is not None else None,
        "schedule_visual_minus_audio_ms": schedule_visual_minus_audio_ms,
        "audio_schedule_matches": audio_matches,
        "video_schedule_matches": video_matches,
        "receipt_valid": receipt_valid,
        "validity": {
            "enough_visual_events": enough_visual,
            "enough_audio_events": enough_audio,
            "enough_paired_events": enough_pairs,
        },
        "dshow_video_start_minus_audio_start_ms": parse_dshow_start_delta_ms(out_dir),
        "note": "Offsets are diagnostic receipt values. DirectShow stream start offsets are reported separately; native timestamped capture should own final calibration timing.",
        "visual_minus_audio_ms": offsets_ms,
        "median_visual_minus_audio_ms": float(np.median(offsets_ms)) if offsets_ms else None,
        "mean_visual_minus_audio_ms": float(np.mean(offsets_ms)) if offsets_ms else None,
        "score_min": float(np.min(scores)) if scores else None,
        "score_max": float(np.max(scores)) if scores else None,
    }
    (out_dir / "latency-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary


def main() -> int:
    args = parse_args()
    out_dir = Path(args.out_dir)
    if not out_dir.is_absolute():
        out_dir = Path.cwd() / out_dir
    out_dir.mkdir(parents=True, exist_ok=True)
    events = build_events(args)
    chirp_path = out_dir / "chirp-train.wav"
    (out_dir / "event-schedule.json").write_text(json.dumps(events, indent=2), encoding="utf-8")
    chirp = generate_chirp(chirp_path, args, events)
    capture_path = run_capture(args, out_dir, chirp_path, events)
    audio_path, frames_dir, frame_times = extract_capture(args, out_dir, capture_path)
    audio_peaks = detect_audio_peaks(audio_path, chirp, args, events)
    video_peaks, scores = detect_video_peaks(frames_dir, frame_times, args)
    summary = summarize(audio_peaks, video_peaks, out_dir, scores, args, events)
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
