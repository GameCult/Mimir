# Mimir Scene Editor Control Surface

Mimir now has a runtime-owned scene editor state for arranging the visible
presentation inputs inside the Fensalir-hosted Mimir window. This editor is not
the OBS program output. It is an operator surface for placing source panels,
text panels, and imported-model placeholders before later renderer cuts turn
those nodes into final composited pixels or volumetric field claims.

## Authority Map

- Owner: `MimirSceneEditorState` owns editor camera, scene nodes, selected node,
  visibility, lock state, 2D-plane transform, and active gizmo mode.
- Inputs: rolling video buffers provide sensor-feed nodes; Fensalir `InputState`
  provides mouse/key edits; the Mimir UI provides visibility, reset, transform,
  text, and model-path commands.
- Outputs: `MimirRuntime` reads the editor state into the Fensalir frame camera,
  spline outlines, selection gizmos, and SDF handle markers.
- Derived state: the hierarchy text, selected-node readout, editor splines, and
  handle SDF objects are derived from the editor graph. They do not own node
  truth.
- Forbidden writers: program-composite controls, OBS source endpoints, bridge
  scripts, and FieldEvidence surface intents do not decide editor transforms.
- Shared paths: UI buttons/sliders and mouse-drag gizmos all commit through
  `MimirSceneEditorState`; reset returns to each node's stored default
  transform.
- Deletion line: do not add a separate transform cache in the renderer or OBS
  path. Fensalir may later render richer node types, but it should consume this
  graph or a deliberate successor, not invent a competing editor graph.

## Current Controls

- `Mimir Editor` panel exposes the scene graph, selected-node visibility and
  lock toggles, transform reset, camera reset, and transform sliders.
- Active gizmo modes are `Grab`, `Rotate`, and `Resize`; keys `1`, `2`, and `3`
  switch modes, and left-drag applies the active mode to the selected node.
- Sensor-feed nodes are created from live video rolling buffers.
- SDF text panels and model placeholders can be added from the panel.
- `--scene-editor-smoke` proves graph creation, visibility toggles, transform
  reset, mouse-drag transform edits, text-node creation, model placeholder
  import, and derived gizmo geometry.

## Renderer Hooks Still Open

- World SDF text: Fensalir currently has DirectWrite overlay text and SDF object
  proxies. Scene text needs a renderer-owned SDF/MSDF glyph atlas plus billboard
  or mesh text lowering before text glyphs are truly drawn inside the scene.
- Imported models: Fensalir resource contracts already know mesh packages, but
  selected mesh draw/material lowerings are still future work. The current
  import control creates graph nodes with paths; it does not decode ASSIMP
  meshes or upload vertex/index resources yet.
- Pixel-accurate gizmo hit-testing: current gizmo edits apply to the selected
  node. Fensalir needs an editor hit-test path before handles can be clicked
  directly in world space.
