namespace Mimir.Runtime.Synchronization;

public enum MimirPerfectMachineNodeRole
{
    StarfireAuthority,
    RavenRemote,
    PhoneWitness,
    MicrocontrollerWitness,
    NightwingEyesMoves,
    CalibrationBench,
    ObsProgramHost
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
    bool PublishesObsProgram,
    TimeSpan RollingWindow,
    string[] RequiredCultMeshDocuments);

public static class MimirPerfectMachineProfiles
{
    public static MimirPerfectMachineProfile StarfireAuthority { get; } = new(
        "starfire-authority",
        "Heavy local workstation: ASIO loopback authority, runtime buffers, calibration truth, Fensalir/Faust lowering, OBS-facing program output.",
        MimirPerfectMachineNodeRole.StarfireAuthority,
        MimirBioacousticDecoderConfiguration.BaselineMfccIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.HybridEvidenceField,
        [MimirComputeBackend.ManagedScalar, MimirComputeBackend.NativeSimd, MimirComputeBackend.D3D12Compute, MimirComputeBackend.FaustNativeDsp],
        EmitsWitness: true,
        OwnsCanonicalClock: true,
        PublishesObsProgram: true,
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
        PublishesObsProgram: false,
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
        PublishesObsProgram: false,
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
        PublishesObsProgram: false,
        RollingWindow: TimeSpan.FromSeconds(2),
        RequiredCultMeshDocuments:
        [
            "mimir.bioacoustic_codebook_state.v1",
            "mimir.bioacoustic_decoder_state.v1"
        ]);

    public static MimirPerfectMachineProfile NightwingEyesMoves { get; } = new(
        "nightwing-eyes-moves",
        "LAN visual witness: PS3 Eyes, PS Move Bluetooth/HID, LED schedules, and compact motion-marker histories.",
        MimirPerfectMachineNodeRole.NightwingEyesMoves,
        MimirBioacousticDecoderConfiguration.CompactFastIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.AlignedStems,
        [MimirComputeBackend.ManagedScalar, MimirComputeBackend.NativeSimd, MimirComputeBackend.RemoteCultMeshWorker],
        EmitsWitness: true,
        OwnsCanonicalClock: false,
        PublishesObsProgram: false,
        RollingWindow: TimeSpan.FromSeconds(3),
        RequiredCultMeshDocuments:
        [
            "mimir.move_controller_schedule_state.v1",
            "mimir.move_controller_observation_state.v1",
            "mimir.camera_feature_track_state.v1",
            "mimir.visual_calibration_state.v1"
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
        PublishesObsProgram: false,
        RollingWindow: TimeSpan.FromSeconds(8),
        RequiredCultMeshDocuments:
        [
            "mimir.bioacoustic_codebook_state.v1",
            "mimir.bioacoustic_decoder_state.v1",
            "mimir.acoustic_path_state.v1"
        ]);

    public static MimirPerfectMachineProfile ObsProgramHost { get; } = new(
        "obs-program-host",
        "Program-output profile: consumes aligned stems/spatial bed and publishes OBS-facing surfaces without becoming sync authority.",
        MimirPerfectMachineNodeRole.ObsProgramHost,
        MimirBioacousticDecoderConfiguration.BaselineMfccIndex,
        MimirAlignmentActuatorProfile.SixSourceFaust,
        MimirAudioFieldModel.SourceBasedSpatialBus,
        [MimirComputeBackend.FaustNativeDsp, MimirComputeBackend.D3D12Compute],
        EmitsWitness: false,
        OwnsCanonicalClock: false,
        PublishesObsProgram: true,
        RollingWindow: TimeSpan.FromSeconds(5),
        RequiredCultMeshDocuments:
        [
            "mimir.acoustic_path_state.v1",
            "mimir.actuator_state.v1"
        ]);

    public static IReadOnlyList<MimirPerfectMachineProfile> All { get; } =
    [
        StarfireAuthority,
        RavenRemote,
        PhoneWitness,
        MicrocontrollerWitness,
        NightwingEyesMoves,
        CalibrationBench,
        ObsProgramHost
    ];
}
