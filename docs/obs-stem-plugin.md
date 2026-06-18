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
Consumer D3D12 fence name: Global\MimirObsProgramConsumerFence
Texture ring slots: 1
```

OBS/libobs is D3D11 internally on Windows, so this source opens the named D3D12
texture, creates an OBS-readable legacy shared D3D11 texture, opens that same
bridge texture from D3D12, and performs a GPU-to-GPU copy during OBS render
before drawing the source. This avoids CPU readback and does not require
Spout2. It is not pure zero-copy yet; the remaining copy is the
D3D12-to-libobs-D3D11 boundary. Fensalir also publishes a named D3D12
program-output fence; the OBS source opens it and observes publication
completion before copying. This prevents blind read-before-producer-completion
behavior without coupling OBS to Fensalir's private frame fence.

For OBS-only stress testing, Fensalir and the OBS source can agree on a bounded
texture ring:

```text
FENSALIR_PROGRAM_OUTPUT_RING_COUNT=3
FENSALIR_PROGRAM_OUTPUT_CONSUMER_FENCE_NAME=Global\MimirObsProgramConsumerFence
Texture ring slots: 3
Consumer D3D12 fence name: Global\MimirObsProgramConsumerFence
```

With a ring count above one, Fensalir publishes textures named
`Global\MimirFensalirProgramTexture.0`, `.1`, and so on. The OBS source uses the
program-output fence value to copy the latest completed slot and signals the
consumer fence after its GPU copy. When Fensalir is launched with the matching
consumer-fence environment variable, it will not reuse a ring slot until OBS has
acknowledged a copy at or beyond that slot's published fence value. Leave the
ring count at `1` for EVE until the EVE relay speaks the same ring contract.

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
Muninn Stream
```

### Muninn Streams

The `Muninn Stream` source is a thin OBS wrapper over Muninn's typed
CultCache/CultMesh stream catalog. The operator-facing UI is intentionally just
the stream dropdown. Store paths, command endpoints, and media return endpoints
are source defaults and migration state, not user-facing stream selection.

The source listens for Raven's typed OBS catalog projection on local UDP port
`17874`. Raven Muninn sends the catalog over a small CultNet RUDP discovery lane
using connection id `0x6d750003`; OBS uses that live catalog for the dropdown
when present.

If live discovery is absent, the source falls back to Muninn's local `.cc`
store:

```text
C:\Meta\Odin\state\muninn.telemetry.cc
```

The source looks for record key `obs` with type:

```text
muninn.obs_stream_catalog
```

Muninn owns that catalog as typed CultCache state. OBS owns only the selected
scene source and media rendering. When a selected stream uses Muninn's RUDP
media profile, the source publishes the same typed
`muninn.capture_stream_command` that an operator would publish manually. Raven
Muninn `serve` still owns whether capture starts and which local FFmpeg/WASAPI
children exist.

If both live discovery and the local catalog projection are absent or stale, the
source keeps a LAN-default Raven A/V stream option instead of showing an empty
source. That is a resilience fallback for the OBS machine, not a second stream
authority. Stale catalog `rudp://` URLs that do not carry the current
low-latency LAN profile are sanitized back to the source-derived URL before the
bridge starts.

The current Raven-room default route is LAN-first: command requests go to
`192.168.1.84:17873`, and Raven sends media back to Starfire at
`192.168.1.66:5204`. Older saved source settings that still point at the
deprecated WireGuard defaults `10.77.0.4:17873` and `10.77.0.2` are migrated on
load. Custom operator-entered endpoints are left alone.

The source polls the catalog while active and republishes activation requests on
a bounded reconnect cadence, so a Raven daemon restart or media child exit does
not require reopening the source properties.

For `rudp://` stream URLs, the plugin binds the advertised media port, accepts
the CultNet RUDP handshake, acknowledges packets, reassembles RUDP fragments,
decodes typed `muninn.media_video_access_unit.v1` and
`muninn.media_audio_packet.v1` records, and lowers elementary H.264 plus ADTS
AAC into local loopback UDP sockets for OBS FFmpeg child sources. The bridge
does not impose a global packet-sequence FIFO over the media lane: CultNet RUDP
media is reliable but unordered, so frame ids, chunk ids, and the advertised
`assembly_deadline_ms` own video reconstruction. Incomplete video frames expire
inside that budget and emit `muninn.media_receiver_feedback.v1` keyframe
pressure instead of stalling newer media behind an obsolete sequence gap.

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
