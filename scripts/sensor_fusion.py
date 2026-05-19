import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.sensor_fusion import (
    Observation2D,
    PointCloud,
    RenderBridgeConfig,
    TrackCache,
    load_fusion_config,
    lower_points_to_render_frame,
)
from localcast.diagnostics.render_frame_json import write_render_frame_json



def load_observations(path: Path) -> list[Observation2D]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(data, dict):
        data = data.get("observations", [])
    return [Observation2D.from_dict(item) for item in data]


def fuse_file(args) -> None:
    config_path = Path(args.config)
    config_data = json.loads(config_path.read_text(encoding="utf-8"))
    rig = load_fusion_config(config_path)
    observations = load_observations(Path(args.observations))
    result = rig.fuse(observations)
    cache = TrackCache(ttl_ns=rig.config.cache_ttl_ns)
    for point in result.points:
        cache.update(point)
    if args.now_ns is not None:
        cache.expire(args.now_ns)
    cloud = PointCloud(cache.points())
    output = Path(args.output)
    cloud.write_ply(output)
    render_frame_output = None
    if args.render_frame_json:
        render_config = RenderBridgeConfig.from_dict(config_data.get("render_bridge", {}))
        frame = lower_points_to_render_frame(
            cloud.points,
            render_config,
            frame_id=args.frame_id,
            created_monotonic_ns=args.created_ns,
        )
        render_frame_output = Path(args.render_frame_json)
        write_render_frame_json(render_frame_output, frame)
    summary = {
        "output": str(output),
        "render_frame_json": None if render_frame_output is None else str(render_frame_output),
        "points": len(cloud.points),
        "dropped": list(result.dropped),
    }
    print(json.dumps(summary, indent=2))


def main() -> None:
    parser = argparse.ArgumentParser(description="Build sparse point clouds from calibrated sensor observations.")
    sub = parser.add_subparsers(required=True)

    p = sub.add_parser("fuse-file")
    p.add_argument("--config", default=str(ROOT / "config" / "sensor-fusion.example.json"))
    p.add_argument("--observations", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--now-ns", type=int)
    p.add_argument("--render-frame-json")
    p.add_argument("--frame-id", type=int, default=0)
    p.add_argument("--created-ns", type=int, default=0)
    p.set_defaults(func=fuse_file)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
