# OBS Native Plugin

The production OBS path is a Mimir/Fensalir-owned program texture plus a
Mimir/Fensalir-owned stem bus, exposed through a narrow native OBS plugin.

## Authority

- Fensalir owns the completed program pixels.
- Mimir/Fensalir own timing, alignment, DSP, and stem identity.
- OBS owns broadcast mixing and scene composition.
- The plugin imports already-produced program pixels and reads already-processed
  stem samples. It does not synchronize raw sources, estimate clocks, render the
  field, or decide which payload is true.

## Current Contract

### Video

Fensalir publishes the completed backbuffer into a named shared D3D12
`B8G8R8A8_UNorm` texture when these environment variables are set:

```text
FENSALIR_PROGRAM_OUTPUT_D3D12=1
FENSALIR_PROGRAM_OUTPUT_NAME=Global\MimirFensalirProgramTexture
```

The native OBS plugin in `native/obs_stem_source` exposes a `Mimir Program
Texture` video source. Set:

```text
Shared D3D12 texture name: Global\MimirFensalirProgramTexture
```

OBS/libobs is D3D11 internally on Windows, so this source opens the named D3D12
texture, creates an OBS-readable shared D3D11 texture, and performs a GPU-to-GPU
copy during OBS render before drawing the source. This avoids CPU readback and
does not require Spout2. It is not pure zero-copy yet; the remaining copy is the
D3D12-to-libobs-D3D11 boundary.

### Audio

`MimirObsStemSharedMemoryPublisher` writes the latest validated
`MimirObsStemPublicationSnapshot` into a Windows shared-memory map named
`Local\MimirObsStemBus` by default. The native OBS plugin in
`native/obs_stem_source` exposes a `Mimir Audio Stem` input source. Add one
source per stem and set:

```text
Shared memory map: Local\MimirObsStemBus
Stem id: aligned_source_0
```

Use `aligned_source_0` through `aligned_source_5` for the first actuator-stage
source lanes.

## Build

```powershell
.\scripts\build-obs-stem-plugin.ps1
```

The script stages `obsproject/obs-plugintemplate` under `artifacts/obs-sdk/`,
lets the upstream template fetch/build the matching OBS/libobs development
surface, then builds `native/obs_stem_source` against that SDK. The staged SDK
and native build products are local artifacts, not repository state.

The managed shared-memory publisher is verified by:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --obs-stem-shared-memory-smoke
```

The local native plugin build currently succeeds and emits:

```text
native\obs_stem_source\build\Release\mimir_obs_stem_source.dll
```

That DLL currently registers both source types:

```text
Mimir Program Texture
Mimir Audio Stem
```
