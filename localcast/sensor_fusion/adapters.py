from __future__ import annotations

from dataclasses import dataclass
import subprocess
import time
from typing import BinaryIO, Iterator, Protocol

import numpy as np


class FrameSource(Protocol):
    def frames(self) -> Iterator["FramePacket"]:
        ...


@dataclass(frozen=True)
class FramePacket:
    sensor_id: str
    timestamp_ns: int
    sequence: int
    image_bgr: np.ndarray


@dataclass(frozen=True)
class FfmpegRawVideoConfig:
    sensor_id: str
    ffmpeg_exe: str
    device_name: str
    width: int
    height: int
    fps: int
    pixel_format: str = "bgr24"

    @property
    def frame_bytes(self) -> int:
        if self.pixel_format != "bgr24":
            raise ValueError(f"Unsupported raw pixel format: {self.pixel_format}")
        return self.width * self.height * 3

    def command(self) -> list[str]:
        return [
            self.ffmpeg_exe,
            "-hide_banner",
            "-loglevel",
            "warning",
            "-f",
            "dshow",
            "-video_size",
            f"{self.width}x{self.height}",
            "-framerate",
            str(self.fps),
            "-pixel_format",
            self.pixel_format,
            "-i",
            f"video={self.device_name}",
            "-an",
            "-f",
            "rawvideo",
            "-",
        ]


class FfmpegRawVideoSource:
    """Frame source adapter for DirectShow devices that FFmpeg negotiates better than OpenCV."""

    def __init__(self, config: FfmpegRawVideoConfig):
        self.config = config
        self._process: subprocess.Popen[bytes] | None = None

    def __enter__(self) -> "FfmpegRawVideoSource":
        self._process = subprocess.Popen(
            self.config.command(),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if self._process is None:
            return
        if self._process.poll() is None:
            self._process.terminate()
            try:
                self._process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self._process.kill()
        self._process = None

    def frames(self) -> Iterator[FramePacket]:
        if self._process is None or self._process.stdout is None:
            raise RuntimeError("FfmpegRawVideoSource must be used as a context manager")
        yield from read_raw_bgr_frames(
            self._process.stdout,
            sensor_id=self.config.sensor_id,
            width=self.config.width,
            height=self.config.height,
            frame_bytes=self.config.frame_bytes,
        )


def read_raw_bgr_frames(
    stream: BinaryIO,
    sensor_id: str,
    width: int,
    height: int,
    frame_bytes: int,
) -> Iterator[FramePacket]:
    sequence = 0
    while True:
        blob = stream.read(frame_bytes)
        if not blob or len(blob) < frame_bytes:
            return
        frame = np.frombuffer(blob, dtype=np.uint8).reshape((height, width, 3)).copy()
        yield FramePacket(
            sensor_id=sensor_id,
            timestamp_ns=time.monotonic_ns(),
            sequence=sequence,
            image_bgr=frame,
        )
        sequence += 1
