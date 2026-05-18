import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.sensor_fusion import Observation2D, PointCloud, TrackCache, load_fusion_config



def load_observations(path: Path) -> list[Observation2D]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(data, dict):
        data = data.get("observations", [])
    return [Observation2D.from_dict(item) for item in data]


def fuse_file(args) -> None:
    rig = load_fusion_config(Path(args.config))
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
    summary = {
        "output": str(output),
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
    p.set_defaults(func=fuse_file)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
