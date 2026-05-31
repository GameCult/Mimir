#!/usr/bin/env python3
"""Probe Nightwing ALSA capture devices without external recorder tools."""

from __future__ import annotations

import argparse
import ctypes
import ctypes.util
import json
import math
import os
import sys
import time
import wave
from pathlib import Path


SND_PCM_STREAM_CAPTURE = 1
SND_PCM_FORMAT_S16_LE = 2
SND_PCM_ACCESS_RW_INTERLEAVED = 3


_libasound_name = ctypes.util.find_library("asound")
if not _libasound_name:
    raise SystemExit("libasound.so not found")

alsa = ctypes.CDLL(_libasound_name, use_errno=True)
alsa.snd_pcm_open.argtypes = [ctypes.POINTER(ctypes.c_void_p), ctypes.c_char_p, ctypes.c_int, ctypes.c_int]
alsa.snd_pcm_open.restype = ctypes.c_int
alsa.snd_pcm_set_params.argtypes = [
    ctypes.c_void_p,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_uint,
    ctypes.c_uint,
    ctypes.c_int,
    ctypes.c_uint,
]
alsa.snd_pcm_set_params.restype = ctypes.c_int
alsa.snd_pcm_readi.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong]
alsa.snd_pcm_readi.restype = ctypes.c_long
alsa.snd_pcm_prepare.argtypes = [ctypes.c_void_p]
alsa.snd_pcm_prepare.restype = ctypes.c_int
alsa.snd_pcm_recover.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
alsa.snd_pcm_recover.restype = ctypes.c_int
alsa.snd_pcm_close.argtypes = [ctypes.c_void_p]
alsa.snd_pcm_close.restype = ctypes.c_int
alsa.snd_strerror.argtypes = [ctypes.c_int]
alsa.snd_strerror.restype = ctypes.c_char_p


def alsa_error(code: int) -> str:
    raw = alsa.snd_strerror(code)
    return raw.decode("utf-8", errors="replace") if raw else f"ALSA error {code}"


def read_proc_cards() -> str:
    path = Path("/proc/asound/cards")
    return path.read_text(encoding="utf-8", errors="replace") if path.exists() else ""


def capture(device: str, rate: int, channels: int, seconds: float, latency_us: int) -> tuple[bytes, dict[str, object]]:
    handle = ctypes.c_void_p()
    open_wall_ns = time.time_ns()
    open_monotonic = time.monotonic()
    code = alsa.snd_pcm_open(ctypes.byref(handle), device.encode("utf-8"), SND_PCM_STREAM_CAPTURE, 0)
    if code < 0:
        raise RuntimeError(f"open failed: {alsa_error(code)}")
    try:
        code = alsa.snd_pcm_set_params(
            handle,
            SND_PCM_FORMAT_S16_LE,
            SND_PCM_ACCESS_RW_INTERLEAVED,
            int(channels),
            int(rate),
            1,
            int(latency_us),
        )
        if code < 0:
            raise RuntimeError(f"set_params failed: {alsa_error(code)}")

        frames_per_read = max(128, min(4096, int(rate // 20)))
        sample_count = frames_per_read * channels
        buffer = (ctypes.c_int16 * sample_count)()
        deadline = time.monotonic() + seconds
        chunks: list[bytes] = []
        frames = 0
        read_calls = 0
        recoveries = 0
        first_read_monotonic: float | None = None
        first_read_wall_ns: int | None = None
        while time.monotonic() < deadline:
            got = int(alsa.snd_pcm_readi(handle, ctypes.byref(buffer), frames_per_read))
            if got < 0:
                recovered = int(alsa.snd_pcm_recover(handle, got, 1))
                if recovered < 0:
                    raise RuntimeError(f"read failed: {alsa_error(got)} / recover {alsa_error(recovered)}")
                recoveries += 1
                continue
            if got == 0:
                time.sleep(0.001)
                continue
            if first_read_monotonic is None:
                first_read_monotonic = time.monotonic()
                first_read_wall_ns = time.time_ns()
            read_calls += 1
            frames += got
            byte_count = got * channels * 2
            chunks.append(bytes(ctypes.string_at(ctypes.byref(buffer), byte_count)))

        pcm = b"".join(chunks)
        stats = pcm_stats(pcm, channels)
        stats.update(
            {
                "device": device,
                "rate": rate,
                "channels": channels,
                "frames": frames,
                "duration_s": frames / rate if rate > 0 else 0.0,
                "read_calls": read_calls,
                "recoveries": recoveries,
                "open_wall_ns": open_wall_ns,
                "open_monotonic_s": open_monotonic,
                "first_read_monotonic_s": first_read_monotonic,
                "first_read_wall_ns": first_read_wall_ns,
                "end_wall_ns": time.time_ns(),
            }
        )
        return pcm, stats
    finally:
        alsa.snd_pcm_close(handle)


def pcm_stats(pcm: bytes, channels: int) -> dict[str, object]:
    if not pcm:
        return {"rms": [], "peak": [], "mean_abs": []}
    values = memoryview(pcm).cast("h")
    frames = len(values) // max(1, channels)
    rms: list[float] = []
    peak: list[float] = []
    mean_abs: list[float] = []
    for channel in range(channels):
        total = 0.0
        abs_total = 0.0
        max_abs = 0
        for frame in range(frames):
            sample = int(values[frame * channels + channel])
            total += sample * sample
            abs_total += abs(sample)
            max_abs = max(max_abs, abs(sample))
        denom = max(1, frames)
        rms.append(math.sqrt(total / denom) / 32768.0)
        peak.append(max_abs / 32768.0)
        mean_abs.append(abs_total / denom / 32768.0)
    return {"rms": rms, "peak": peak, "mean_abs": mean_abs}


def write_wav(path: Path, pcm: bytes, rate: int, channels: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(channels)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(pcm)


def append_visible_log(message: str) -> None:
    path = Path.home() / ".local/state/gamecult/codex-ssh-activity.log"
    path.parent.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    with path.open("a", encoding="utf-8") as log:
        log.write(f"{stamp} Codex: {message}\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", default="hw:0,0")
    parser.add_argument("--rate", type=int, default=48000)
    parser.add_argument("--channels", type=int, default=2)
    parser.add_argument("--seconds", type=float, default=2.0)
    parser.add_argument("--latency-us", type=int, default=50_000)
    parser.add_argument("--wav")
    parser.add_argument("--json")
    parser.add_argument("--visible-log", action="store_true")
    args = parser.parse_args()

    if args.visible_log:
        append_visible_log(
            f"ALSA mic probe starting device={args.device} rate={args.rate} channels={args.channels} seconds={args.seconds:.2f}"
        )

    print("alsa-cards")
    print(read_proc_cards().rstrip())
    pcm, stats = capture(args.device, args.rate, args.channels, args.seconds, args.latency_us)
    if args.wav:
        write_wav(Path(args.wav), pcm, args.rate, args.channels)
        stats["wav"] = args.wav
    print("alsa-capture " + json.dumps(stats, sort_keys=True))
    if args.json:
        Path(args.json).parent.mkdir(parents=True, exist_ok=True)
        Path(args.json).write_text(json.dumps(stats, indent=2, sort_keys=True), encoding="utf-8")
    if args.visible_log:
        append_visible_log(
            "ALSA mic probe complete "
            f"device={args.device} frames={stats['frames']} rms={stats['rms']} peak={stats['peak']}"
        )
    return 0 if stats.get("frames", 0) else 1


if __name__ == "__main__":
    raise SystemExit(main())
