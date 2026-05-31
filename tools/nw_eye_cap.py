#!/usr/bin/env python3
"""Capture raw luma frames from Nightwing V4L2 cameras for sync receipts."""

from __future__ import annotations

import argparse
import ctypes
import ctypes.util
import fcntl
import json
import mmap
import os
import select
import time
from pathlib import Path


V4L2_BUF_TYPE_VIDEO_CAPTURE = 1
V4L2_FIELD_NONE = 1
V4L2_MEMORY_MMAP = 1
VIDIOC_G_FMT = 0xC0D05604
VIDIOC_S_FMT = 0xC0D05605
VIDIOC_REQBUFS = 0xC0145608
VIDIOC_QUERYBUF = 0xC0585609
VIDIOC_QBUF = 0xC058560F
VIDIOC_DQBUF = 0xC0585611
VIDIOC_STREAMON = 0x40045612
VIDIOC_STREAMOFF = 0x40045613
VIDIOC_G_PARM = 0xC0CC5615
VIDIOC_S_PARM = 0xC0CC5616

_libc = ctypes.CDLL(ctypes.util.find_library("c") or "libc.so.6", use_errno=True)
_libc.ioctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_void_p]
_libc.ioctl.restype = ctypes.c_int


def fourcc(text: str) -> int:
    raw = text.encode("ascii")
    return raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24)


class V4L2PixFormat(ctypes.Structure):
    _fields_ = [
        ("width", ctypes.c_uint32),
        ("height", ctypes.c_uint32),
        ("pixelformat", ctypes.c_uint32),
        ("field", ctypes.c_uint32),
        ("bytesperline", ctypes.c_uint32),
        ("sizeimage", ctypes.c_uint32),
        ("colorspace", ctypes.c_uint32),
        ("priv", ctypes.c_uint32),
        ("flags", ctypes.c_uint32),
        ("ycbcr_enc", ctypes.c_uint32),
        ("quantization", ctypes.c_uint32),
        ("xfer_func", ctypes.c_uint32),
    ]


class V4L2FormatUnion(ctypes.Union):
    _fields_ = [("pix", V4L2PixFormat), ("raw", ctypes.c_uint8 * 200), ("_align", ctypes.c_uint64)]


class V4L2Format(ctypes.Structure):
    _fields_ = [("type", ctypes.c_uint32), ("fmt", V4L2FormatUnion)]


class V4L2RequestBuffers(ctypes.Structure):
    _fields_ = [
        ("count", ctypes.c_uint32),
        ("type", ctypes.c_uint32),
        ("memory", ctypes.c_uint32),
        ("capabilities", ctypes.c_uint32),
        ("flags", ctypes.c_uint8),
        ("reserved", ctypes.c_uint8 * 3),
    ]


class V4L2Fract(ctypes.Structure):
    _fields_ = [("numerator", ctypes.c_uint32), ("denominator", ctypes.c_uint32)]


class V4L2CaptureParm(ctypes.Structure):
    _fields_ = [
        ("capability", ctypes.c_uint32),
        ("capturemode", ctypes.c_uint32),
        ("timeperframe", V4L2Fract),
        ("extendedmode", ctypes.c_uint32),
        ("readbuffers", ctypes.c_uint32),
        ("reserved", ctypes.c_uint32 * 4),
    ]


class V4L2StreamParmUnion(ctypes.Union):
    _fields_ = [("capture", V4L2CaptureParm), ("raw", ctypes.c_uint8 * 200)]


class V4L2StreamParm(ctypes.Structure):
    _fields_ = [("type", ctypes.c_uint32), ("parm", V4L2StreamParmUnion)]


class Timeval(ctypes.Structure):
    _fields_ = [("tv_sec", ctypes.c_long), ("tv_usec", ctypes.c_long)]


class V4L2Timecode(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint32),
        ("flags", ctypes.c_uint32),
        ("frames", ctypes.c_uint8),
        ("seconds", ctypes.c_uint8),
        ("minutes", ctypes.c_uint8),
        ("hours", ctypes.c_uint8),
        ("userbits", ctypes.c_uint8 * 4),
    ]


class V4L2BufferUnion(ctypes.Union):
    _fields_ = [("offset", ctypes.c_uint32), ("userptr", ctypes.c_ulong), ("planes", ctypes.c_void_p), ("fd", ctypes.c_int32)]


class V4L2Buffer(ctypes.Structure):
    _fields_ = [
        ("index", ctypes.c_uint32),
        ("type", ctypes.c_uint32),
        ("bytesused", ctypes.c_uint32),
        ("flags", ctypes.c_uint32),
        ("field", ctypes.c_uint32),
        ("timestamp", Timeval),
        ("timecode", V4L2Timecode),
        ("sequence", ctypes.c_uint32),
        ("memory", ctypes.c_uint32),
        ("m", V4L2BufferUnion),
        ("length", ctypes.c_uint32),
        ("reserved2", ctypes.c_uint32),
        ("request_fd", ctypes.c_int32),
    ]


def xioctl(fd: int, request: int, obj: ctypes.Structure) -> None:
    if _libc.ioctl(fd, request, ctypes.byref(obj)) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))


def set_frame_interval(fd: int, fps: int) -> None:
    parm = V4L2StreamParm()
    parm.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
    try:
        xioctl(fd, VIDIOC_G_PARM, parm)
    except OSError:
        pass
    parm.parm.capture.timeperframe.numerator = 1
    parm.parm.capture.timeperframe.denominator = fps
    xioctl(fd, VIDIOC_S_PARM, parm)


def yuyv_to_luma(raw: bytes, width: int, height: int, stride: int) -> bytes:
    rows = []
    for row in range(height):
        start = row * stride
        line = raw[start:start + width * 2]
        rows.append(line[0::2])
    return b"".join(rows)


def capture(args: argparse.Namespace) -> dict:
    fd = os.open(args.device, os.O_RDWR | os.O_NONBLOCK)
    maps: list[mmap.mmap] = []
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    frames_path = out.with_suffix(".y8")
    try:
        set_frame_interval(fd, args.fps)
        fmt = V4L2Format()
        fmt.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
        xioctl(fd, VIDIOC_G_FMT, fmt)
        fmt.fmt.pix.width = args.width
        fmt.fmt.pix.height = args.height
        fmt.fmt.pix.pixelformat = fourcc(args.format)
        fmt.fmt.pix.field = V4L2_FIELD_NONE
        xioctl(fd, VIDIOC_S_FMT, fmt)
        actual = fmt.fmt.pix

        req = V4L2RequestBuffers()
        req.count = args.buffers
        req.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
        req.memory = V4L2_MEMORY_MMAP
        xioctl(fd, VIDIOC_REQBUFS, req)
        for index in range(req.count):
            buf = V4L2Buffer()
            buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
            buf.memory = V4L2_MEMORY_MMAP
            buf.index = index
            xioctl(fd, VIDIOC_QUERYBUF, buf)
            maps.append(mmap.mmap(fd, buf.length, mmap.MAP_SHARED, mmap.PROT_READ | mmap.PROT_WRITE, offset=buf.m.offset))
            xioctl(fd, VIDIOC_QBUF, buf)

        stream_type = ctypes.c_int(V4L2_BUF_TYPE_VIDEO_CAPTURE)
        fcntl.ioctl(fd, VIDIOC_STREAMON, stream_type)
        start = time.monotonic()
        deadline = start + args.seconds
        timestamps = []
        sequences = []
        with frames_path.open("wb") as frames:
            while time.monotonic() < deadline:
                ready, _, _ = select.select([fd], [], [], 0.25)
                if not ready:
                    continue
                buf = V4L2Buffer()
                buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
                buf.memory = V4L2_MEMORY_MMAP
                try:
                    xioctl(fd, VIDIOC_DQBUF, buf)
                except OSError:
                    continue
                raw = maps[buf.index][:buf.bytesused]
                frames.write(yuyv_to_luma(raw, int(actual.width), int(actual.height), int(actual.bytesperline)))
                timestamps.append(float(buf.timestamp.tv_sec) + float(buf.timestamp.tv_usec) / 1_000_000.0)
                sequences.append(int(buf.sequence))
                xioctl(fd, VIDIOC_QBUF, buf)
        try:
            fcntl.ioctl(fd, VIDIOC_STREAMOFF, stream_type)
        except OSError:
            pass
        first_ts = timestamps[0] if timestamps else None
        rel = [(ts - first_ts) for ts in timestamps] if first_ts is not None else []
        gap = max(0, (sequences[-1] - sequences[0] + 1) - len(sequences)) if len(sequences) >= 2 else 0
        stats = {
            "device": args.device,
            "frames_path": str(frames_path),
            "width": int(actual.width),
            "height": int(actual.height),
            "stride": int(actual.bytesperline),
            "format": args.format,
            "frames": len(timestamps),
            "timestamps_s": rel,
            "first_device_timestamp_s": first_ts,
            "sequence_gap": gap,
            "duration_s": (rel[-1] - rel[0]) if len(rel) >= 2 else 0.0,
        }
        out.write_text(json.dumps(stats, indent=2, sort_keys=True), encoding="utf-8")
        return stats
    finally:
        for mapping in maps:
            mapping.close()
        os.close(fd)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", required=True)
    parser.add_argument("--width", type=int, default=320)
    parser.add_argument("--height", type=int, default=240)
    parser.add_argument("--format", default="YUYV")
    parser.add_argument("--fps", type=int, default=187)
    parser.add_argument("--seconds", type=float, default=8.0)
    parser.add_argument("--buffers", type=int, default=8)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    stats = capture(args)
    print(json.dumps(stats, sort_keys=True))
    return 0 if stats["frames"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
