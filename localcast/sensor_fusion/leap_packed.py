"""Pure Leap packed-channel decoding and motion-point lowering."""

import numpy as np

from .render_bridge import RenderPointPacket


def unpack_leap_packed_channels(frame_bgr: np.ndarray) -> dict[str, np.ndarray]:
    frame = frame_bgr.astype(np.float32) / 255.0
    blue = frame[:, :, 0]
    green = frame[:, :, 1]
    red = frame[:, :, 2]
    magenta = np.maximum(red, blue)
    return {
        "green": green,
        "magenta": magenta,
        "red": red,
        "blue": blue,
    }


def leap_channel_motion_points(
    channel_name: str,
    current: np.ndarray,
    previous: np.ndarray,
    timestamp_ns: int,
    *,
    step: int,
) -> list[RenderPointPacket]:
    height, width = current.shape[:2]
    motion = np.abs(current - previous)
    threshold = max(0.035, float(np.percentile(motion, 94)))
    points: list[RenderPointPacket] = []
    for row in range(0, height, step):
        for col in range(0, width, step):
            value = float(motion[row, col])
            intensity = float(current[row, col])
            if value < threshold and intensity < 0.10:
                continue
            u = col / max(1, width - 1)
            v = row / max(1, height - 1)
            x = -0.46 + 0.92 * u
            y = -0.16 + 0.68 * (1.0 - v)
            z = 0.74 + 0.58 * (1.0 - v) + 0.12 * intensity
            confidence = max(0.28, min(1.0, value * 3.8 + intensity * 0.42))
            if channel_name == "green":
                color = (0.28, 1.0, 0.62, 0.86)
            elif channel_name == "magenta":
                color = (1.0, 0.26, 0.92, 0.80)
            elif channel_name == "red":
                color = (1.0, 0.22, 0.18, 0.42)
            else:
                color = (0.20, 0.42, 1.0, 0.42)
            points.append(
                RenderPointPacket(
                    stable_key=f"leap-motion:{channel_name}:{row}:{col}",
                    xyz=np.array([x, y, z], dtype=np.float64),
                    radius_m=0.012 + 0.026 * confidence,
                    color_rgba=color,
                    confidence=confidence,
                    source_timestamp_ns=timestamp_ns,
                )
            )
    return points
