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
Shared D3D12 fence name: Global\MimirFensalirProgramFence
```

OBS/libobs is D3D11 internally on Windows, so this source opens the named D3D12
texture, creates an OBS-readable legacy shared D3D11 texture, opens that same
bridge texture from D3D12, and performs a GPU-to-GPU copy during OBS render
before drawing the source. This avoids CPU readback and does not require
Spout2. It is not pure zero-copy yet; the remaining copy is the
D3D12-to-libobs-D3D11 boundary. Fensalir also publishes a named D3D12 fence;
the OBS source opens it and observes the producer completion value before
copying. This prevents blind read-before-producer-completion behavior. A later
multi-texture publication ring is still the clean way to make overwrite races
structurally impossible when producer and consumer cadence diverge.

For a visible interop witness while the real Mimir scene is sparse, launch
Mimir with:

```text
MIMIR_OBS_PROOF_VISUAL=1
```

That enables a small bright SDF/starfield proof visual. It is a debug witness,
not production scene policy.

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

## Install

For the standard OBS plugin search path:

```powershell
.\scripts\install-obs-mimir-plugin.ps1
```

By default this writes:

```text
C:\ProgramData\obs-studio\plugins\mimir_obs_stem_source\bin\64bit\mimir_obs_stem_source.dll
```

For a portable OBS tree or direct OBS installation root:

```powershell
.\scripts\install-obs-mimir-plugin.ps1 -ObsRoot D:\Tools\obs-studio
```

The script does not create scene items or alter OBS profiles.
