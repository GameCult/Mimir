import argparse
from pathlib import Path
import sys
import time

import cv2

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.sensor_fusion import (
    PointCloud,
    load_fusion_config,
    match_surface_features,
    orb_surface_observations,
    triangulate_surface_tracks,
)


def main() -> None:
    parser = argparse.ArgumentParser(description="Match surface features between two calibrated views and write a PLY.")
    parser.add_argument("--config", default=str(ROOT / "config" / "sensor-fusion.json"))
    parser.add_argument("--left-id", required=True)
    parser.add_argument("--right-id", required=True)
    parser.add_argument("--left-image", required=True)
    parser.add_argument("--right-image", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--max-features", type=int, default=2000)
    parser.add_argument("--max-descriptor-distance", type=float, default=44.0)
    parser.add_argument("--max-reprojection-error-px", type=float, default=4.0)
    args = parser.parse_args()

    rig = load_fusion_config(Path(args.config))
    left = cv2.imread(args.left_image)
    right = cv2.imread(args.right_image)
    if left is None:
        raise SystemExit(f"Could not read {args.left_image}")
    if right is None:
        raise SystemExit(f"Could not read {args.right_image}")
    timestamp_ns = time.monotonic_ns()
    left_features = orb_surface_observations(args.left_id, left, timestamp_ns, max_features=args.max_features)
    right_features = orb_surface_observations(args.right_id, right, timestamp_ns, max_features=args.max_features)
    tracks = match_surface_features(
        left_features,
        right_features,
        max_descriptor_distance=args.max_descriptor_distance,
        max_dt_ns=25_000_000,
    )
    points = triangulate_surface_tracks(
        tracks,
        rig.cameras,
        max_reprojection_error_px=args.max_reprojection_error_px,
    )
    PointCloud(points).write_ply(Path(args.output))
    print(
        {
            "left_features": len(left_features),
            "right_features": len(right_features),
            "tracks": len(tracks),
            "points": len(points),
            "output": args.output,
        }
    )


if __name__ == "__main__":
    main()
