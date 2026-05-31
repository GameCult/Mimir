# Eve Dashboard

## Objective

Use EveCanvas as a native operator dashboard substrate: Mimir, StreamPixels, and
other local/Yggdrasil-routed apps can publish retained dashboard trees while Eve
renders the control surface locally and sends edit intents back. Eve does not
own final composition or run remote UI code.

## Authority Map

- Owner: each dashboard provider owns its accepted state and command handling.
  The Starfire broker owns provider discovery/routing. EveCanvas owns native
  rendering, hit-testing, and touch affordances.
- Inputs: Eve sends `select`, `move`, `scale`, `rotate`, `toggle-visibility`,
  `reset-transform`, and `open-provider` commands over `/eve/deck`.
- Outputs: providers send `dashboard-state` snapshots with provider id, title,
  nodes, health, visibility, dimensions, transform, selected node, and LUT
  preset. Provider catalogs are exposed at `/eve/deck/providers`.
- Derived state: Eve's UIKit panels are local render projections of Mimir
  state/provider state.
- Forbidden writers: Eve gestures do not mutate program layout directly; they
  request changes and then re-render the accepted snapshot. Remote apps do not
  ship arbitrary JavaScript or native code into Eve.
- Cut line: no camera-stream fanout to Eve for the dashboard. Source previews
  may be added later as thumbnails/proxies, but dashboard control must not
  require Starfire to render every camera feed for the iPad.
- CultMesh line: dashboard manifests and states now have typed
  `mimir.eve_dashboard_manifest.v1` and `mimir.eve_dashboard_state.v1`
  document shapes. The live iPad lane remains WebSocket/TCP because it is the
  proven transport and works over SSH tunnels; CultMesh should mirror compact
  state where the local mesh substrate is available.

## Runtime

Start the dashboard server:

```powershell
dotnet run --project .\src\Mimir.EveDashboard\Mimir.EveDashboard.csproj -- --port 8795
```

EveCanvas connects to:

```text
ws://192.168.1.66:8795/eve/deck
```

The compatibility endpoint remains:

```text
ws://192.168.1.66:8795/eve/dashboard
```

Broker inspection endpoints:

```text
http://192.168.1.66:8795/health
http://192.168.1.66:8795/eve/deck/manifest
http://192.168.1.66:8795/eve/deck/providers
ws://192.168.1.66:8795/eve/deck/{providerId}
```

Extra providers can be registered at launch:

```powershell
dotnet run --project .\src\Mimir.EveDashboard\Mimir.EveDashboard.csproj -- `
  --port 8795 `
  --voidbot-swarm-state "E:\Projects\VoidBot\.voidbot\status\swarm-state.json" `
  --provider "app.id|App Title|ws://127.0.0.1:14000/eve/deck"
```

## Current State

`Mimir.EveDashboard` now runs as a small native dashboard broker. It includes:

- `eve.dashboard.broker`: the switchboard rendered as provider cards.
- `mimir.stream.layout`: the existing Mimir source-layout fixture provider.
- `voidbot.swarm`: the native VoidBot tab. It reads the existing
  `swarm-state.json` projection from VoidBot, publishes avatar image URLs on
  identity nodes, and lets Eve render the CTB rail, selected Face status panel,
  state tree, and detail pane as native UIKit. It is read-only in this cut;
  scheduler mutations still belong to VoidBot's heartbeat controls.
- `yggdrasil.streampixels.edge`: the first Yggdrasil/StreamPixels service
  dashboard placeholder for the TCP/SSH-routed live edge.

The Mimir provider still serves fixture scene nodes for the first native control
proof. It accepts transform/visibility commands, mutates the provider state,
increments the state version, and broadcasts the new snapshot. The next cut is
replacing fixture state with live `MimirPresentationControlState` and
`MimirSceneEditorState` snapshots.

## Verification

The command smoke connected to `/eve/dashboard`, received state version `1`,
sent:

```json
{"type":"move","nodeId":"eve-camera","x":0.25,"y":-0.25}
```

and received state version `2` with `eve-camera` moved to that transform. During
the same smoke, EveCanvas connected from `192.168.1.72` and remained running.

The first device screenshot proved the dashboard rendered on Eve, but also
showed the legacy debug overlay occluding the hierarchy panel. EveCanvas hides
that overlay while dashboard mode is active; dashboard status remains in the
native dashboard footer.

The broker smoke on 2026-05-31 verified:

- `/health` reports active provider, client count, transport, and typed
  CultMesh dashboard document name.
- `/eve/deck/providers` returns the built-in provider manifests.
- WebSocket `/eve/deck` sends the switchboard snapshot.
- `open-provider` switches to `mimir.stream.layout` and broadcasts that
  provider state.
- `open-provider` switches to `voidbot.swarm`, which emits VoidBot's swarm
  status, CTB rail with avatar URLs, agent cards, and selected Face state
  detail from the existing VoidBot swarm snapshot.
