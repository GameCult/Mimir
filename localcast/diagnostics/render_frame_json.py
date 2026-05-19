from __future__ import annotations

import json
from pathlib import Path

from localcast.sensor_fusion.render_bridge import RenderFramePacket


def write_render_frame_json(path: Path, frame: RenderFramePacket) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(frame.to_dict(), indent=2), encoding="utf-8")


def load_render_frame_json(path: Path) -> RenderFramePacket:
    return RenderFramePacket.from_dict(json.loads(path.read_text(encoding="utf-8")))
