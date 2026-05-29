# Program Control Surface

Mimir now exposes a compact Fensalir-hosted program panel for final presentation
intent.

Authority map:

- Owner: `MimirPresentationControlState` owns operator intent for program video,
  audio mix, and global color preset.
- Inputs: runtime rolling buffers, aligned audio lanes, LUT preset selection,
  feed visibility, feed opacity, stem mute/solo, and stem gain.
- Outputs: filtered video surface intents, Faust/native DSP gain controls,
  sample gain applied before Fensalir streaming DSP, and a postprocess control
  snapshot.
- Derived state: feed lists are derived from active rolling buffers. LUT preset
  exposure and bloom are currently mapped into `GraphicsSettings`.
- Forbidden writers: OBS, bridge endpoints, debug readouts, and network arrival
  timestamps must not choose program composition.
- Shared paths: local cameras, Raven display capture, and future network video
  feeds all enter rolling buffers first, then the presentation state decides
  whether they contribute to production surface intent.
- Deletion line: direct compositor shortcuts outside Fensalir FieldEvidence and
  audio DSP are diagnostic only.

The current UI lives in the `Mimir Program` panel:

- `Video`: select feed, visible, solo, opacity, layer ordering.
- `Audio`: select stem/source, mute, solo, gain.
- `Color`: LUT preset and LUT strength.

The shader-side LUT path is explicit state, not finished renderer authority yet.
`MimirLookupTablePreset` carries `LutPath`, strength, contrast, saturation,
temperature, and tint so Fensalir can consume the same contract when the global
post shader grows real LUT sampling. Until that renderer cut lands, Mimir maps
the preset exposure and bloom values into existing Fensalir `GraphicsSettings`.

Smoke proof:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --presentation-control-smoke
```

