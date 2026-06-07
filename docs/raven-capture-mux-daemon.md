# Raven Capture Mux Daemon

Raven owns local display capture and Realtek render-loopback capture. Mimir receives the muxed stream and decides what it means inside the synchronized program model.

## Authority Map

- Owner: `Mimir.RavenDaemon` owns process supervision and typed status for Raven's capture mux.
- Inputs: local display capture parameters, WASAPI render-loopback selection, FFmpeg path, named SRT targets, log root, and operator flags.
- Outputs: `mimir.raven_capture_mux_state.v1`, an Eve dashboard surface, health JSON, and named FFmpeg SRT MPEG-TS streams for Mimir and OBS.
- Derived state: PID, restart count, log paths, and dashboard nodes are derived from the supervised process and daemon options.
- Forbidden writers: capture scripts may still launch diagnostics, but they do not own daemon status or the canonical Raven mux state.
- Shared paths: service launch, dry run, health checks, and Eve dashboard projection all read the same `MimirRavenCaptureMuxStateDocument`.
- Cut line: no OBS plugin, custom media daemon, or alternate transport owns media in this pass. FFmpeg/WASAPI own capture, mux, encode, and SRT fanout; CultMesh owns status and operator surface.

## Run On Raven

From a Mimir checkout on Raven:

```powershell
.\scripts\start-raven-daemon.ps1
```

Defaults:

- Mimir target: `srt://10.77.0.2:5200`
- OBS target: `srt://10.77.0.2:5204`
- Eve/CultMesh dashboard: `ws://0.0.0.0:8801/eve/deck`
- Binary CultMesh dashboard stream: `ws://0.0.0.0:8801/eve/deck/cultmesh`
- Health: `http://127.0.0.1:8801/health`
- State cache: `C:\Meta\Mimir\state\raven-capture-mux.ccmp`
- Logs: `C:\Meta\Mimir\logs`

Use a dry run to inspect the exact FFmpeg pipeline without starting capture:

```powershell
.\scripts\start-raven-daemon.ps1 -DryRun
```

Disable the OBS sink when only Mimir should receive the mux:

```powershell
.\scripts\start-raven-daemon.ps1 -NoObsTarget
```

Use a specific Realtek render endpoint when Windows exposes more than one:

```powershell
.\scripts\start-raven-daemon.ps1 -AudioDevice "Realtek"
```

If Raven already has a host-level CultMesh server bound, keep this daemon as a local typed-state publisher and Eve endpoint:

```powershell
.\scripts\start-raven-daemon.ps1 -NoCultNetServer
```

## Receiver Shape

The daemon sends identical MPEG-TS SRT streams containing:

- `raven-display`: Raven desktop via `ddagrab` or `gdigrab`, encoded with `h264_nvenc`
- `raven-realtk-loopback`: Raven render loopback via `Mimir.WasapiLoopback` or the PowerShell WASAPI fallback, encoded as AAC

Mimir receives the ingest copy on `5200`. `scripts/start-raven-av-demux.ps1`
can split that copy back into local video and audio source lanes for the
current raw ingest adapters. OBS receives its own copy on `5204` as an ordinary
SRT Media Source; it does not compete with Mimir for the `5200` listener.

The typo in the existing source id, `raven-realtk-loopback`, is preserved until the receiver config is migrated.

## Idunn Lifecycle

Idunn watches the Raven daemon from Odin's local keepalive body:

- Health: `E:\Projects\Odin\scripts\health-raven-capture-mux.cmd`
- Restart: `E:\Projects\Odin\scripts\restart-raven-capture-mux.cmd`
- Default SSH target: Raven's WireGuard address, `10.77.0.4`

If Raven is unreachable, Idunn should record the failed health probe and raise
the normal keepalive decision instead of pretending the daemon is healthy.
