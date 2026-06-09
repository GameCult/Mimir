namespace Mimir.Runtime.Synchronization;

public enum MimirPerfectMachineNodeRole
{
    StarfireAuthority,
    RavenRemote,
    MuninnHost,
    PhoneWitness,
    MicrocontrollerWitness,
    CalibrationBench,
    MimirProgramPublisher
}

public enum MimirAudioFieldModel
{
    AlignedStems,
    SourceBasedSpatialBus,
    FirstOrderAmbisonicBed,
    SparseEquivalentSources,
    HybridEvidenceField
}

public enum MimirComputeBackend
{
    ManagedScalar,
    NativeSimd,
    D3D12Compute,
    FaustNativeDsp,
    RemoteCultMeshWorker
}

public sealed record MimirPerfectMachineProfile(
    string Id,
    string Description,
    MimirPerfectMachineNodeRole Role,
    MimirBioacousticDecoderConfiguration Decoder,
    MimirAlignmentActuatorProfile Actuator,
    MimirAudioFieldModel AudioFieldModel,
    IReadOnlyList<MimirComputeBackend> ComputeBackends,
    bool EmitsWitness,
    bool OwnsCanonicalClock,
    bool PublishesProgram,
    TimeSpan RollingWindow,
    string[] RequiredCultMeshDocuments);

public static class MimirPerfectMachineProfiles
{
    public static MimirPerfectMachineProfile StarfireAuthority { get; } = new(
        "starfire-authority",
        "Heavy local workstation: ASIO loopback authority, runtime buffers, calibration truth, Mimir composition, Fensalir/Faust lowering, Eve operator surfaces, and program publication.",
        MimirPerfectMachineNodeRole.StarfireAuthority,
        MimirBioacousticDecoderConfiguration.BaselineMfccIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.HybridEvidenceField,
        [MimirComputeBackend.ManagedScalar, MimirComputeBackend.NativeSimd, MimirComputeBackend.D3D12Compute, MimirComputeBackend.FaustNativeDsp],
        EmitsWitness: true,
        OwnsCanonicalClock: true,
        PublishesProgram: true,
        RollingWindow: TimeSpan.FromSeconds(5),
        RequiredCultMeshDocuments:
        [
            "mimir.bioacoustic_codebook_state.v1",
            "mimir.bioacoustic_decoder_state.v1",
            "mimir.acoustic_path_state.v1",
            "mimir.actuator_state.v1"
        ]);

    public static MimirPerfectMachineProfile RavenRemote { get; } = new(
        "raven-remote",
        "Co-streamer/game machine: captures local loopback/mic evidence, decodes against shared schedule, ships typed observations back to Starfire.",
        MimirPerfectMachineNodeRole.RavenRemote,
        MimirBioacousticDecoderConfiguration.CompactFastIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.AlignedStems,
        [MimirComputeBackend.ManagedScalar, MimirComputeBackend.RemoteCultMeshWorker],
        EmitsWitness: false,
        OwnsCanonicalClock: false,
        PublishesProgram: false,
        RollingWindow: TimeSpan.FromSeconds(5),
        RequiredCultMeshDocuments:
        [
            "mimir.bioacoustic_codebook_state.v1",
            "mimir.bioacoustic_decoder_state.v1",
            "mimir.acoustic_path_state.v1"
        ]);

    public static MimirPerfectMachineProfile PhoneWitness { get; } = new(
        "phone-witness",
        "Small listener: knows codebook/schedule, decodes local mic anchors, reports clock/path observations without owning global truth.",
        MimirPerfectMachineNodeRole.PhoneWitness,
        MimirBioacousticDecoderConfiguration.CompactFastIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.AlignedStems,
        [MimirComputeBackend.ManagedScalar, MimirComputeBackend.RemoteCultMeshWorker],
        EmitsWitness: false,
        OwnsCanonicalClock: false,
        PublishesProgram: false,
        RollingWindow: TimeSpan.FromSeconds(3),
        RequiredCultMeshDocuments:
        [
            "mimir.bioacoustic_codebook_state.v1",
            "mimir.bioacoustic_decoder_state.v1"
        ]);

    public static MimirPerfectMachineProfile MicrocontrollerWitness { get; } = new(
        "microcontroller-witness",
        "Tiny listener profile: narrow codebook/schedule receiver that reports sparse anchors and health, not full audio.",
        MimirPerfectMachineNodeRole.MicrocontrollerWitness,
        MimirBioacousticDecoderConfiguration.HighbandRoomIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.AlignedStems,
        [MimirComputeBackend.RemoteCultMeshWorker],
        EmitsWitness: false,
        OwnsCanonicalClock: false,
        PublishesProgram: false,
        RollingWindow: TimeSpan.FromSeconds(2),
        RequiredCultMeshDocuments:
        [
            "mimir.bioacoustic_codebook_state.v1",
            "mimir.bioacoustic_decoder_state.v1"
        ]);

    public static MimirPerfectMachineProfile CalibrationBench { get; } = new(
        "calibration-bench",
        "Offline or live calibration worker: explores decoder/codebook/path variants and preserves receipts.",
        MimirPerfectMachineNodeRole.CalibrationBench,
        MimirBioacousticDecoderConfiguration.RobustWideIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.SparseEquivalentSources,
        [MimirComputeBackend.ManagedScalar, MimirComputeBackend.NativeSimd, MimirComputeBackend.D3D12Compute],
        EmitsWitness: true,
        OwnsCanonicalClock: false,
        PublishesProgram: false,
        RollingWindow: TimeSpan.FromSeconds(8),
        RequiredCultMeshDocuments:
        [
            "mimir.bioacoustic_codebook_state.v1",
            "mimir.bioacoustic_decoder_state.v1",
            "mimir.acoustic_path_state.v1"
        ]);

    public static MimirPerfectMachineProfile MuninnHost { get; } = new(
        "muninn-host",
        "Capture host profile: Muninn observes local screens, windows, loopbacks, cameras, and device health, publishes typed stream capabilities/media, and does not compose the final program.",
        MimirPerfectMachineNodeRole.MuninnHost,
        MimirBioacousticDecoderConfiguration.CompactFastIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.AlignedStems,
        [MimirComputeBackend.ManagedScalar, MimirComputeBackend.RemoteCultMeshWorker],
        EmitsWitness: false,
        OwnsCanonicalClock: false,
        PublishesProgram: false,
        RollingWindow: TimeSpan.FromSeconds(5),
        RequiredCultMeshDocuments:
        [
            "muninn.telemetry_surface.v1",
            "muninn.capture_stream.v1",
            "mimir.cultmesh_media_frame.v1"
        ]);

    public static MimirPerfectMachineProfile MimirProgramPublisher { get; } = new(
        "mimir-program-publisher",
        "Program-output profile: consumes selected Muninn/Mimir streams, owns scene composition and Eve controls, and publishes the program locally and through Yggdrasil without OBS authority.",
        MimirPerfectMachineNodeRole.MimirProgramPublisher,
        MimirBioacousticDecoderConfiguration.BaselineMfccIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.SourceBasedSpatialBus,
        [MimirComputeBackend.FaustNativeDsp, MimirComputeBackend.D3D12Compute],
        EmitsWitness: false,
        OwnsCanonicalClock: false,
        PublishesProgram: true,
        RollingWindow: TimeSpan.FromSeconds(5),
        RequiredCultMeshDocuments:
        [
            "mimir.acoustic_path_state.v1",
            "mimir.actuator_state.v1",
            "mimir.program_scene.v1",
            "mimir.program_output.v1",
            "mimir.eve_operator_surface.v1"
        ]);

    public static IReadOnlyList<MimirPerfectMachineProfile> All { get; } =
    [
        StarfireAuthority,
        RavenRemote,
        PhoneWitness,
        MicrocontrollerWitness,
        CalibrationBench,
        MuninnHost,
        MimirProgramPublisher
    ];
}
