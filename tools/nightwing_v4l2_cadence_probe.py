#!/usr/bin/env python3
"""Probe Linux V4L2 camera cadence without external packages.

Nightwing uses UVC/gspca video nodes for PS3 Eyes. This script keeps the
dependency surface tiny: query capabilities/formats, set one capture mode, mmap
driver buffers, and count dequeued frames for a bounded window.
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.util
import fcntl
import mmap
import os
import select
import time


V4L2_BUF_TYPE_VIDEO_CAPTURE = 1
V4L2_CAP_VIDEO_CAPTURE = 0x00000001
V4L2_CAP_STREAMING = 0x04000000
V4L2_FIELD_NONE = 1
V4L2_MEMORY_MMAP = 1
VIDIOC_QUERYCAP = 0x80685600
VIDIOC_ENUM_FMT = 0xC0405602
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
V4L2_CAP_TIMEPERFRAME = 0x1000


_libc_name = ctypes.util.find_library("c")
_libc = ctypes.CDLL(_libc_name or "libc.so.6", use_errno=True)
_libc.ioctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_void_p]
_libc.ioctl.restype = ctypes.c_int


def fourcc(text: str) -> int:
    raw = text.encode("ascii")
    if len(raw) != 4:
        raise ValueError("pixel format must be four ASCII characters")
    return raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24)


def fourcc_text(value: int) -> str:
    raw = bytes((value >> (8 * i)) & 0xFF for i in range(4))
    return raw.decode("ascii", errors="replace")


class V4L2Capability(ctypes.Structure):
    _fields_ = [
        ("driver", ctypes.c_char * 16),
        ("card", ctypes.c_char * 32),
        ("bus_info", ctypes.c_char * 32),
        ("version", ctypes.c_uint32),
        ("capabilities", ctypes.c_uint32),
        ("device_caps", ctypes.c_uint32),
        ("reserved", ctypes.c_uint32 * 3),
    ]


class V4L2Fmtdesc(ctypes.Structure):
    _fields_ = [
        ("index", ctypes.c_uint32),
        ("type", ctypes.c_uint32),
        ("flags", ctypes.c_uint32),
        ("description", ctypes.c_char * 32),
        ("pixelformat", ctypes.c_uint32),
        ("reserved", ctypes.c_uint32 * 4),
    ]


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
    _fields_ = [
        ("pix", V4L2PixFormat),
        ("raw", ctypes.c_uint8 * 200),
        ("_align", ctypes.c_uint64),
    ]


class V4L2Format(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint32),
        ("fmt", V4L2FormatUnion),
    ]


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
    _fields_ = [
        ("capture", V4L2CaptureParm),
        ("raw", ctypes.c_uint8 * 200),
    ]


class V4L2StreamParm(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint32),
        ("parm", V4L2StreamParmUnion),
    ]


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
    _fields_ = [
        ("offset", ctypes.c_uint32),
        ("userptr", ctypes.c_ulong),
        ("planes", ctypes.c_void_p),
        ("fd", ctypes.c_int32),
    ]


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


def cstr(value: bytes) -> str:
    return value.split(b"\0", 1)[0].decode("utf-8", errors="replace")


def query_formats(fd: int) -> list[str]:
    formats: list[str] = []
    index = 0
    while True:
        desc = V4L2Fmtdesc()
        desc.index = index
        desc.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
        try:
            xioctl(fd, VIDIOC_ENUM_FMT, desc)
        except OSError:
            break
        formats.append(f"{fourcc_text(desc.pixelformat)}:{cstr(desc.description)}")
        index += 1
    return formats


def read_frame_interval(fd: int) -> tuple[int, int, int]:
    parm = V4L2StreamParm()
    parm.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
    xioctl(fd, VIDIOC_G_PARM, parm)
    tpf = parm.parm.capture.timeperframe
    return int(tpf.numerator), int(tpf.denominator), int(parm.parm.capture.capability)


def set_frame_interval(fd: int, fps: int) -> tuple[int, int, int]:
    parm = V4L2StreamParm()
    parm.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
    try:
        xioctl(fd, VIDIOC_G_PARM, parm)
    except OSError:
        parm = V4L2StreamParm()
        parm.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
    parm.parm.capture.timeperframe.numerator = 1
    parm.parm.capture.timeperframe.denominator = fps
    xioctl(fd, VIDIOC_S_PARM, parm)
    tpf = parm.parm.capture.timeperframe
    return int(tpf.numerator), int(tpf.denominator), int(parm.parm.capture.capability)


def probe_device(path: str, width: int, height: int, pixel_format: str, fps: int | None, seconds: float, buffers: int) -> int:
    fd = os.open(path, os.O_RDWR | os.O_NONBLOCK)
    maps: list[mmap.mmap] = []
    try:
        cap = V4L2Capability()
        xioctl(fd, VIDIOC_QUERYCAP, cap)
        caps = cap.device_caps or cap.capabilities
        print(
            f"v4l2-device path={path} driver={cstr(cap.driver)} card=\"{cstr(cap.card)}\" "
            f"bus={cstr(cap.bus_info)} caps=0x{caps:08x} formats={','.join(query_formats(fd))}"
        )
        if not (caps & V4L2_CAP_VIDEO_CAPTURE):
            print(f"v4l2-cadence path={path} status=not-video-capture")
            return 1
        if not (caps & V4L2_CAP_STREAMING):
            print(f"v4l2-cadence path={path} status=not-streaming")
            return 1

        before_tpf = (0, 0, 0)
        after_tpf = (0, 0, 0)
        try:
            before_tpf = read_frame_interval(fd)
            if fps is not None:
                after_tpf = set_frame_interval(fd, fps)
            else:
                after_tpf = before_tpf
        except OSError as ex:
            print(f"v4l2-parm path={path} status=failed error=\"{str(ex).replace(chr(34), chr(39))}\"")

        fmt = V4L2Format()
        fmt.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
        xioctl(fd, VIDIOC_G_FMT, fmt)
        fmt.fmt.pix.width = width
        fmt.fmt.pix.height = height
        fmt.fmt.pix.pixelformat = fourcc(pixel_format)
        fmt.fmt.pix.field = V4L2_FIELD_NONE
        xioctl(fd, VIDIOC_S_FMT, fmt)
        actual = fmt.fmt.pix

        req = V4L2RequestBuffers()
        req.count = buffers
        req.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
        req.memory = V4L2_MEMORY_MMAP
        xioctl(fd, VIDIOC_REQBUFS, req)
        if req.count < 2:
            print(f"v4l2-cadence path={path} status=insufficient-buffers count={req.count}")
            return 1

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
        deadline = start + seconds
        frames = 0
        bytes_used = 0
        first_sequence: int | None = None
        last_sequence: int | None = None
        first_ts: float | None = None
        last_ts: float | None = None

        while time.monotonic() < deadline:
            ready, _, _ = select.select([fd], [], [], 0.25)
            if not ready:
                continue
            buf = V4L2Buffer()
            buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE
            buf.memory = V4L2_MEMORY_MMAP
            try:
                xioctl(fd, VIDIOC_DQBUF, buf)
            except BlockingIOError:
                continue
            except OSError:
                continue
            frames += 1
            bytes_used += buf.bytesused
            ts = float(buf.timestamp.tv_sec) + float(buf.timestamp.tv_usec) / 1_000_000.0
            if first_sequence is None:
                first_sequence = int(buf.sequence)
                first_ts = ts
            last_sequence = int(buf.sequence)
            last_ts = ts
            xioctl(fd, VIDIOC_QBUF, buf)

        try:
            fcntl.ioctl(fd, VIDIOC_STREAMOFF, stream_type)
        except OSError:
            pass

        elapsed = max(time.monotonic() - start, 1e-9)
        frame_fps = frames / elapsed
        device_span = (last_ts - first_ts) if first_ts is not None and last_ts is not None and last_ts > first_ts else 0.0
        device_fps = ((frames - 1) / device_span) if frames > 1 and device_span > 0 else 0.0
        sequence_gap = (
            max(0, (last_sequence - first_sequence + 1) - frames)
            if first_sequence is not None and last_sequence is not None and last_sequence >= first_sequence
            else 0
        )
        print(
            f"v4l2-cadence path={path} status=ok requested={width}x{height}/{pixel_format} "
            f"actual={actual.width}x{actual.height}/{fourcc_text(actual.pixelformat)} stride={actual.bytesperline} "
            f"tpfBefore={before_tpf[0]}/{before_tpf[1]} tpfAfter={after_tpf[0]}/{after_tpf[1]} "
            f"frames={frames} fps={frame_fps:.2f} deviceFps={device_fps:.2f} sequenceGap={sequence_gap} "
            f"bytesPerFrame={(bytes_used / frames) if frames else 0:.1f}"
        )
        return 0 if frames > 0 else 1
    finally:
        for mapping in maps:
            mapping.close()
        os.close(fd)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", action="append", required=True)
    parser.add_argument("--width", type=int, default=320)
    parser.add_argument("--height", type=int, default=240)
    parser.add_argument("--format", default="YUYV")
    parser.add_argument("--fps", type=int)
    parser.add_argument("--seconds", type=float, default=2.0)
    parser.add_argument("--buffers", type=int, default=8)
    args = parser.parse_args()

    status = 0
    for device in args.device:
        status |= probe_device(device, args.width, args.height, args.format, args.fps, args.seconds, args.buffers)
    return status


if __name__ == "__main__":
    raise SystemExit(main())
