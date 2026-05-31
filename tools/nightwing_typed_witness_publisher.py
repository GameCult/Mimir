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
import os
import random
import socket
import struct
import time
import urllib.parse


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


def run(args: argparse.Namespace) -> None:
    sequence = 0
    while True:
        ws = WebSocket(args.url)
        try:
            ws.connect()
            while True:
                for payload in observations(sequence):
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
    args = parser.parse_args()
    run(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
