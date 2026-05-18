import argparse
import json
from pathlib import Path
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.sensor_fusion import (
    BoardObservation,
    BoardSpec,
    CameraIntrinsics,
    camera_models_to_config,
    solve_common_space_from_fixed_board,
)


def main() -> None:
    parser = argparse.ArgumentParser(description="Solve camera extrinsics into one fixed-board world frame.")
    parser.add_argument("--input", required=True, help="JSON with board, intrinsics, and observations.")
    parser.add_argument("--output", default=str(ROOT / "config" / "sensor-fusion.json"))
    parser.add_argument("--max-reprojection-error-px", type=float, default=2.5)
    args = parser.parse_args()

    data = json.loads(Path(args.input).read_text(encoding="utf-8"))
    board_data = data["board"]
    board = BoardSpec(
        squares_x=int(board_data["squares_x"]),
        squares_y=int(board_data["squares_y"]),
        square_length_m=float(board_data["square_length"]),
    )
    intrinsics = [
        CameraIntrinsics(
            sensor_id=item["id"],
            camera_matrix=np.asarray(item["camera_matrix"], dtype=np.float64),
            dist_coeffs=np.asarray(item.get("dist_coeffs", []), dtype=np.float64),
            width=int(item["width"]),
            height=int(item["height"]),
            role=item.get("role", "tracking"),
            latency_ms=float(item.get("latency_ms", 0.0)),
        )
        for item in data.get("intrinsics", [])
    ]
    observations = [
        BoardObservation(
            sensor_id=item["sensor_id"],
            timestamp_ns=int(item.get("timestamp_ns", 0)),
            corner_ids=np.asarray(item["corner_ids"], dtype=np.int32),
            image_points=np.asarray(item["image_points"], dtype=np.float64),
            confidence=float(item.get("confidence", 1.0)),
        )
        for item in data.get("observations", [])
    ]
    cameras, solves = solve_common_space_from_fixed_board(
        intrinsics,
        board,
        observations,
        max_reprojection_error_px=args.max_reprojection_error_px,
    )
    output = {
        "fusion": data.get(
            "fusion",
            {
                "max_pair_dt_ns": 25000000,
                "max_reprojection_error_px": 4.0,
                "min_confidence": 0.1,
                "cache_ttl_ns": 500000000,
            },
        ),
        "render_bridge": data.get("render_bridge", {}),
        "cameras": camera_models_to_config(cameras.values()),
        "calibration_report": {
            "world_frame": "fixed ChArUco board coordinates",
            "solves": [
                {
                    "sensor_id": solve.sensor_id,
                    "timestamp_ns": solve.timestamp_ns,
                    "corner_count": solve.corner_count,
                    "reprojection_error_px": solve.reprojection_error_px,
                }
                for solve in solves
            ],
        },
    }
    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(output, indent=2), encoding="utf-8")
    print(out)
    print(json.dumps(output["calibration_report"], indent=2))


if __name__ == "__main__":
    main()
