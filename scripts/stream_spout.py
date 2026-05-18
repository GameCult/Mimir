import argparse
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.sensor_fusion.spout_output import (
    OpenGLSpoutPointRenderer,
    RenderFrameFileSource,
    SpoutOutputConfig,
    write_status,
)


def main() -> None:
    parser = argparse.ArgumentParser(description="Publish render-frame packets as a named Spout texture for OBS.")
    parser.add_argument("--frame-json", default=str(ROOT / "calibration" / "runs" / "synthetic-render-frame.json"))
    parser.add_argument("--sender-name", default="LocalCastBridge Point Cloud")
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--fps", type=float, default=60.0)
    parser.add_argument("--point-scale", type=float, default=1.0)
    parser.add_argument("--demo-if-missing", action="store_true")
    parser.add_argument("--demo-point-count", type=int, default=4096)
    parser.add_argument("--status", default=str(ROOT / "calibration" / "runs" / "stream-spout-status.json"))
    parser.add_argument("--duration", type=float, help="Optional run duration in seconds for smoke tests.")
    args = parser.parse_args()

    frame_path = Path(args.frame_json)
    status_path = Path(args.status)
    config = SpoutOutputConfig(
        sender_name=args.sender_name,
        width=args.width,
        height=args.height,
        fps=args.fps,
        point_scale=args.point_scale,
        demo_point_count=args.demo_point_count,
    )
    source = RenderFrameFileSource(frame_path, demo_if_missing=args.demo_if_missing, demo_point_count=args.demo_point_count)
    frame_interval = 1.0 / max(1.0, args.fps)
    start = time.monotonic()
    last_status = 0.0

    with OpenGLSpoutPointRenderer(config) as renderer:
        while True:
            now = time.monotonic()
            if args.duration is not None and now - start >= args.duration:
                break
            frame = source.current_frame(now, config)
            ok = renderer.render(frame)
            if now - last_status >= 1.0:
                write_status(
                    status_path,
                    sender_name=config.sender_name,
                    frames_sent=renderer.frames_sent,
                    point_count=len(frame.points),
                    frame_path=frame_path,
                    last_error=None if ok else "SpoutGL sendTexture returned false",
                )
                last_status = now
            sleep_for = frame_interval - (time.monotonic() - now)
            if sleep_for > 0:
                time.sleep(sleep_for)
    write_status(
        status_path,
        sender_name=config.sender_name,
        frames_sent=renderer.frames_sent,
        point_count=0,
        frame_path=frame_path,
        last_error="stopped",
    )


if __name__ == "__main__":
    main()
