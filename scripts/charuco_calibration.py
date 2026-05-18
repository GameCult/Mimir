import argparse
import json
import time
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np


ROOT = Path(__file__).resolve().parents[1]
TARGETS = ROOT / "calibration" / "targets"
RUNS = ROOT / "calibration" / "runs"


def utc_stamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def write_json(path: Path, data) -> None:
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def dictionary(name: str):
    if not hasattr(cv2.aruco, name):
        raise ValueError(f"Unknown ArUco dictionary: {name}")
    return cv2.aruco.getPredefinedDictionary(getattr(cv2.aruco, name))


def make_board(args):
    TARGETS.mkdir(parents=True, exist_ok=True)
    aruco_dict = dictionary(args.dictionary)
    board = cv2.aruco.CharucoBoard(
        (args.squares_x, args.squares_y),
        args.square_length,
        args.marker_length,
        aruco_dict,
    )
    width = args.squares_x * args.pixels_per_square
    height = args.squares_y * args.pixels_per_square
    image = board.generateImage((width, height), marginSize=args.margin, borderBits=1)
    out = TARGETS / args.output
    cv2.imwrite(str(out), image)
    meta = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "dictionary": args.dictionary,
        "squares_x": args.squares_x,
        "squares_y": args.squares_y,
        "square_length": args.square_length,
        "marker_length": args.marker_length,
        "pixels_per_square": args.pixels_per_square,
        "margin": args.margin,
        "image": str(out.relative_to(ROOT)),
    }
    write_json(out.with_suffix(".json"), meta)
    print(out)


def api_value(name: str):
    return {
        "any": 0,
        "dshow": cv2.CAP_DSHOW,
        "msmf": cv2.CAP_MSMF,
    }[name]


def open_camera(index: int, api: str, width: int | None, height: int | None, fps: int | None):
    cap = cv2.VideoCapture(index, api_value(api))
    if width:
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
    if height:
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
    if fps:
        cap.set(cv2.CAP_PROP_FPS, fps)
    cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
    return cap


def detect_charuco(gray, board, aruco_dict):
    corners, ids, _ = cv2.aruco.detectMarkers(gray, aruco_dict)
    if ids is None or len(ids) == 0:
        return None, None, corners, ids
    count, charuco_corners, charuco_ids = cv2.aruco.interpolateCornersCharuco(corners, ids, gray, board)
    if count is None or count < 1:
        return None, None, corners, ids
    return charuco_corners, charuco_ids, corners, ids


def capture(args):
    aruco_dict = dictionary(args.dictionary)
    board = cv2.aruco.CharucoBoard(
        (args.squares_x, args.squares_y),
        args.square_length,
        args.marker_length,
        aruco_dict,
    )
    out = RUNS / f"{utc_stamp()}-charuco-capture-{args.api}{args.index}"
    out.mkdir(parents=True, exist_ok=False)
    cap = open_camera(args.index, args.api, args.width, args.height, args.fps)
    if not cap.isOpened():
        raise SystemExit(f"Could not open camera {args.api}:{args.index}")

    accepted = []
    start = time.monotonic()
    last_save = 0.0
    attempts = 0
    while len(accepted) < args.frames and (time.monotonic() - start) < args.timeout:
        ok, frame = cap.read()
        attempts += 1
        if not ok or frame is None:
            continue
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        charuco_corners, charuco_ids, marker_corners, marker_ids = detect_charuco(gray, board, aruco_dict)
        corner_count = 0 if charuco_ids is None else int(len(charuco_ids))
        now = time.monotonic()
        if corner_count >= args.min_corners and (now - last_save) >= args.interval:
            filename = f"frame-{len(accepted):03d}-corners{corner_count}.png"
            cv2.imwrite(str(out / filename), frame)
            accepted.append(
                {
                    "file": filename,
                    "timestamp_monotonic": now,
                    "charuco_corners": corner_count,
                    "charuco_ids": [] if charuco_ids is None else [int(value) for value in charuco_ids.reshape(-1)],
                    "charuco_image_points": []
                    if charuco_corners is None
                    else [[float(x), float(y)] for x, y in charuco_corners.reshape((-1, 2))],
                    "marker_count": 0 if marker_ids is None else int(len(marker_ids)),
                }
            )
            last_save = now
            print(f"saved {filename}")
    cap.release()
    manifest = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "camera": {"api": args.api, "index": args.index},
        "board": {
            "dictionary": args.dictionary,
            "squares_x": args.squares_x,
            "squares_y": args.squares_y,
            "square_length": args.square_length,
            "marker_length": args.marker_length,
        },
        "attempts": attempts,
        "accepted": accepted,
    }
    write_json(out / "manifest.json", manifest)
    print(out)


def calibrate(args):
    aruco_dict = dictionary(args.dictionary)
    board = cv2.aruco.CharucoBoard(
        (args.squares_x, args.squares_y),
        args.square_length,
        args.marker_length,
        aruco_dict,
    )
    image_paths = sorted(Path(args.images).glob("*.png"))
    all_corners = []
    all_ids = []
    image_size = None
    used = []
    for path in image_paths:
        image = cv2.imread(str(path))
        if image is None:
            continue
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        image_size = gray.shape[::-1]
        charuco_corners, charuco_ids, _, _ = detect_charuco(gray, board, aruco_dict)
        if charuco_ids is None or len(charuco_ids) < args.min_corners:
            continue
        all_corners.append(charuco_corners)
        all_ids.append(charuco_ids)
        used.append(str(path))
    if len(all_corners) < args.min_frames:
        raise SystemExit(f"Need {args.min_frames} usable frames; found {len(all_corners)}")
    rms, camera_matrix, dist_coeffs, rvecs, tvecs = cv2.aruco.calibrateCameraCharuco(
        all_corners,
        all_ids,
        board,
        image_size,
        None,
        None,
    )
    result = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "rms_reprojection_error": float(rms),
        "image_size": image_size,
        "camera_matrix": camera_matrix.tolist(),
        "dist_coeffs": dist_coeffs.tolist(),
        "used_frames": used,
    }
    out = Path(args.output) if args.output else Path(args.images) / "intrinsics.json"
    write_json(out, result)
    print(out)
    print(json.dumps({"rms_reprojection_error": result["rms_reprojection_error"], "frames": len(used)}, indent=2))


def add_board_args(parser):
    parser.add_argument("--dictionary", default="DICT_4X4_100")
    parser.add_argument("--squares-x", type=int, default=8)
    parser.add_argument("--squares-y", type=int, default=6)
    parser.add_argument("--square-length", type=float, default=0.035)
    parser.add_argument("--marker-length", type=float, default=0.026)


def main():
    parser = argparse.ArgumentParser(description="Generate and capture ChArUco calibration data.")
    sub = parser.add_subparsers(required=True)

    p = sub.add_parser("board")
    add_board_args(p)
    p.add_argument("--pixels-per-square", type=int, default=180)
    p.add_argument("--margin", type=int, default=40)
    p.add_argument("--output", default="charuco-8x6-dict4x4-100.png")
    p.set_defaults(func=make_board)

    p = sub.add_parser("capture")
    add_board_args(p)
    p.add_argument("--api", choices=["any", "dshow", "msmf"], default="dshow")
    p.add_argument("--index", type=int, required=True)
    p.add_argument("--width", type=int)
    p.add_argument("--height", type=int)
    p.add_argument("--fps", type=int)
    p.add_argument("--frames", type=int, default=25)
    p.add_argument("--timeout", type=float, default=60.0)
    p.add_argument("--interval", type=float, default=0.5)
    p.add_argument("--min-corners", type=int, default=12)
    p.set_defaults(func=capture)

    p = sub.add_parser("calibrate")
    add_board_args(p)
    p.add_argument("--images", required=True)
    p.add_argument("--output")
    p.add_argument("--min-corners", type=int, default=12)
    p.add_argument("--min-frames", type=int, default=8)
    p.set_defaults(func=calibrate)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
