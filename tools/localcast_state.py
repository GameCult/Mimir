from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
STATE_DIR = ROOT / "state"
MAP_PATH = STATE_DIR / "map.yaml"
BRANCHES_PATH = STATE_DIR / "branches.json"
EVIDENCE_PATH = STATE_DIR / "evidence.jsonl"


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def write_text(path: Path, content: str) -> None:
    path.write_text(content, encoding="utf-8")


def load_branches() -> dict[str, Any]:
    return json.loads(read_text(BRANCHES_PATH))


def save_branches(data: dict[str, Any]) -> None:
    write_text(BRANCHES_PATH, json.dumps(data, indent=2) + "\n")


def append_evidence(record: dict[str, Any]) -> None:
    with EVIDENCE_PATH.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, ensure_ascii=True) + "\n")


def local_stamp() -> str:
    return datetime.now(timezone.utc).astimezone().replace(microsecond=0).isoformat()


def cmd_status(_: argparse.Namespace) -> int:
    branches = load_branches().get("branches", [])
    active = [branch for branch in branches if branch.get("status") == "active"]
    summary = extract_map_value(["current_status", "summary"]) or "(missing)"
    next_action = extract_map_value(["current_status", "next_action"]) or "(missing)"
    subgoals = extract_list("active_subgoals")

    print(f"Workspace: {ROOT}")
    print(f"Summary: {summary}")
    print(f"Next action: {next_action}")
    print(f"Active branches: {len(active)} / {len(branches)}")
    if subgoals:
        print("Active subgoals:")
        for item in subgoals:
            print(f"- {item}")
    return 0


def cmd_add_evidence(args: argparse.Namespace) -> int:
    record: dict[str, Any] = {
        "ts": local_stamp(),
        "type": args.type,
        "status": args.status,
        "note": args.note,
    }
    if args.branch:
        record["branch"] = args.branch
    append_evidence(record)
    print("Appended evidence record.")
    return 0


def cmd_add_branch(args: argparse.Namespace) -> int:
    data = load_branches()
    branches = data.setdefault("branches", [])
    if any(branch.get("id") == args.id for branch in branches):
        raise SystemExit(f"Branch '{args.id}' already exists.")
    branches.append(
        {
            "id": args.id,
            "hypothesis": args.hypothesis,
            "status": "active",
            "artifacts": args.artifact or [],
            "notes": args.note or "",
        }
    )
    save_branches(data)
    print(f"Added branch '{args.id}'.")
    return 0


def cmd_close_branch(args: argparse.Namespace) -> int:
    data = load_branches()
    for branch in data.get("branches", []):
        if branch.get("id") != args.id:
            continue
        branch["status"] = args.status
        if args.note:
            branch["notes"] = args.note
        save_branches(data)
        print(f"Updated branch '{args.id}' to status '{args.status}'.")
        return 0
    raise SystemExit(f"Branch '{args.id}' was not found.")


def extract_map_value(path: list[str]) -> str | None:
    lines = read_text(MAP_PATH).splitlines()
    current_path: list[str] = []
    for index, line in enumerate(lines):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        indent = len(line) - len(line.lstrip(" "))
        level = indent // 2
        stripped = line.strip()
        if stripped.startswith("- ") or ":" not in stripped:
            continue
        key, value = stripped.split(":", 1)
        current_path = current_path[:level] + [key]
        if current_path != path:
            continue
        value = value.strip()
        if value in {">", "|"}:
            return collect_block(lines, index + 1, indent)
        return value.strip("\"'")
    return None


def collect_block(lines: list[str], start: int, parent_indent: int) -> str:
    parts: list[str] = []
    for line in lines[start:]:
        if not line.strip():
            if parts:
                parts.append("")
            continue
        indent = len(line) - len(line.lstrip(" "))
        if indent <= parent_indent:
            break
        parts.append(line.strip())
    return " ".join(part for part in parts if part).strip()


def extract_list(section: str) -> list[str]:
    lines = read_text(MAP_PATH).splitlines()
    results: list[str] = []
    current: list[str] = []
    in_section = False
    section_indent = 0
    for line in lines:
        if not in_section:
            if line.strip() == f"{section}:":
                in_section = True
                section_indent = len(line) - len(line.lstrip(" "))
            continue
        indent = len(line) - len(line.lstrip(" "))
        if line.startswith(" " * (section_indent + 2) + "- "):
            if current:
                results.append(" ".join(current).strip())
            current = [line.strip()[2:].strip()]
            continue
        if current and line.strip() and indent > section_indent + 2:
            current.append(line.strip())
            continue
        if line.strip() and indent <= section_indent:
            break
    if current:
        results.append(" ".join(current).strip())
    return results


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Inspect and update Mimir repo state files."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    status_parser = subparsers.add_parser("status", help="Show a compact state summary.")
    status_parser.set_defaults(func=cmd_status)

    evidence_parser = subparsers.add_parser(
        "add-evidence", help="Append one JSONL evidence record."
    )
    evidence_parser.add_argument("--type", required=True)
    evidence_parser.add_argument("--status", required=True)
    evidence_parser.add_argument("--note", required=True)
    evidence_parser.add_argument("--branch")
    evidence_parser.set_defaults(func=cmd_add_evidence)

    add_branch_parser = subparsers.add_parser(
        "add-branch", help="Create a new active branch entry."
    )
    add_branch_parser.add_argument("--id", required=True)
    add_branch_parser.add_argument("--hypothesis", required=True)
    add_branch_parser.add_argument("--artifact", action="append")
    add_branch_parser.add_argument("--note")
    add_branch_parser.set_defaults(func=cmd_add_branch)

    close_branch_parser = subparsers.add_parser(
        "close-branch", help="Close an existing branch."
    )
    close_branch_parser.add_argument("--id", required=True)
    close_branch_parser.add_argument(
        "--status", required=True, choices=["accepted", "rejected", "archived"]
    )
    close_branch_parser.add_argument("--note")
    close_branch_parser.set_defaults(func=cmd_close_branch)

    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
