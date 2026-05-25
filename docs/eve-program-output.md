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
dotnet run --project .\src\Mimir.App\Mimir.App.csproj
```

Fensalir creates a shareable D3D12 texture with that name and copies the finished
backbuffer into it during the present pass. A hardware encoder process should
open that named texture, encode H.264/HEVC with GPU hardware, and send frames to
EVE's native receiver.

`scripts/start-eve-program-output.ps1` starts the Mimir/Fensalir app with the
shared-texture output enabled and verifies the `eve` SSH target before launch.

## Receiver Contract

EVE owns:

- network receive;
- hardware decode;
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
```
