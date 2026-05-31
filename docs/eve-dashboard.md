# Eve Dashboard

## Objective

Use EveCanvas as a native Mimir operator dashboard: scene graph, source panels,
visibility/reset controls, and multitouch transform editing. Eve renders the
control surface locally and sends edit intents to Mimir; it does not own final
composition.

## Authority Map

- Owner: Mimir owns accepted dashboard state and transform truth.
- Inputs: Eve sends `select`, `move`, `scale`, `rotate`, `toggle-visibility`,
  and `reset-transform` commands over `/eve/dashboard`.
- Outputs: Mimir sends `dashboard-state` snapshots with nodes, health,
  visibility, dimensions, transform, selected node, and LUT preset.
- Derived state: Eve's UIKit panels are local render projections of Mimir
  state.
- Forbidden writers: Eve gestures do not mutate program layout directly; they
  request changes and then re-render the accepted snapshot.
- Cut line: no camera-stream fanout to Eve for the dashboard. Source previews
  may be added later as thumbnails/proxies, but dashboard control must not
  require Starfire to render every camera feed for the iPad.

## Runtime

Start the dashboard server:

```powershell
dotnet run --project .\src\Mimir.EveDashboard\Mimir.EveDashboard.csproj -- --port 8795
```

EveCanvas connects to:

```text
ws://192.168.1.66:8795/eve/dashboard
```

## Current State

`Mimir.EveDashboard` currently serves fixture scene nodes for the first native
control proof. It accepts transform/visibility commands, mutates the server-side
state, increments the state version, and broadcasts the new snapshot. The next
cut is replacing fixture state with live `MimirPresentationControlState` and
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
