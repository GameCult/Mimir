# Network Display Synchronization

Raven can be a low-level synchronized producer when it controls both its display
output and an audio timing signal routed into the Scarlett.

Authority map:

- Owner: `Mimir.Runtime` owns aligned rolling-buffer frames.
- Inputs: camera buffers, Raven display frames, Scarlett ASIO audio buffers, and
  explicit timing corrections learned from audio evidence.
- Outputs: `MimirSynchronizedBufferFrame` slices at one canonical presentation
  time for Fensalir/Faust/OBS publication.
- Derived state: Raven display timing is derived from the Raven audio sync
  signal. The display feed is not a timing authority by itself.
- Forbidden writers: OBS, SRT bridge endpoints, frame-event probes, and network
  receive timestamps must not decide cross-stream alignment.
- Shared path: local cameras, network display frames, mic channels, loopback
  channels, and Raven audio sync channels all enter `MimirRollingStreamBuffer`
  first, then the synchronized planner chooses slices.
- Deletion line: any compositor path that directly consumes Raven pixels outside
  the runtime buffer/alignment path is diagnostic only.

Practical shape:

1. Raven renders a visible timing marker or encoded frame identity on the
   captured display.
2. Raven emits the matching audio timing signal through its audio output.
3. That audio is physically or digitally routed into a Scarlett input beside the
   mic channels.
4. Mimir decodes the Scarlett audio evidence, learns the Raven clock offset, and
   applies that clock-domain correction to the `raven-display` video buffer.
5. Fensalir receives aligned buffer slices and still owns composition.

The disabled example in `config/mimir-runtime.asio.example.json` tags the
network display stream with `clockDomainId: raven-sync`. The display receiver is
expected to emit frame-event metadata with Raven source timestamps and frame
identity. The Scarlett channel carrying Raven's sync audio is the evidence path
that earns the `raven-sync` timing correction.

Smoke proof:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --synchronized-buffer-planner-smoke
```

