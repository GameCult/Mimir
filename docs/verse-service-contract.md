# Mimir Verse Service Contract

Mimir is a Verse service for the measured room. It accepts local and network
sensors, publishes typed observations and capture pages, asks Fensalir to lower
field evidence into program surfaces, and exposes operator controls through
Eve/CultUI surfaces. OBS, browser dashboards, JSON logs, and screenshot streams
are lowerings or witnesses. They are not canonical presentation or state owners.

## Owner Map

- Mimir service owner: configuration, launch, calibration truth, sensor
  identity, stream identity, synchronization state, Well publication, recorder
  receipts, and service contract.
- `Mimir.Runtime`: bounded rolling buffers, source descriptors, source polling,
  direct native ingest, audio synchronization analysis, and cached read models.
- Native sensor workers: device reads, closest-to-device timestamps, native or
  GPU handles, unavoidable copy counts, and per-device actuator commands.
- `Mimir.Well`: typed live pages, configured composite state, timing pressure,
  feature signals, capture-body publication, and the append-side observation
  ledger.
- `Mimir.VerseRecorder`: run session receipts, body indexing, replay witnesses,
  and `.cc`/CultCache-compatible export for offline reconstruction.
- `Mimir.FensalirDaemon`: reservoir job selection, queue pressure, GPU worker
  ownership, program/reservoir surfaces, and daemon state.
- Fensalir: D3D12 resource resolution, field claims, selected lowerings,
  temporal guide lanes, reservoir resolve, and program presentation.
- Faust/native DSP: hot audio alignment, sample movement, suppression,
  separation, spatialization, and program stems.
- Eve/CultUI lowerings: operator interaction and display of provider-owned
  state. They submit commands to owners; they do not own synchronization,
  reservoir jobs, OBS composition, or calibration truth.
- Odin: discovery of Mimir's published CultMesh namespaces and Eve surfaces.

## CultCache Witness Contract

Durable state should be typed CultCache `.cc`, or CultCache-compatible with a
`.cc` witness/export during migration. The witness set is:

| State family | Owner | Durable shape |
| --- | --- | --- |
| Sensor state | `Mimir.Runtime` plus native workers | Source inventory, device identity, driver profile, clock domain, format, actuator capability, native handle class, copy count, and last health. |
| Media state | `Mimir.Well` / Fensalir | Capture page metadata, resource keys, body refs, fence refs, page timing, configured composites, and program surface refs. |
| Observation state | `Mimir.Runtime` / Well | Typed audio/video/IMU/feature/path observations with source id, time range, confidence, calibration id, and payload/resource reference. |
| Ledger state | `Mimir.VerseRecorder` | Session receipts, body index, replay cursor, migration version, schema witness, and rejected/drop reasons. |
| Control state | Runtime owners / Fensalir daemon / DSP | Operator commands, accepted command receipts, selected program layer state, reservoir worker pressure, audio actuator commands, and stream/record/live status. |

JSON may remain only as schema publication, diagnostic text export, external
tool boundary, or temporary replay witness. If a JSON file can decide behavior
without a typed `.cc` witness, it is still an owner and must be migrated.

## CultMesh Namespaces

Mimir should publish these namespaces for Odin discovery:

- `verse.mimir.service.v1`: service manifest, run id, host identity, schema
  catalog, transport lanes, and nested Verse roots.
- `mimir.sensor.*`: source inventory, native worker health, device timing,
  media format, actuator capability, and producer fences.
- `mimir.media.*`: capture pages, media bodies, resource references, program
  texture/fence refs, stem refs, and replay body indexes.
- `mimir.observation.*`: sensor observations, media observations, feature
  tracks, audio path evidence, calibration constraints, and local frustum hints.
- `mimir.ledger.*`: recorder session, append cursor, migration state,
  body-index records, dropped-page reasons, and evidence receipts.
- `mimir.control.*`: operator commands, command receipts, recording/streaming
  state, runtime mode, source placement, DSP actuator state, and reservoir
  scheduling pressure.
- `mimir.eve.*`: `gamecult.eve.surface.v1` provider manifests, retained
  surfaces, field bindings, assets, command bindings, and lowering metadata.
- `mimir.fensalir.*`: daemon summary, reservoir worker state, reservoir pressure,
  program surfaces, resource resolver status, and selected lowering status.

Existing document names such as `mimir.well_snapshot.v1`,
`mimir.well_capture_page.v1`, `mimir.eve_sensor_observation.v1`,
`mimir.eve_media_observation.v1`, `mimir.eve_dashboard_state.v1`,
`mimir.fensalir_daemon_state.v1`, `mimir.fensalir_reservoir_worker_state.v1`,
and `mimir.fensalir_reservoir_pressure.v1` should be kept as concrete records
inside those namespaces until a schema catalog renames them deliberately.

## Transport Lanes

CultMesh reliable UDP is the default Verse transport for live Mimir network
state and media. TCP/WebSocket, SRT, browser streams, and local pipes are
diagnostic lowerings or external-boundary bridges unless a specific deployment
constraint temporarily promotes them.

The live lane split is:

- Typed control/state lane: compact documents such as timing state, command
  receipts, health, stream cursors, and backpressure.
- Media stream-frame lane: typed per-frame envelopes such as
  `mimir.cultmesh_stream_frame.v1` carrying source identity, capture time,
  resource/body refs, payload format, and confidence.
- Media body-shard lane: bounded CultCache media/page shards with hashes,
  cursors, receiver acknowledgements, and backpressure. Large bodies do not
  travel as inline base64.
- Latest-state dashboard lane: lossy/current Eve surfaces for operators. It
  may drop obsolete frames; it does not own durable media bodies.

Media streams are first-class Verse payloads. They still do not become timing
authority by arrival timestamp. Clock influence comes from decoded anchors,
source-local evidence, calibrated loopback, and explicit clock-domain state.

## Eve Surfaces

Mimir publishes operator presentation as Eve/CultUI DSL, preferably
`gamecult.eve.surface.v1`. The expected surfaces are:

- `mimir.sensor.field.surface`: sensor field view with source health, buffer
  depth, timing confidence, calibration status, copy count, media format,
  resource residency, and per-source observation pressure.
- `mimir.operator.flow.surface`: operator workflow for source selection,
  program placement, audio strips, record/live controls, reservoir pressure,
  sync mode, command receipts, and safe degraded-state visibility.
- `mimir.observation.ledger.surface`: session/replay view over observation
  counts, body refs, accepted/dropped pages, cursor state, and schema witness.
- `mimir.fensalir.reservoir.surface`: daemon-owned worker queue, ready ratio,
  timing confidence, GPU queue pressure, drop causes, and program surface refs.

The old dashboard broker, WebSocket `/eve/deck` lane, `/eve/dashboard`
compatibility endpoint, JSON health endpoints, JPEG streams, and browser
prototypes are lowerings or transport probes. They may mirror the Eve surface,
but they do not become presentation owners.

## Nested Verses

Mimir's service Verse contains smaller Verses with bounded authority:

- Room Verse: calibration truth, coordinate frame, sensor graph, clock model,
  configured composites, and operator identity for one physical room.
- Sensor Verse: one device or logical source, including driver health, timing
  domain, media format, actuator surface, and observation stream.
- Stream Verse: one synchronized media/control lane, including rolling-window
  cursor, capture pages, resource refs, body refs, and backpressure state.
- Operator Verse: Eve/CultUI command surface, selected source/layer state,
  command receipts, record/live state, and degraded-state visibility.

Nested Verses may publish local surfaces and state, but the parent Mimir service
owns discovery and contract routing. A sensor may report what it observed; it
does not become room clock authority unless explicitly promoted by calibration
state.

## Migration Order

1. Publish a `verse.mimir.service.v1` manifest that names active namespaces,
   schema ids, run id, nested Verse roots, and the current transport lanes.
2. Give each current JSONL/session/body-index path a `.cc` witness or
   CultCache-compatible export, starting with Well capture pages and
   `Mimir.VerseRecorder`.
3. Promote sensor, media, observation, ledger, and control state into explicit
   `mimir.*` CultMesh namespaces while preserving current document ids.
4. Publish `gamecult.eve.surface.v1` surfaces for sensor field and operator
   flows, with typed field bindings back to the owning state documents.
5. Make the old dashboard broker and `/eve/dashboard` compatibility endpoint
   consume those provider surfaces instead of assembling private truth.
6. Let Odin discover the service manifest and surfaces, then treat direct HTTP
   health/status probes as temporary diagnostics.
7. Delete or neuter any private JSON store, dashboard state cache, or UI callback
   path that can still decide service behavior after the typed owner exists.

## Demotion Line

Bespoke UI, browser dashboards, retained dashboard node lists, compatibility
WebSockets, SRT/FFmpeg media bridges, JSON status files, JSONL replay logs,
screenshot streams, and OBS bridge scripts are demoted to lowerings,
diagnostics, external-boundary export, or replay witnesses. They may display,
transport, inspect, or reconstruct typed Mimir state. They may not own sensor
truth, calibration truth, command state, reservoir scheduling, program
presentation, media-over-Verse policy, or synchronization behavior.
