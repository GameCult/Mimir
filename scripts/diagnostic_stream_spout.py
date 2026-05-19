import argparse
from pathlib import Path
import sys
import time
from typing import BinaryIO

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.diagnostics.spout_output import (
    CultCacheRenderFrameSource,
    OpenGLSpoutPointRenderer,
    RenderFrameFileSource,
    SpoutOutputConfig,
    write_cult_status,
    write_sync_status,
    write_status,
)


def acquire_runtime_lock(path: Path) -> BinaryIO | None:
    import msvcrt

    path.parent.mkdir(parents=True, exist_ok=True)
    handle = path.open("a+b")
    handle.seek(0)
    handle.write(b"\0")
    handle.flush()
    handle.seek(0)
    try:
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        handle.close()
        return None
    return handle


def main() -> None:
    parser = argparse.ArgumentParser(description="Publish render-frame packets as a named Spout texture for OBS.")
    parser.add_argument("--frame-json", default=str(ROOT / "calibration" / "runs" / "synthetic-render-frame.json"))
    parser.add_argument("--frame-cache", default=str(ROOT / "calibration" / "runs" / "visual-state.msgpack"))
    parser.add_argument("--sender-name", default="LocalCastBridge Point Cloud")
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--fps", type=float, default=60.0)
    parser.add_argument("--point-scale", type=float, default=1.0)
    parser.add_argument("--camera-preset", choices=["kiyo-mid-deru", "orbit"], default="kiyo-mid-deru")
    parser.add_argument("--max-render-points", type=int, default=5000)
    parser.add_argument("--demo-if-missing", action="store_true")
    parser.add_argument("--demo-point-count", type=int, default=4096)
    parser.add_argument("--status", default=str(ROOT / "calibration" / "runs" / "stream-spout-status.json"))
    parser.add_argument("--status-cache", default=str(ROOT / "calibration" / "runs" / "visual-stream-status.msgpack"))
    parser.add_argument("--sync-status", default=str(ROOT / "calibration" / "runs" / "av-sync-status.json"))
    parser.add_argument("--audio-cache", default=str(ROOT / "calibration" / "runs" / "audio-state.msgpack"))
    parser.add_argument("--audio-events-cache", default=str(ROOT / "calibration" / "runs" / "audio-events.msgpack"))
    parser.add_argument("--audio-event-window-ms", type=float, default=120.0)
    parser.add_argument("--no-audio-overlay", action="store_true")
    parser.add_argument("--remote-video-name", default="Neighbor PC - Video")
    parser.add_argument("--remote-video-url", default="srt://0.0.0.0:5100?mode=listener&latency=120000&timeout=5000000")
    parser.add_argument("--remote-video-latency-ms", type=float, default=250.0)
    parser.add_argument("--remote-video-tolerance-ms", type=float, default=120.0)
    parser.add_argument("--no-remote-video-sync", action="store_true")
    parser.add_argument("--source", choices=["cultcache", "json"], default="cultcache")
    parser.add_argument("--duration", type=float, help="Optional run duration in seconds for smoke tests.")
    args = parser.parse_args()

    frame_path = Path(args.frame_json)
    frame_cache_path = Path(args.frame_cache)
    status_path = Path(args.status)
    status_cache_path = Path(args.status_cache)
    sync_status_path = Path(args.sync_status)
    lock = acquire_runtime_lock(status_path.with_suffix(".sender.lock"))
    if lock is None:
        return
    config = SpoutOutputConfig(
        sender_name=args.sender_name,
        width=args.width,
        height=args.height,
        fps=args.fps,
        point_scale=args.point_scale,
        demo_point_count=args.demo_point_count,
        camera_preset=args.camera_preset,
        max_render_points=args.max_render_points,
    )
    if args.source == "cultcache":
        source = CultCacheRenderFrameSource(
            frame_cache_path,
            demo_if_missing=args.demo_if_missing,
            demo_point_count=args.demo_point_count,
            audio_cache=None if args.no_audio_overlay else Path(args.audio_cache),
            audio_events_cache=None if args.no_audio_overlay else Path(args.audio_events_cache),
            audio_event_window_ns=int(float(args.audio_event_window_ms) * 1_000_000),
            remote_video_name=None if args.no_remote_video_sync else args.remote_video_name,
            remote_video_url=None if args.no_remote_video_sync else args.remote_video_url,
            remote_video_latency_ns=int(float(args.remote_video_latency_ms) * 1_000_000),
            remote_video_tolerance_ns=int(float(args.remote_video_tolerance_ms) * 1_000_000),
        )
        frame_source_name = str(frame_cache_path)
    else:
        source = RenderFrameFileSource(frame_path, demo_if_missing=args.demo_if_missing, demo_point_count=args.demo_point_count)
        frame_source_name = str(frame_path)
    frame_interval = 1.0 / max(1.0, args.fps)
    start = time.monotonic()
    last_status = 0.0

    with OpenGLSpoutPointRenderer(config) as renderer:
        while True:
            now = time.monotonic()
            if args.duration is not None and now - start >= args.duration:
                break
            try:
                frame = source.current_frame(now, config)
            except FileNotFoundError:
                if now - last_status >= 1.0:
                    write_status(
                        status_path,
                        sender_name=config.sender_name,
                        frames_sent=renderer.frames_sent,
                        point_count=0,
                        frame_path=Path(frame_source_name),
                        last_error="waiting for visual frame cache",
                    )
                    write_cult_status(
                        status_cache_path,
                        sender_name=config.sender_name,
                        frames_sent=renderer.frames_sent,
                        point_count=0,
                        frame_source=frame_source_name,
                        last_error="waiting for visual frame cache",
                    )
                    last_status = now
                time.sleep(frame_interval)
                continue
            ok = renderer.render(frame)
            if now - last_status >= 1.0:
                write_status(
                    status_path,
                    sender_name=config.sender_name,
                    frames_sent=renderer.frames_sent,
                    point_count=renderer.last_render_point_count,
                    frame_path=Path(frame_source_name),
                    last_error=None if ok else "SpoutGL sendTexture returned false",
                    point_prefix_counts=renderer.last_render_prefix_counts,
                )
                write_cult_status(
                    status_cache_path,
                    sender_name=config.sender_name,
                    frames_sent=renderer.frames_sent,
                    point_count=renderer.last_render_point_count,
                    frame_source=frame_source_name,
                    last_error=None if ok else "SpoutGL sendTexture returned false",
                )
                write_sync_status(sync_status_path, getattr(source, "last_sync_status", None))
                last_status = now
            sleep_for = frame_interval - (time.monotonic() - now)
            if sleep_for > 0:
                time.sleep(sleep_for)
    write_status(
        status_path,
        sender_name=config.sender_name,
        frames_sent=renderer.frames_sent,
        point_count=0,
        frame_path=Path(frame_source_name),
        last_error="stopped",
    )
    write_cult_status(
        status_cache_path,
        sender_name=config.sender_name,
        frames_sent=renderer.frames_sent,
        point_count=0,
        frame_source=frame_source_name,
        last_error="stopped",
    )


if __name__ == "__main__":
    main()
