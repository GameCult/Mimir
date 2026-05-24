namespace Mimir.Runtime.Synchronization;

public sealed record MimirMachineAssemblyPlan(
    string Id,
    string Description,
    MimirPerfectMachineProfile NodeProfile,
    MimirCalibrationSessionPlan Calibration,
    MimirAudioFieldConfiguration AudioField,
    MimirVisualFusionConfiguration VisualFusion,
    MimirComputeOffloadConfiguration Compute,
    string[] FirstProofCommands,
    string[] PromotionGates);

public static class MimirMachineAssemblyPlans
{
    public static MimirMachineAssemblyPlan SyntheticReceiverAndActuator { get; } = new(
        "synthetic-receiver-actuator",
        "Smallest coherent proof: bioacoustic identity, global clock fit, CultMesh contract shape, and actuator residual.",
        MimirPerfectMachineProfiles.CalibrationBench,
        MimirCalibrationSessionPlans.QuickSynthetic,
        MimirAudioFieldConfigurations.AlignedStemsSixMic,
        MimirVisualFusionConfigurations.CadenceProof,
        MimirComputeOffloadConfigurations.CalibrationSweep,
        [
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke",
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-train --sample-rate 48000 --seconds 0.75",
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-actuator-self-test --sample-rate 48000 --delay-samples 317.375"
        ],
        [
            "profile smoke passes",
            "training receipt persists CultCache and audio artifacts",
            "actuator residual remeasures below one microsecond"
        ]);

    public static MimirMachineAssemblyPlan ScarlettLoopbackAuthority { get; } = new(
        "scarlett-loopback-authority",
        "Electrical timing authority proof: local Scarlett loopback validates emitted timeline and DSP correction before room acoustics.",
        MimirPerfectMachineProfiles.StarfireAuthority,
        MimirCalibrationSessionPlans.ScarlettLoopback,
        MimirAudioFieldConfigurations.AlignedStemsSixMic,
        MimirVisualFusionConfigurations.CadenceProof,
        MimirComputeOffloadConfigurations.StarfireLocalHeavy,
        [
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --render-bioacoustic-f32 --seconds 8",
            "native\\probes\\asio_audio_cadence\\build\\Release\\asio_audio_cadence.exe --play-f32-mono artifacts\\asio\\bioacoustic-f32.raw --record-f32-interleaved artifacts\\asio\\scarlett-bioacoustic.f32 --sample-rate 192000 --seconds 8",
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --analyze-asio-f32 --input artifacts\\asio\\scarlett-bioacoustic.f32 --sample-rate 192000"
        ],
        [
            "loopback channels decode stable canonical anchors",
            "path state CultMesh document can be produced",
            "actuator target can be generated from sync state"
        ]);

    public static MimirMachineAssemblyPlan MeatspaceFieldCalibration { get; } = new(
        "meatspace-field-calibration",
        "Physical room proof: monitors, loopback, microphones, response weighting, and visual constraints start forming a field.",
        MimirPerfectMachineProfiles.StarfireAuthority,
        MimirCalibrationSessionPlans.MeatspaceRoom,
        MimirAudioFieldConfigurations.HybridEvidenceField,
        MimirVisualFusionConfigurations.AudioConstrainedField,
        MimirComputeOffloadConfigurations.StarfireLocalHeavy,
        [
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-train --sample-rate 48000 --seconds 8",
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --chirp-only-sync-self-test --sample-rate 192000",
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"
        ],
        [
            "at least one physical mic decodes stable canonical anchors",
            "path response identifies reliable bands",
            "field claim remains diagnostic until geometry residuals are bounded"
        ]);

    public static MimirMachineAssemblyPlan DistributedCultMeshWitnesses { get; } = new(
        "distributed-cultmesh-witnesses",
        "Remote receivers: Raven, phones, and tiny listeners decode local evidence and sync typed state through CultMesh.",
        MimirPerfectMachineProfiles.RavenRemote,
        MimirCalibrationSessionPlans.MeatspaceRoom,
        MimirAudioFieldConfigurations.SourceBasedSpatialBus,
        MimirVisualFusionConfigurations.AudioConstrainedField,
        MimirComputeOffloadConfigurations.DistributedWitnesses,
        [
            "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"
        ],
        [
            "remote codebook and decoder state round-trip through CultMesh",
            "remote path observations never claim canonical clock authority",
            "Starfire can combine remote anchors with local loopback truth"
        ]);

    public static IReadOnlyList<MimirMachineAssemblyPlan> BuiltIn { get; } =
    [
        SyntheticReceiverAndActuator,
        ScarlettLoopbackAuthority,
        MeatspaceFieldCalibration,
        DistributedCultMeshWitnesses
    ];
}
