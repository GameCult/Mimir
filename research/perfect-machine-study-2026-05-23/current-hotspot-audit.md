# Current Hotspot Audit

## What This Is

This is a static audit of the current repo for likely allocation, scan, and
driver-pressure hotspots. It is not a profiler output. Treat it as a list of
places to benchmark once the next implementation cut starts.

## High-Risk Runtime Hotspots

### `MimirAudioSynchronizationAnalyzer.Analyze`

Why it is hot:

- Called repeatedly by `MimirSynchronizationHub`.
- Extracts windows from rolling buffers.
- Converts PCM formats.
- Resamples candidate windows.
- Invokes passive or active decode.

Risk shape:

- `ExtractMonoWindow` builds a `List<float>` and returns `ToArray`.
- Candidate windows can be re-extracted on every analysis step.
- Resampling allocates a new output array.
- Reports/traces allocate lists and records.

Coherent next cut:

- Add an analyzer scratch/context object owning reusable buffers.
- Add streaming active decoder state so repeated windows are not rescanned.
- Cache reference extracted window/spectrum per analysis edge.

### `MimirChirpBinTimeline.DecodeStreamWindow`

Why it is hot:

- Current active path for chirp-only/hybrid sync.
- Runs event proposal, classification, anchor decode, clock fit.

Risk shape:

- `BuildWindowEnergyTrace` allocates prefix/output arrays.
- `DetectFrames` sorts proposals by energy.
- `ScoreBins` scores every symbol for each candidate.
- `DecodeAnchors` creates candidate windows and path arrays.
- LINQ/`ToArray` appears in several inner-ish paths.

Coherent next cut:

- Split into streaming proposal state and stateless symbol scorer.
- Replace LINQ in candidate/code paths with bounded arrays/spans once behavior
  is frozen.
- Move calibration likelihood into the scoring stage before candidate pruning.

### `MimirPassiveAudioSynchronizationEstimator.Estimate`

Why it is hot:

- Passive/hybrid mode can compare multiple sources.
- FFT arrays allocate per estimate.

Risk shape:

- `Complex[]` reference/candidate arrays per call.
- Hann/preemphasis recomputed per call.
- Reference FFT repeated for each candidate.

Coherent next cut:

- Scratch object with reusable complex buffers.
- Precomputed window.
- Reference spectrum cache per edge.
- Limit lag search by known physical/network horizon.

### `MimirRuntime.QueueCalibrationTimeline`

Why it is hot-ish:

- Renders/encodes active witness audio inside runtime update.

Risk shape:

- Allocates PCM bytes and base64 every batch.
- Renders sine chirps with scalar `Math.Sin`.

Coherent next cut:

- For production, move witness generation to a reusable native/DSP oscillator or
  pre-rendered segment ring. Keep runtime policy, move audio hot work.

## Native Hotspots

### `native/asio_capture/pushInputBlocks`

Why it is hot:

- Runs from ASIO callback.
- Copies every input channel into a queued `std::vector<float>`.

Risk shape:

- Allocates/copies per channel block.
- Uses mutex-protected deque.

Current acceptability:

- Fine for proof and likely fine for four Scarlett channels at 192 kHz in short
  term.

Coherent next cut:

- Preallocated block pool or SPSC ring if profiling shows callback pressure.

### `native/probes/ks_camera_cadence/measureCandidate`

Why it is hot:

- Direct KS async read queue for camera cadence.

Risk shape:

- Diagnostic stdout and optional JSON events.
- Probe code owns too much CLI behavior for production.

Coherent next cut:

- Extract the async read queue shape into a direct driver worker, minus stdout
  and measurement ceremony.

### `native/reservoir/RollingReservoir`

Why it matters:

- Intended lower shared-edge retention authority.

Risk shape:

- Current Rust `VecDeque` and typed-view iteration are conceptually clean but
  not necessarily the final cache-optimal layout.

Coherent next cut:

- Keep invariant. Replace storage only when Fensalir/Faust integration proves
  the need.

## Allocation Smells Found By Static Scan

Likely production-relevant:

- `MimirChirpBinTimeline`: repeated `List<>`, `ToArray`, sorting, grouping in
  decode path.
- `MimirAudioSynchronizationAnalyzer`: window extraction and resampling arrays.
- `MimirPassiveAudioSynchronizationEstimator`: per-call FFT arrays.
- `MimirAsioStreamSource`: per-block byte arrays after native read.
- `MimirRollingStreamBuffer.Snapshot`: full array snapshot for readers.

Mostly diagnostic:

- `Mimir.BufferSmoke` allocations.
- Native probe stdout/JSON allocations.
- Bridge scripts.

## Rule For Next Optimization

Do not optimize the whole repo. Pick one measured hot path:

1. physical chirp-bin decode;
2. passive FFT reuse;
3. ASIO queue pressure;
4. camera driver handoff.

Then make one ownership-improving cut and benchmark before widening.

