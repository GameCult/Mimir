from __future__ import annotations

from dataclasses import dataclass
import subprocess
import threading
import time
from typing import BinaryIO, Callable, Iterator, Protocol

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


@dataclass(frozen=True)
class OpenCvCaptureConfig:
    sensor_id: str
    index: int
    api: str
    width: int
    height: int
    fps: float | None = None


class OpenCvFrameSource:
    """OpenCV camera source. Use behind LatestFramePump for live loops."""

    def __init__(self, config: OpenCvCaptureConfig):
        self.config = config
        self._capture = None

    def __enter__(self) -> "OpenCvFrameSource":
        import cv2

        capture = cv2.VideoCapture(self.config.index, cv2_api(self.config.api))
        if not capture.isOpened():
            try:
                capture.release()
            except Exception:
                pass
            raise RuntimeError(f"OpenCV capture did not open: {self.config}")
        capture.set(cv2.CAP_PROP_FRAME_WIDTH, self.config.width)
        capture.set(cv2.CAP_PROP_FRAME_HEIGHT, self.config.height)
        if self.config.fps is not None:
            capture.set(cv2.CAP_PROP_FPS, float(self.config.fps))
        capture.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        self._capture = capture
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if self._capture is not None:
            try:
                self._capture.release()
            except Exception:
                pass
            self._capture = None

    def frames(self) -> Iterator[FramePacket]:
        if self._capture is None:
            raise RuntimeError("OpenCvFrameSource must be used as a context manager")
        sequence = 0
        while True:
            ok, frame = self._capture.read()
            if not ok or frame is None:
                return
            yield FramePacket(
                sensor_id=self.config.sensor_id,
                timestamp_ns=time.monotonic_ns(),
                sequence=sequence,
                image_bgr=frame.copy(),
            )
            sequence += 1


class LatestFramePump:
    """Runs a blocking FrameSource in a daemon thread and exposes its latest frame."""

    def __init__(self, source_factory: Callable[[], FrameSource]):
        self.source_factory = source_factory
        self._lock = threading.Lock()
        self._latest: FramePacket | None = None
        self._error: str | None = None
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="localcast-latest-frame-pump", daemon=True)
        self._thread.start()

    def latest(self, *, max_age_ns: int | None = None, now_ns: int | None = None) -> FramePacket | None:
        with self._lock:
            frame = self._latest
        if frame is None:
            return None
        if max_age_ns is not None:
            current = time.monotonic_ns() if now_ns is None else int(now_ns)
            if current - int(frame.timestamp_ns) > int(max_age_ns):
                return None
        return frame

    @property
    def error(self) -> str | None:
        with self._lock:
            return self._error

    def _run(self) -> None:
        try:
            with self.source_factory() as source:  # type: ignore[attr-defined]
                for frame in source.frames():
                    with self._lock:
                        self._latest = frame
                        self._error = None
        except Exception as exc:
            with self._lock:
                self._error = repr(exc)


def cv2_api(name: str) -> int:
    import cv2

    return {
        "any": 0,
        "dshow": cv2.CAP_DSHOW,
        "msmf": cv2.CAP_MSMF,
    }[name]


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
