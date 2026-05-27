# EVE Program Output

## Authority Map

- Owner: Fensalir owns the visible D3D12 program pixels.
- Inputs: Fensalir reads the current `AquariumFrame`, render plan, GPU resources,
  and overlay state.
- Outputs: a named shared D3D12 `B8G8R8A8_UNorm` texture containing the completed
  backbuffer for the current frame.
- Derived state: Mimir publication profiles describe the stream contract; they
  do not render pixels. EVE receives decoded pixels and composites them.
- Forbidden writers: WebKit, DOM layout, browser dashboards, OBS capture, and
  desktop/window capture do not own this path.
- Shared paths: local display and EVE streaming both use the same completed
  Fensalir backbuffer copy.
- Deletion line: diagnostic Sunshine/Moonlight or desktop-duplication probes may
  prove network/decode budget, but they are not production authority.

## Current Mechanism

Set these before launching the Mimir app:

```powershell
$env:FENSALIR_PROGRAM_OUTPUT_D3D12 = "1"
$env:FENSALIR_PROGRAM_OUTPUT_NAME = "Global\MimirFensalirProgramTexture"
$env:FENSALIR_PROGRAM_OUTPUT_FENCE_NAME = "Global\MimirFensalirProgramFence"
$env:FENSALIR_PROGRAM_OUTPUT_RING_COUNT = "1"
dotnet run --project .\src\Mimir.App\Mimir.App.csproj
```

Fensalir creates a shareable D3D12 texture with that name, publishes a named
program-output fence, and copies the finished backbuffer into the texture during
the present pass. `Mimir.EveRelay` opens that named texture, reads the completed
program frame, feeds a low-latency H.264 encoder, and sends complete Annex-B
access units to EveCanvas over the existing `/stream` WebSocket. EveCanvas
treats JPEG frames as the old VoidBot dashboard fallback only; Mimir dashboard
streaming is `h264-annexb` and displays through a native
`AVSampleBufferDisplayLayer` so the iPad owns hardware decode/composite.

The EVE relay currently consumes the single-texture contract. Keep
`FENSALIR_PROGRAM_OUTPUT_RING_COUNT=1` for EVE until the relay has the same
slot-selection contract as the OBS program texture source.

`scripts/start-eve-dashboard-stream.ps1` starts the Mimir/Fensalir app with the
shared-texture output enabled, starts the H.264 relay, verifies the `eve` SSH
target, opens a reverse SSH tunnel from Eve's `127.0.0.1:8792` back to the
local relay when direct inbound TCP is blocked, and launches EveCanvas:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-eve-dashboard-stream.ps1
```

The relay defaults to `h264_nvenc`. Use `-Encoder libx264` only as a diagnostic
fallback when no NVENC-capable FFmpeg is installed.

EveCanvas tries `ws://127.0.0.1:8792/stream` first, then falls back to
`ws://192.168.1.66:8792/stream`. The localhost path is not JPEG tunneling; it is
the same H.264 Annex-B stream carried through SSH port forwarding so a Windows
firewall rule cannot silently turn the dashboard into a wall.

## Receiver Contract

EVE owns:

- network receive;
- hardware decode of H.264 access units;
- texture upload/import;
- final fullscreen composite;
- touch/Pencil/sensor uplink later.

EVE does not own:

- dashboard DOM;
- Chromium/WebKit compatibility;
- layout parity;
- Mimir timing authority;
- OBS composition authority.

## Smoke

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --eve-program-output-contract-smoke
dotnet build .\src\Mimir.EveRelay\Mimir.EveRelay.csproj --no-restore --disable-build-servers
```
