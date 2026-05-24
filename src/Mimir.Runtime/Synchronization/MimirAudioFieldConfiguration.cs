namespace Mimir.Runtime.Synchronization;

public enum MimirSpatialOutputTarget
{
    ObsMonoStem,
    ObsStereoStem,
    FirstOrderAmbisonic,
    HigherOrderAmbisonic,
    FensalirDiagnosticField,
    CultMeshRemoteObservation
}

public enum MimirLocalizationModel
{
    None,
    TdoaPairwise,
    SrpPhatGrid,
    VisualConstrainedSrp,
    SparseEquivalentSourceGrid,
    HybridTemporalEvidence
}

public sealed record MimirMicrophoneGeometry(
    string SourceId,
    double X,
    double Y,
    double Z,
    string Role,
    double Reliability);

public sealed record MimirSpeakerGeometry(
    string SourceId,
    double X,
    double Y,
    double Z,
    string Role);

public sealed record MimirAudioFieldConfiguration(
    string Id,
    string Description,
    MimirAudioFieldModel FieldModel,
    MimirLocalizationModel Localization,
    MimirSpatialOutputTarget OutputTarget,
    int AmbisonicOrder,
    bool RequiresKnownMicGeometry,
    bool UsesVisualConstraints,
    IReadOnlyList<MimirMicrophoneGeometry> Microphones,
    IReadOnlyList<MimirSpeakerGeometry> Speakers);

public static class MimirAudioFieldConfigurations
{
    public static MimirAudioFieldConfiguration AlignedStemsSixMic { get; } = new(
        "aligned-stems-six-mic",
        "Production-first six-mic alignment: aligned host/co-streamer/context stems with no volumetric claim.",
        MimirAudioFieldModel.AlignedStems,
        MimirLocalizationModel.None,
        MimirSpatialOutputTarget.ObsStereoStem,
        AmbisonicOrder: 0,
        RequiresKnownMicGeometry: false,
        UsesVisualConstraints: false,
        Microphones: CreateDefaultMicrophones(),
        Speakers: CreateDefaultSpeakers());

    public static MimirAudioFieldConfiguration SourceBasedSpatialBus { get; } = new(
        "source-based-spatial-bus",
        "Honest first spatial bed: source tracks and known speaker/mic geometry drive a Faust spatial bus.",
        MimirAudioFieldModel.SourceBasedSpatialBus,
        MimirLocalizationModel.TdoaPairwise,
        MimirSpatialOutputTarget.ObsStereoStem,
        AmbisonicOrder: 0,
        RequiresKnownMicGeometry: true,
        UsesVisualConstraints: true,
        Microphones: CreateDefaultMicrophones(),
        Speakers: CreateDefaultSpeakers());

    public static MimirAudioFieldConfiguration FirstOrderAmbisonicBed { get; } = new(
        "foa-bed",
        "Low-order ambisonic bed for diffuse/context audio after synchronization earns trust.",
        MimirAudioFieldModel.FirstOrderAmbisonicBed,
        MimirLocalizationModel.VisualConstrainedSrp,
        MimirSpatialOutputTarget.FirstOrderAmbisonic,
        AmbisonicOrder: 1,
        RequiresKnownMicGeometry: true,
        UsesVisualConstraints: true,
        Microphones: CreateDefaultMicrophones(),
        Speakers: CreateDefaultSpeakers());

    public static MimirAudioFieldConfiguration HybridEvidenceField { get; } = new(
        "hybrid-evidence-field",
        "Perfect Machine target: synchronized acoustic observations become temporal evidence claims for Fensalir and Faust.",
        MimirAudioFieldModel.HybridEvidenceField,
        MimirLocalizationModel.HybridTemporalEvidence,
        MimirSpatialOutputTarget.FensalirDiagnosticField,
        AmbisonicOrder: 1,
        RequiresKnownMicGeometry: true,
        UsesVisualConstraints: true,
        Microphones: CreateDefaultMicrophones(),
        Speakers: CreateDefaultSpeakers());

    public static IReadOnlyList<MimirAudioFieldConfiguration> BuiltIn { get; } =
    [
        AlignedStemsSixMic,
        SourceBasedSpatialBus,
        FirstOrderAmbisonicBed,
        HybridEvidenceField
    ];

    public static IReadOnlyList<MimirMicrophoneGeometry> DefaultMicrophones { get; } = CreateDefaultMicrophones();

    public static IReadOnlyList<MimirSpeakerGeometry> DefaultSpeakers { get; } = CreateDefaultSpeakers();

    private static IReadOnlyList<MimirMicrophoneGeometry> CreateDefaultMicrophones() =>
    [
        new("scarlett-host-mic", 0.0, 0.0, 1.25, "dialogue-anchor", 1.0),
        new("scarlett-raven-mic", 1.5, 0.8, 1.25, "co-streamer-anchor", 0.95),
        new("kiyo-basic-mic", -0.6, 0.3, 1.1, "room-context", 0.45),
        new("kiyo-pro-mic", 0.7, 0.2, 1.1, "rgb-ground-context", 0.45),
        new("ps3-eye-0-mic", -0.9, -0.4, 1.0, "tracking-context", 0.40),
        new("ps3-eye-1-mic", 0.9, -0.4, 1.0, "tracking-context", 0.40)
    ];

    private static IReadOnlyList<MimirSpeakerGeometry> CreateDefaultSpeakers() =>
    [
        new("scarlett-left-monitor", -0.65, 0.7, 1.15, "left-reference"),
        new("scarlett-right-monitor", 0.65, 0.7, 1.15, "right-reference")
    ];
}
