# Perfect Machine Module Library

This is the runtime shelf for coherent parts that deserve to be inside Mimir,
not a trophy wall for abandoned attempts. Each module below has one owner and a
job-interview answer for why it belongs in the body of the machine.

## Runtime Modules

| Module | Owner | Purpose | Code |
| --- | --- | --- | --- |
| Bioacoustic decoder profiles | `Mimir.Runtime` | Named indexed log-mel/MFCC receiver configurations for clean, compact, robust, and high-band room paths. | `src/Mimir.Runtime/Synchronization/MimirBioacousticDecoderConfiguration.cs` |
| Bioacoustic language profiles | `Mimir.Runtime` | Passive, hybrid, continuous, calibration, and phone-witness vocabulary/emission configurations. | `src/Mimir.Runtime/Synchronization/MimirBioacousticLanguageConfiguration.cs` |
| Bioacoustic clock solver | `Mimir.Runtime` | Fits canonical source time from decoded word anchors for local, Raven, phone, and microcontroller witnesses. | `src/Mimir.Runtime/Synchronization/MimirBioacousticClockHypothesis.cs` |
| Alignment actuator | `Mimir.Runtime` + Faust | Converts sync state into bounded fractional-delay, gain, and sample-rate-offset controls; Faust moves samples. | `src/Mimir.Runtime/Synchronization/MimirAudioAlignmentActuator.cs` |
| Audio actuator strategies | `Mimir.Runtime` + Faust | Integer baseline, Farrow fractional delay, variable ASRC, and hybrid delay/ASRC strategy profiles. | `src/Mimir.Runtime/Synchronization/MimirAudioActuatorConfiguration.cs` |
| CultMesh contracts | `Mimir.Runtime` | Typed codebook, decoder, acoustic path, and actuator state documents for distributed Mimir nodes. | `src/Mimir.Runtime/Synchronization/MimirCultMeshContracts.cs` |
| Acoustic path learning | `Mimir.Runtime` | Calibration stages for usable bands, confusion, global delay, group delay, and codebook adaptation. | `src/Mimir.Runtime/Synchronization/MimirAcousticPathLearningConfiguration.cs` |
| Acoustic localization | `Mimir.Runtime` + Fensalir | Pairwise TDOA, SRP-PHAT grid, sparse source, and visual-constrained localization profiles plus a small SRP/TDOA scorer. | `src/Mimir.Runtime/Synchronization/MimirAcousticLocalizationConfiguration.cs`, `src/Mimir.Runtime/Synchronization/MimirAcousticLocalizationSolver.cs` |
| Benchmark panels | `Mimir.Runtime` | Decoder golf and meatspace acceptance degradation panels with receipt roots and thresholds. | `src/Mimir.Runtime/Synchronization/MimirBenchmarkPanelConfiguration.cs` |
| Capture profiles | `Mimir.Runtime` + native workers | Hardware intent for Leap, Kiyos, PS Eyes, Starfire Scarlett, and Raven Scarlett. | `src/Mimir.Runtime/Synchronization/MimirNativeCaptureConfiguration.cs` |
| Camera ingest strategies | `Mimir.Runtime` + native workers | Diagnostic metadata, managed native wrapper, native SPSC ring, and shared-GPU-texture ingest shapes. | `src/Mimir.Runtime/Synchronization/MimirCameraIngestConfiguration.cs` |
| Stereo depth profiles | `Mimir.Runtime` + Fensalir | SGM-shaped stereo disparity/depth profiles with libSGM as provenance for the first D3D12 lane, not a CUDA dependency. | `src/Mimir.Runtime/Synchronization/MimirStereoDepthConfiguration.cs` |
| Reservoir strategies | `Mimir.Runtime` + native reservoir + Fensalir | Managed rolling buffers, native shared-edge rings, and derived GPU temporal field storage. | `src/Mimir.Runtime/Synchronization/MimirReservoirConfiguration.cs` |
| Distributed witnesses | `Mimir.Runtime` + CultMesh | Raven, phone, microcontroller, Nightwing Eyes/Moves, and remote camera rig configurations for compact timing/path/visual state. | `src/Mimir.Runtime/Synchronization/MimirDistributedWitnessConfiguration.cs` |
| Network transports | `Mimir.Runtime` + CultMesh | Typed timing state, reliable-UDP Verse media/body lanes, debug windows, SRT bridge media, and experimental browser transport policy plus constraint-based selection. | `src/Mimir.Runtime/Synchronization/MimirNetworkTransportConfiguration.cs`, `src/Mimir.Runtime/Synchronization/MimirNetworkTransportSelector.cs` |
| Authority policy | `Mimir.Runtime` | Trust decisions for Starfire loopback, Raven evidence, phone candidates, diagnostic media, and unknown nodes. | `src/Mimir.Runtime/Synchronization/MimirAuthorityPolicyConfiguration.cs`, `src/Mimir.Runtime/Synchronization/MimirAuthorityPolicyEvaluator.cs` |
| Audio field configurations | `Mimir.Runtime` | Six-mic, source-based, ambisonic, and hybrid evidence field shapes. | `src/Mimir.Runtime/Synchronization/MimirAudioFieldConfiguration.cs` |
| Visual fusion configurations | `Mimir.Runtime` + Fensalir | Sensor roles and fusion modes from cadence proof through full hybrid evidence. | `src/Mimir.Runtime/Synchronization/MimirVisualFusionConfiguration.cs` |
| Compute offload configurations | `Mimir.Runtime` + CultMesh | Local-heavy, distributed witness, and calibration sweep workload placement. | `src/Mimir.Runtime/Synchronization/MimirComputeOffloadConfiguration.cs` |
| Assembly plans | `Mimir.Runtime` | Named combinations of modules that prove synthetic, Scarlett, meatspace, and distributed cuts. | `src/Mimir.Runtime/Synchronization/MimirMachineAssemblyPlan.cs` |
| OBS publication profiles | Fensalir + Faust + OBS | Spout/shared-texture video and separately mixable Faust stem publication. | `src/Mimir.Runtime/Synchronization/MimirObsPublicationConfiguration.cs` |
| Fensalir lowering | `Mimir.Runtime` -> Fensalir | Pure mapping from Mimir rolling/video/audio state into Fensalir GPU sensor, stereo-depth, and acoustic field frames. | `src/Mimir.Runtime/Synchronization/MimirFensalirFieldLowering.cs` |
| Module catalog | `Mimir.Runtime` | Indexes the live module set and verification commands. | `src/Mimir.Runtime/Synchronization/MimirModuleLibrary.cs` |
| Manifest factory | `Mimir.Runtime` | Produces an exportable JSON manifest for tools, docs, UI, and remote witnesses. | `src/Mimir.Runtime/Synchronization/MimirPerfectMachineManifest.cs` |

## Current Authority Map

- `Mimir.Runtime` owns device and synchronization truth before it becomes a
  renderer/DSP packet.
- Fensalir owns GPU resource lifetime, temporal field rendering, debug UI, and
  program video publication.
- Faust owns hot sample movement, alignment, spatialization, separation, and
  stem generation.
- CultMesh carries typed state surfaces and reliable-UDP media/body lanes;
  remote nodes may observe, decode, and stream media through the Verse, but
  they do not become independent clock authorities.
- OBS receives final program surfaces and stems. It does not own calibration.

## Verification Surface

Use these as quick probes after touching the module shelf:

```powershell
dotnet build .\Mimir.slnx
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --perfect-machine-lowering-benchmark
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --perfect-machine-contract-smoke
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --perfect-machine-manifest
dotnet run --no-build --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- --bioacoustic-actuator-self-test
```

## Research Links

- [[research/perfect-machine-study-2026-05-23/index|Perfect Machine Study Index]]
- [[research/perfect-machine-study-2026-05-23/option-matrix|Option Matrix]]
- [[research/perfect-machine-study-2026-05-23/state-machine-invariants|State Machine Invariants]]
- [[research/perfect-machine-study-2026-05-23/fensalir-integration-map|Fensalir Integration Map]]
- [[research/perfect-machine-study-2026-05-23/optimization-ledger|Optimization Ledger]]

## Cut Line

If a future implementation supersedes one of these module shapes, delete or
demote the old module in the same pass. The library is meant to keep usable
organs ready at hand, not preserve every fossil that once twitched.
