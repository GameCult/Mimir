#!/usr/bin/env python3
"""Export Mimir's read-only Eve provider advertisement fixture."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any


DOCUMENT: dict[str, Any] = {
    "schema": "gamecult.eve.provider_advertisement.v1",
    "providerId": "mimir.verse.service",
    "title": "Mimir Verse Service",
    "description": (
        "Read-only advertisement for Mimir's room, sensor, stream, field, "
        "and operator Verses. Runtime state remains owned by typed "
        "CultCache/CultMesh publishers; this fixture names the contract surface."
    ),
    "mode": "fixture-read-only",
    "serviceVerse": {
        "id": "verse.mimir.service.v1",
        "cultMeshKey": "verse.mimir.service.v1/provider-advertisement",
        "authority": "Mimir service manifest and contract routing",
        "owns": [
            "service discovery",
            "nested Verse roots",
            "schema catalog",
            "transport lane names",
            "provider advertisement publication",
        ],
    },
    "nestedVerses": [
        {
            "id": "mimir.room.default",
            "kind": "room",
            "namespace": "mimir.room.*",
            "cultMeshKey": "mimir.room.default/state",
            "authority": "Mimir calibration and room configuration",
            "owns": [
                "calibration truth",
                "coordinate frame",
                "sensor graph",
                "clock model",
                "configured composites",
                "operator identity",
            ],
            "doesNotOwn": ["device reads", "program presentation", "global Odin routing"],
        },
        {
            "id": "mimir.sensor.root",
            "kind": "sensor",
            "namespace": "mimir.sensor.*",
            "cultMeshKey": "mimir.sensor.inventory/current",
            "authority": "Mimir.Runtime and native sensor workers",
            "owns": [
                "source inventory",
                "native worker health",
                "device timing",
                "media format",
                "actuator capability",
                "producer fences",
            ],
            "doesNotOwn": ["room clock authority unless promoted by calibration state"],
        },
        {
            "id": "mimir.stream.root",
            "kind": "stream",
            "namespace": "mimir.media.*",
            "cultMeshKey": "mimir.media.streams/current",
            "authority": "Mimir.Runtime rolling windows, Mimir.Well, and VerseRecorder",
            "owns": [
                "rolling-window cursor",
                "capture pages",
                "resource refs",
                "body refs",
                "backpressure state",
                "recorder receipts",
            ],
            "doesNotOwn": ["OBS composition", "field presentation"],
        },
        {
            "id": "mimir.field.root",
            "kind": "field",
            "namespace": "mimir.fensalir.*",
            "cultMeshKey": "mimir.fensalir.reservoir/current",
            "authority": "Mimir.FensalirDaemon and Fensalir",
            "owns": [
                "reservoir job selection",
                "GPU worker pressure",
                "program surface refs",
                "resource resolver status",
                "selected lowerings",
            ],
            "doesNotOwn": ["sensor truth", "calibration truth", "operator command issuance"],
        },
        {
            "id": "mimir.operator.root",
            "kind": "operator",
            "namespace": "mimir.control.*",
            "cultMeshKey": "mimir.control.operator/current",
            "authority": "Runtime owners, Fensalir daemon, Faust/native DSP",
            "owns": [
                "operator command receipts",
                "selected program layer state",
                "record/live state",
                "degraded-state visibility",
            ],
            "doesNotOwn": ["sensor synchronization", "reservoir scheduling", "OBS internals"],
        },
    ],
    "schemaWitnessFamilies": [
        {
            "family": "sensor",
            "namespace": "mimir.sensor.*",
            "witnesses": [
                "mimir.eve_sensor_observation.v1",
                "mimir.native_sensor_state.v1",
                "mimir.sensor_inventory.v1",
            ],
            "durableShape": "typed CultCache .cc witness or CultCache-compatible migration export",
        },
        {
            "family": "media",
            "namespace": "mimir.media.*",
            "witnesses": [
                "mimir.well_capture_page.v1",
                "mimir.eve_media_observation.v1",
                "mimir.cultmesh_stream_frame.v1",
            ],
            "durableShape": "capture page metadata, resource keys, body refs, fence refs",
        },
        {
            "family": "observation",
            "namespace": "mimir.observation.*",
            "witnesses": [
                "mimir.eve_sensor_observation.v1",
                "mimir.eve_media_observation.v1",
                "mimir.audio_path_evidence.v1",
                "mimir.calibration_constraint.v1",
            ],
            "durableShape": "typed observations with source id, time range, confidence, calibration id, payload/resource ref",
        },
        {
            "family": "ledger",
            "namespace": "mimir.ledger.*",
            "witnesses": [
                "mimir.recorder_body_index.v1",
                "mimir.verse_recorder_session.v1",
                "mimir.replay_cursor.v1",
            ],
            "durableShape": "session receipts, body index, replay cursor, migration version, schema witness",
        },
        {
            "family": "control",
            "namespace": "mimir.control.*",
            "witnesses": [
                "mimir.eve_dashboard_command.v1",
                "mimir.actuator_state.v1",
                "mimir.fensalir_reservoir_worker_state.v1",
                "mimir.fensalir_reservoir_pressure.v1",
            ],
            "durableShape": "operator commands, accepted receipts, selected state, reservoir pressure, DSP actuator state",
        },
        {
            "family": "eve",
            "namespace": "mimir.eve.*",
            "witnesses": [
                "gamecult.eve.provider_advertisement.v1",
                "gamecult.eve.surface.v1",
                "mimir.eve_dashboard_manifest.v1",
                "mimir.eve_dashboard_state.v1",
            ],
            "durableShape": "provider manifests, retained surfaces, field bindings, command bindings, lowering metadata",
        },
    ],
    "cultMeshSurfaceKeys": [
        {
            "id": "mimir.sensor.field.surface",
            "documentSchema": "gamecult.eve.surface.v1",
            "cultMeshKey": "mimir.eve.surface.sensor-field",
            "binds": ["mimir.sensor.*", "mimir.observation.*"],
            "status": "expected-surface",
        },
        {
            "id": "mimir.operator.flow.surface",
            "documentSchema": "gamecult.eve.surface.v1",
            "cultMeshKey": "mimir.eve.surface.operator-flow",
            "binds": ["mimir.control.*", "mimir.media.*", "mimir.fensalir.*"],
            "status": "expected-surface",
        },
        {
            "id": "mimir.observation.ledger.surface",
            "documentSchema": "gamecult.eve.surface.v1",
            "cultMeshKey": "mimir.eve.surface.observation-ledger",
            "binds": ["mimir.ledger.*", "mimir.observation.*"],
            "status": "expected-surface",
        },
        {
            "id": "mimir.fensalir.reservoir.surface",
            "documentSchema": "gamecult.eve.surface.v1",
            "cultMeshKey": "mimir.eve.surface.fensalir-reservoir",
            "binds": ["mimir.fensalir.*", "mimir.media.*"],
            "status": "expected-surface",
        },
    ],
    "commandBoundaries": [
        {
            "id": "mimir.command.operator.request",
            "owner": "runtime owner named by command target",
            "acceptedByFixture": False,
            "transport": "CultMesh command document with accepted/rejected receipt",
            "allowedTargets": [
                "mimir.control.recording",
                "mimir.control.streaming",
                "mimir.control.source-placement",
                "mimir.control.audio-actuator",
                "mimir.fensalir.reservoir-scheduling",
            ],
            "forbiddenTargets": [
                "sensor truth",
                "calibration truth",
                "OBS composition internals",
                "dashboard-local private state",
            ],
        }
    ],
    "freshnessCapabilities": {
        "advertisement": "static-fixture",
        "runtimeState": "not-published-by-this-exporter",
        "expectedLiveCadence": {
            "sensor": "per producer/device cadence",
            "media": "per capture page or stream frame",
            "control": "on command receipt or state transition",
            "eve": "on retained surface revision",
            "fensalir": "daemon poll/publish cadence",
        },
        "stalenessPolicy": "consumers must inspect owner-published updatedAt/fence/cursor fields, not this fixture",
    },
    "styleCapabilities": [
        "dense-tui",
        "native-eve",
        "browser-lowering",
        "operator-dashboard",
        "degraded-state-visible",
        "read-only-discovery-first-cut",
    ],
    "loweringRules": [
        "Eve/CultUI renders provider-owned state and submits commands; it does not own state truth.",
        "OBS, browser dashboards, JSON logs, and screenshots are lowerings or witnesses.",
        "Runtime migration to typed .cc state is outside this fixture exporter.",
    ],
}


def render_document() -> str:
    return json.dumps(DOCUMENT, indent=2, sort_keys=False) + "\n"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export or verify Mimir's read-only Eve provider advertisement fixture."
    )
    parser.add_argument("--out", type=Path, help="Write the advertisement JSON to this path instead of stdout.")
    parser.add_argument("--check", type=Path, help="Compare an existing file with the generated fixture.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    rendered = render_document()

    if args.check:
        existing = args.check.read_text(encoding="utf-8")
        if existing != rendered:
            print(f"provider advertisement fixture is stale: {args.check}", file=sys.stderr)
            return 1
        print(f"provider advertisement fixture ok: {args.check}")
        return 0

    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(rendered, encoding="utf-8")
        print(f"wrote {args.out}")
        return 0

    print(rendered, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
