# OBS Stem Plugin

The production audio path is a Mimir/Fensalir-owned stem bus plus a narrow OBS
source plugin.

## Authority

- Mimir/Fensalir own timing, alignment, DSP, and stem identity.
- OBS owns broadcast mixing and scene composition.
- The plugin reads already-processed stem samples. It does not synchronize raw
  sources, estimate clocks, or decide which stem is true.

## Current Contract

`MimirObsStemSharedMemoryPublisher` writes the latest validated
`MimirObsStemPublicationSnapshot` into a Windows shared-memory map named
`Local\MimirObsStemBus` by default. The native OBS plugin in
`native/obs_stem_source` exposes one audio input source. Add one source per stem
and set:

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
