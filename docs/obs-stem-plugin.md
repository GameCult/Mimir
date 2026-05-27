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

The plugin requires an OBS/libobs development package that provides
`libobsConfig.cmake`.

```powershell
cmake -S .\native\obs_stem_source -B .\native\obs_stem_source\build -Dlibobs_DIR=<path-to-libobs-cmake>
cmake --build .\native\obs_stem_source\build --config Release
```

This workstation currently does not have the libobs SDK installed, so CMake
configuration stops at `find_package(libobs)`. The managed shared-memory
publisher is still verified by:

```powershell
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --obs-stem-shared-memory-smoke
```
