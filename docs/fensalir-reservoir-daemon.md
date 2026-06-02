# Fensalir Reservoir Daemon

## Objective

Move heavy field reconstruction out of the interactive Mimir editor. The editor
should present synced video feeds and controls responsively. A separate
Fensalir reservoir daemon should drink from the Well, spend whatever CPU/GPU
budget is assigned, and publish typed reservoir/program surfaces.

## Authority Map

Owner: the Fensalir reservoir daemon owns reservoir work selection, GPU
residency, temporal reuse, fusion kernels, worker scheduling, and publication of
reservoir-derived program surfaces.

Inputs: typed Well pages, configured composite state, live timing policy,
feature-signal documents, calibration state, Fensalir-owned texture leases, and
explicit budget settings.

Outputs: typed CultMesh/CultCache reservoir status, program texture/fence
surfaces, optional diagnostic Eve surfaces, and OBS-facing shared texture or
plugin feeds.

Derived state: Mimir editor telemetry, preview rectangles, Odin/Eve dashboard
rows, and OBS plugin status are observers. They do not decide reservoir work.

Forbidden writers: the interactive editor render loop must not run temporal
reservoir history just because it is presenting a backbuffer. Field diagnostics
must be opt-in. A camera `FieldEvidenceFrame` claim must not impersonate a flat
video compositor.

Shared paths: direct user placement, configured Well composites, OBS output,
daemon diagnostics, and future Eve room lowerings must all reference the same
source ids, timing state, and placement/composite document. Manual UI placement
and daemon program output may differ only through an explicit selected
composite/version.

Deletion line: old app-hosted reservoir proof visuals remain diagnostic only.
They cannot be the default Mimir program output while the current goal is synced
video presentation.

## Current Scar

On 2026-06-02 the Mimir editor became unresponsive because Fensalir ran
reservoir history presentation on a video-only frame. The renderer now gates
that pass behind source-derived reservoir-producing input, and the live
video-only app log showed `reservoir-update n/a`.

The remaining gap is real: Fensalir still lacks a flat program-layer video
backend that draws Fensalir-owned camera texture leases as 2D synced layers.
That backend is separate from the reservoir daemon. Build it as the editor's
presentation path; do not smuggle it through field diagnostics.

## Current Daemon Cut

`src/Mimir.FensalirDaemon` is the first owner-shaped daemon:

- eats CultCache through `state/fensalir-daemon.ccmp`, using the canonical
  MessagePack CultCache backing store;
- drinks Well JSONL logs containing `mimir.well_snapshot.v1`,
  `mimir.well_capture_page.v1`, and `mimir.well_stream_pressure.v1`;
- publishes typed `mimir.fensalir_daemon_state.v1` state;
- speaks a CultNet-shaped WebSocket Eve provider at `/eve/deck`;
- speaks binary CultMesh dashboard documents at `/eve/deck/cultmesh`;
- exposes `/health` and `/eve/deck/manifest`;
- presents a retained `cultmesh.eve_surface.v0` surface with compact text and
  metrics that Eve GUI and Odin/Nightwing TUI can lower.

Start it with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-fensalir-daemon.ps1
```

The launcher writes a supervisor manifest under
`artifacts/runtime/fensalir-daemon-*` with provider specs:

```text
mimir-fensalir-daemon|Mimir Fensalir Daemon|ws://127.0.0.1:8799/eve/deck
mimir-fensalir-daemon|Mimir Fensalir Daemon|ws://127.0.0.1:8799/eve/deck/cultmesh
```

The current worker mode is `surface-owner-installed`: kernel scheduling,
GPU-resident reservoir compute, and actual fused program-surface publication
belong behind this daemon next. Do not move those back into the editor.
