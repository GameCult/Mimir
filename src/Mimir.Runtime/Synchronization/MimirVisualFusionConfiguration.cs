namespace Mimir.Runtime.Synchronization;

public enum MimirVisualSensorRole
{
    LeapStereoIrTiming,
    Ps3EyeHighRateTracking,
    KiyoRgbGroundTruth,
    KiyoProRgbGroundTruth,
    RemoteRgbOrTracking,
    DiagnosticOnly
}

public enum MimirVisualFusionModel
{
    TimestampedFramesOnly,
    SparseFeatureTracks,
    StereoSgmDepth,
    VisualConstrainedAudioField,
    TemporalGaussianClaims,
    FullHybridEvidenceField
}

public sealed record MimirVisualSensorConfiguration(
    string SourceId,
    MimirVisualSensorRole Role,
    int Width,
    int Height,
    double TargetFramesPerSecond,
    bool RequiresDirectDriver,
    bool PreferredGpuHandle,
    string Notes);

public sealed record MimirVisualFusionConfiguration(
    string Id,
    string Description,
    MimirVisualFusionModel Model,
    IReadOnlyList<MimirVisualSensorConfiguration> Sensors,
    bool RequiresFensalirD3D12,
    bool PublishesSpout,
    bool UsesAudioConstraints);

public static class MimirVisualFusionConfigurations
{
    public static MimirVisualFusionConfiguration CadenceProof { get; } = new(
        "cadence-proof",
        "Typed frame descriptors and sustained cadence only; no dense visual fusion claim.",
        MimirVisualFusionModel.TimestampedFramesOnly,
        CreateDefaultSensors(),
        RequiresFensalirD3D12: false,
        PublishesSpout: false,
        UsesAudioConstraints: false);

    public static MimirVisualFusionConfiguration TrackingFusion { get; } = new(
        "tracking-fusion",
        "High-rate PS3 Eye and Leap observations become sparse feature tracks; Kiyos provide RGB context.",
        MimirVisualFusionModel.SparseFeatureTracks,
        CreateDefaultSensors(),
        RequiresFensalirD3D12: true,
        PublishesSpout: false,
        UsesAudioConstraints: false);

    public static MimirVisualFusionConfiguration StereoSgmDepth { get; } = new(
        "stereo-sgm-depth",
        "Rectified synchronized stereo texture pairs lower through a Fensalir-owned D3D12 SGM depth lane.",
        MimirVisualFusionModel.StereoSgmDepth,
        CreateDefaultSensors(),
        RequiresFensalirD3D12: true,
        PublishesSpout: false,
        UsesAudioConstraints: false);

    public static MimirVisualFusionConfiguration AudioConstrainedField { get; } = new(
        "audio-constrained-field",
        "Visual feature tracks constrain acoustic source hypotheses before Fensalir stabilizes field claims.",
        MimirVisualFusionModel.VisualConstrainedAudioField,
        CreateDefaultSensors(),
        RequiresFensalirD3D12: true,
        PublishesSpout: false,
        UsesAudioConstraints: true);

    public static MimirVisualFusionConfiguration FullHybridEvidence { get; } = new(
        "full-hybrid-evidence",
        "Perfect Machine visual target: synchronized camera and acoustic constraints lower into Fensalir temporal evidence.",
        MimirVisualFusionModel.FullHybridEvidenceField,
        CreateDefaultSensors(),
        RequiresFensalirD3D12: true,
        PublishesSpout: true,
        UsesAudioConstraints: true);

    public static IReadOnlyList<MimirVisualFusionConfiguration> BuiltIn { get; } =
    [
        CadenceProof,
        TrackingFusion,
        StereoSgmDepth,
        AudioConstrainedField,
        FullHybridEvidence
    ];

    public static IReadOnlyList<MimirVisualSensorConfiguration> DefaultSensors { get; } = CreateDefaultSensors();

    private static IReadOnlyList<MimirVisualSensorConfiguration> CreateDefaultSensors() =>
    [
        new("leap-stereo-ir", MimirVisualSensorRole.LeapStereoIrTiming, 640, 240, 110.0, RequiresDirectDriver: true, PreferredGpuHandle: true, "Packed stereo IR pair; timing/close-range tracking candidate."),
        new("ps3-eye-0", MimirVisualSensorRole.Ps3EyeHighRateTracking, 320, 240, 187.0, RequiresDirectDriver: true, PreferredGpuHandle: true, "High-rate tracking witness; quality is secondary."),
        new("ps3-eye-1", MimirVisualSensorRole.Ps3EyeHighRateTracking, 320, 240, 187.0, RequiresDirectDriver: true, PreferredGpuHandle: true, "Second high-rate tracking witness for geometry."),
        new("kiyo-basic", MimirVisualSensorRole.KiyoRgbGroundTruth, 1920, 1080, 30.0, RequiresDirectDriver: true, PreferredGpuHandle: true, "RGB context/ground truth in known-good cadence modes."),
        new("kiyo-pro", MimirVisualSensorRole.KiyoProRgbGroundTruth, 1920, 1080, 25.0, RequiresDirectDriver: true, PreferredGpuHandle: true, "RGB ground truth; currently SuperSpeed/cadence-limited."),
        new("raven-camera", MimirVisualSensorRole.RemoteRgbOrTracking, 1280, 720, 30.0, RequiresDirectDriver: true, PreferredGpuHandle: false, "Remote feed enters as network producer, not clock authority.")
    ];
}
