#!/usr/bin/env python3
"""Publish Nightwing witness status as typed Mimir CultMesh observations.

This sends MessagePack `mimir.eve_sensor_observation.v1` documents to
Mimir.EveSensorReceiver. It is deliberately narrow: Nightwing owns local sensor
presence/timing receipts, while Mimir and Odin own synchronization and UI
projection.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import glob
import math
import os
import random
import socket
import struct
import subprocess
import time
import urllib.parse
from dataclasses import dataclass

try:
    import nw_eye_cap
    import nw_move_hint
except Exception:
    nw_eye_cap = None
    nw_move_hint = None


def pack_nil() -> bytes:
    return b"\xc0"


def pack_bool(value: bool) -> bytes:
    return b"\xc3" if value else b"\xc2"


def pack_int(value: int) -> bytes:
    if 0 <= value <= 0x7f:
        return bytes([value])
    if -32 <= value < 0:
        return bytes([0xe0 | (value + 32)])
    if -(1 << 31) <= value < (1 << 31):
        return b"\xd2" + struct.pack(">i", value)
    return b"\xd3" + struct.pack(">q", value)


def pack_float(value: float) -> bytes:
    return b"\xcb" + struct.pack(">d", float(value))


def pack_str(value: str) -> bytes:
    data = value.encode("utf-8")
    if len(data) <= 31:
        return bytes([0xa0 | len(data)]) + data
    if len(data) <= 0xff:
        return b"\xd9" + bytes([len(data)]) + data
    if len(data) <= 0xffff:
        return b"\xda" + struct.pack(">H", len(data)) + data
    return b"\xdb" + struct.pack(">I", len(data)) + data


def pack_array(items: list[bytes]) -> bytes:
    if len(items) <= 15:
        return bytes([0x90 | len(items)]) + b"".join(items)
    if len(items) <= 0xffff:
        return b"\xdc" + struct.pack(">H", len(items)) + b"".join(items)
    return b"\xdd" + struct.pack(">I", len(items)) + b"".join(items)


def pack_double_array(values: list[float]) -> bytes:
    return pack_array([pack_float(value) for value in values])


def pack_optional_string(value: str | None) -> bytes:
    return pack_nil() if value is None else pack_str(value)


def pack_optional_int(value: int | None) -> bytes:
    return pack_nil() if value is None else pack_int(value)


def sensor_document(
    observation_id: str,
    device_id: str,
    stream_id: str,
    kind: str,
    sequence: int,
    values: list[float],
    accuracy: int | None = None,
) -> bytes:
    now_ns = time.time_ns()
    wall = dt.datetime.now(dt.timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    return pack_array([
        pack_str(observation_id),
        pack_str(device_id),
        pack_str(stream_id),
        pack_str(kind),
        pack_int(sequence),
        pack_int(now_ns),
        pack_int(time.monotonic_ns()),
        pack_str(wall),
        pack_str("nightwing-monotonic"),
        pack_double_array(values),
        pack_optional_string(None),
        pack_optional_int(None),
        pack_nil(),
        pack_nil(),
        pack_optional_int(accuracy),
    ])


class WebSocket:
    def __init__(self, url: str) -> None:
        parsed = urllib.parse.urlparse(url)
        if parsed.scheme != "ws":
            raise ValueError("Only ws:// is supported")
        self.host = parsed.hostname or "127.0.0.1"
        self.port = parsed.port or 80
        self.path = parsed.path or "/"
        self.sock: socket.socket | None = None

    def connect(self) -> None:
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        sock = socket.create_connection((self.host, self.port), timeout=5)
        request = (
            f"GET {self.path} HTTP/1.1\r\n"
            f"Host: {self.host}:{self.port}\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n"
            "\r\n"
        )
        sock.sendall(request.encode("ascii"))
        response = b""
        while b"\r\n\r\n" not in response:
            chunk = sock.recv(4096)
            if not chunk:
                raise ConnectionError("WebSocket handshake closed")
            response += chunk
        if b" 101 " not in response.splitlines()[0]:
            raise ConnectionError(response.decode("utf-8", errors="replace").splitlines()[0])
        self.sock = sock

    def send_binary(self, payload: bytes) -> None:
        self._send_frame(0x2, payload)

    def close(self) -> None:
        if self.sock:
            self.sock.close()
            self.sock = None

    def _send_frame(self, opcode: int, payload: bytes) -> None:
        if not self.sock:
            raise ConnectionError("WebSocket not connected")
        mask = random.randbytes(4) if hasattr(random, "randbytes") else os.urandom(4)
        length = len(payload)
        head = bytearray([0x80 | opcode])
        if length < 126:
            head.append(0x80 | length)
        elif length <= 0xffff:
            head.extend([0x80 | 126])
            head.extend(struct.pack(">H", length))
        else:
            head.extend([0x80 | 127])
            head.extend(struct.pack(">Q", length))
        masked = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))
        self.sock.sendall(bytes(head) + mask + masked)


def video_devices() -> list[tuple[str, int, str]]:
    devices: list[tuple[str, int, str]] = []
    for path in sorted(glob.glob("/sys/class/video4linux/video*")):
        node = os.path.basename(path)
        try:
            index = int(node.replace("video", ""))
        except ValueError:
            continue
        try:
            with open(os.path.join(path, "name"), "r", encoding="utf-8", errors="replace") as handle:
                name = handle.read().strip()
        except OSError:
            name = "video"
        devices.append((node, index, name))
    return devices


@dataclass(frozen=True)
class MoveLight:
    name: str
    hidraw: str
    rgb: tuple[int, int, int]


def parse_rgb(value: str) -> tuple[int, int, int]:
    text = value.strip()
    if text.startswith("#"):
        text = text[1:]
    if len(text) != 6:
        raise ValueError(f"Expected #rrggbb, got {value!r}")
    return (int(text[0:2], 16), int(text[2:4], 16), int(text[4:6], 16))


def parse_move_light(value: str) -> MoveLight:
    try:
        name, rest = value.split("=", 1)
        hidraw, rgb_text = rest.split(":", 1)
    except ValueError as ex:
        raise argparse.ArgumentTypeError("Move light must be name=/dev/hidrawN:#rrggbb") from ex
    name = name.strip()
    hidraw = hidraw.strip()
    if not name or not hidraw:
        raise argparse.ArgumentTypeError("Move light name and hidraw path are required")
    try:
        rgb = parse_rgb(rgb_text)
    except ValueError as ex:
        raise argparse.ArgumentTypeError(str(ex)) from ex
    return MoveLight(name=name, hidraw=hidraw, rgb=rgb)


def write_move_light(move: MoveLight, rgb: tuple[int, int, int] | None = None) -> bool:
    r, g, b = rgb if rgb is not None else move.rgb
    try:
        with open(move.hidraw, "wb", buffering=0) as device:
            device.write(bytes([0x06, 0, r, g, b, 0, 0, 0, 0]))
        return True
    except OSError:
        return False


def move_light_observations(args: argparse.Namespace, sequence: int) -> list[bytes]:
    docs: list[bytes] = []
    for move in args.move_light:
        ok = write_move_light(move)
        r, g, b = move.rgb
        docs.append(sensor_document(
            observation_id=f"nightwing:{move.name}:move-light:{sequence}",
            device_id="nightwing",
            stream_id=f"nightwing-{move.name}",
            kind="mimir.psmove_light_state.v1",
            sequence=sequence,
            values=[1.0 if ok else 0.0, float(r), float(g), float(b)],
            accuracy=3 if ok else 0,
        ))
    return docs


def observations(sequence: int) -> list[bytes]:
    docs: list[bytes] = []
    for node, index, name in video_devices():
        present = 1.0 if os.path.exists(f"/dev/{node}") else 0.0
        is_eye = 1.0 if "gspca" in name.lower() or "playstation" in name.lower() else 0.0
        docs.append(sensor_document(
            observation_id=f"nightwing:{node}:{sequence}",
            device_id="nightwing",
            stream_id=f"nightwing-{node}",
            kind="camera-device-status",
            sequence=sequence,
            values=[present, float(index), is_eye],
            accuracy=3 if present else 0,
        ))
    docs.append(sensor_document(
        observation_id=f"nightwing:leap-tracking:{sequence}",
        device_id="nightwing",
        stream_id="nightwing-leap-tracking",
        kind="leap-tracking-status",
        sequence=sequence,
        values=[0.0],
        accuracy=0,
    ))
    return docs


def builtin_camera_observations(args: argparse.Namespace, sequence: int) -> list[bytes]:
    docs: list[bytes] = []
    for node, index, name in video_devices():
        lname = name.lower()
        is_eye = "gspca" in lname or "playstation" in lname or "eye" in lname
        if is_eye:
            continue
        docs.append(sensor_document(
            observation_id=f"nightwing:{node}:builtin-camera:{sequence}",
            device_id="nightwing",
            stream_id=f"nightwing-{node}",
            kind="mimir.nightwing_builtin_camera_witness.v1",
            sequence=sequence,
            values=[1.0, float(index), float(len(name)), float(args.interval)],
            accuracy=2,
        ))
    if not docs:
        docs.append(sensor_document(
            observation_id=f"nightwing:builtin-camera:missing:{sequence}",
            device_id="nightwing",
            stream_id="nightwing-builtin-camera",
            kind="mimir.nightwing_builtin_camera_witness.v1",
            sequence=sequence,
            values=[0.0],
            accuracy=0,
        ))
    return docs


def builtin_mic_observation(args: argparse.Namespace, sequence: int) -> bytes:
    values = [0.0, 0.0, 0.0, float(args.mic_sample_rate)]
    accuracy = 0
    try:
        result = subprocess.run(
            [
                "timeout",
                str(max(0.05, args.mic_window_seconds)),
                "arecord",
                "-D",
                args.mic_device,
                "-f",
                "S16_LE",
                "-r",
                str(args.mic_sample_rate),
                "-c",
                "1",
                "-t",
                "raw",
                "-q",
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            check=False,
            timeout=max(0.2, args.mic_window_seconds + 0.2),
        )
        data = result.stdout
        count = len(data) // 2
        if count > 0:
            samples = struct.unpack("<" + "h" * count, data[:count * 2])
            rms = math.sqrt(sum(float(sample) * float(sample) for sample in samples) / count) / 32768.0
            peak = max(abs(sample) for sample in samples) / 32768.0
            values = [1.0, rms, peak, float(args.mic_sample_rate), float(count)]
            accuracy = 2 if rms > 0.0 else 1
    except Exception:
        values = [0.0, 0.0, 0.0, float(args.mic_sample_rate)]
        accuracy = 0
    return sensor_document(
        observation_id=f"nightwing:builtin-mic:{sequence}",
        device_id="nightwing",
        stream_id="nightwing-builtin-mic",
        kind="mimir.nightwing_builtin_mic_witness.v1",
        sequence=sequence,
        values=values,
        accuracy=accuracy,
    )


def eye_tracking_observations(args: argparse.Namespace, sequence: int) -> list[bytes]:
    if nw_eye_cap is None or nw_move_hint is None:
        return []
    docs: list[bytes] = []
    devices = [part for part in args.eye_devices.split(",") if part]
    for eye_index, device in enumerate(devices):
        raw_path = None
        source_id = f"ps3-eye-{eye_index}"
        stats_path = f"/tmp/mimir-mvp-{source_id}.json"
        cap_args = argparse.Namespace(
            device=device,
            width=args.eye_width,
            height=args.eye_height,
            format="YUYV",
            fps=args.eye_fps,
            seconds=args.eye_window_seconds,
            buffers=8,
            out=stats_path,
            stdout=False,
        )
        try:
            stats = nw_eye_cap.capture(cap_args)
            raw_path = stats["frames_path"]
            width = int(stats["width"])
            height = int(stats["height"])
            frame_bytes = width * height
            with open(raw_path, "rb") as handle:
                raw = handle.read()
            frame_count = min(len(raw) // frame_bytes, len(stats.get("timestamps_s", [])))
            stride = max(1, int(args.tracking_stride))
            for frame_index in range(0, frame_count, stride):
                frame = raw[frame_index * frame_bytes:(frame_index + 1) * frame_bytes]
                blob = nw_move_hint.best_blob(frame, width, height)
                if blob is None or float(blob["confidence"]) < args.min_confidence:
                    continue
                values = [
                    float(blob["clip_x"]),
                    float(blob["clip_y"]),
                    float(blob["area_px"]),
                    float(blob["radius_px"]),
                    float(blob["mean_luma"]),
                    float(blob["peak_luma"]),
                    float(blob["confidence"]),
                    float(frame_index),
                    float(width),
                    float(height),
                ]
                docs.append(sensor_document(
                    observation_id=f"nightwing:{source_id}:move-sphere:{sequence}:{frame_index}",
                    device_id="nightwing",
                    stream_id=source_id,
                    kind="mimir.move_controller_observation_state.v1",
                    sequence=sequence * 100000 + frame_index,
                    values=values,
                    accuracy=3,
                ))
        except Exception as ex:
            docs.append(sensor_document(
                observation_id=f"nightwing:{source_id}:tracking-error:{sequence}",
                device_id="nightwing",
                stream_id=source_id,
                kind=f"tracking-error:{type(ex).__name__}",
                sequence=sequence,
                values=[0.0],
                accuracy=0,
            ))
        finally:
            try:
                if raw_path:
                    os.unlink(raw_path)
            except OSError:
                pass
    return docs


def run(args: argparse.Namespace) -> None:
    sequence = 0
    while True:
        ws = WebSocket(args.url)
        try:
            ws.connect()
            while True:
                for payload in observations(sequence):
                    ws.send_binary(payload)
                if args.track_builtin_camera:
                    for payload in builtin_camera_observations(args, sequence):
                        ws.send_binary(payload)
                if args.track_builtin_mic:
                    ws.send_binary(builtin_mic_observation(args, sequence))
                for payload in move_light_observations(args, sequence):
                    ws.send_binary(payload)
                if args.track_eyes:
                    for payload in eye_tracking_observations(args, sequence):
                        ws.send_binary(payload)
                sequence += 1
                if args.once:
                    return
                time.sleep(max(0.02, args.interval))
        finally:
            ws.close()
        if args.once:
            return
        time.sleep(args.reconnect_seconds)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="ws://192.168.1.66:8796/eve/periwinkle")
    parser.add_argument("--interval", type=float, default=0.25)
    parser.add_argument("--reconnect-seconds", type=float, default=2.0)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--track-eyes", action="store_true", help="Publish compact Nightwing-local PS3 Eye Move-sphere observations.")
    parser.add_argument("--track-builtin-camera", action="store_true", help="Publish compact status for Nightwing-local non-Eye camera devices.")
    parser.add_argument("--track-builtin-mic", action="store_true", help="Publish compact ALSA microphone level observations.")
    parser.add_argument(
        "--move-light",
        action="append",
        type=parse_move_light,
        default=[],
        help="Keep a Nightwing-local PS Move sphere lit: name=/dev/hidrawN:#rrggbb.",
    )
    parser.add_argument("--eye-devices", default="/dev/video2,/dev/video3")
    parser.add_argument("--eye-width", type=int, default=320)
    parser.add_argument("--eye-height", type=int, default=240)
    parser.add_argument("--eye-fps", type=int, default=187)
    parser.add_argument("--eye-window-seconds", type=float, default=0.12)
    parser.add_argument("--tracking-stride", type=int, default=6)
    parser.add_argument("--min-confidence", type=float, default=0.25)
    parser.add_argument("--mic-device", default="default")
    parser.add_argument("--mic-sample-rate", type=int, default=16000)
    parser.add_argument("--mic-window-seconds", type=float, default=0.08)
    args = parser.parse_args()
    run(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
