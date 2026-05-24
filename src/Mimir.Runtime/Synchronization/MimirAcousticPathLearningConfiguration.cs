namespace Mimir.Runtime.Synchronization;

public enum MimirPathLearningTarget
{
    MagnitudeResponse,
    ConfusionMatrix,
    DelayHypothesis,
    FrequencyShiftHypothesis,
    PhaseGroupDelay,
    ReflectionRisk,
    CodebookAdaptation
}

public sealed record MimirPathLearningStage(
    string Id,
    MimirPathLearningTarget Target,
    double DurationSeconds,
    double MinimumConfidence,
    string[] Outputs,
    string[] Measurements);

public sealed record MimirAcousticPathLearningConfiguration(
    string Id,
    string Description,
    string OutputSourceId,
    string[] MicSourceIds,
    int SampleRate,
    double BufferSeconds,
    IReadOnlyList<MimirPathLearningStage> Stages,
    string PersistenceDocument,
    string Notes);

public static class MimirAcousticPathLearningConfigurations
{
    public static MimirAcousticPathLearningConfiguration StarfireRoom { get; } = new(
        "starfire-room-path-learning",
        "Measure local monitor to Starfire mic paths before trusting active acoustic timing.",
        "loopback-scarlett-speakers",
        ["scarlett-input-1", "scarlett-input-2", "kiyo-pro-mic", "ps3eye-left-mic", "ps3eye-right-mic"],
        SampleRate: 192_000,
        BufferSeconds: 5.0,
        Stages:
        [
            new("usable-band", MimirPathLearningTarget.MagnitudeResponse, 8.0, 0.30, ["left", "right"], ["per-bin-energy", "noise-floor"]),
            new("confusion", MimirPathLearningTarget.ConfusionMatrix, 12.0, 0.40, ["left", "right"], ["expected-symbol", "observed-energy-vector", "confidence"]),
            new("global-delay", MimirPathLearningTarget.DelayHypothesis, 10.0, 0.55, ["left", "right"], ["timeline-offset", "residual", "anchor-coverage"]),
            new("phase-delay", MimirPathLearningTarget.PhaseGroupDelay, 10.0, 0.45, ["left", "right"], ["phase-slope", "group-delay", "band-coherence"]),
            new("adapt-codebook", MimirPathLearningTarget.CodebookAdaptation, 4.0, 0.50, ["left", "right"], ["reliable-symbols", "recommended-order"])
        ],
        "mimir.acoustic_path_state",
        "This is the calibration command shape requested for physical paths.");

    public static MimirAcousticPathLearningConfiguration RavenRoundTrip { get; } = StarfireRoom with
    {
        Id = "raven-roundtrip-path-learning",
        Description = "Learn Raven loopback/mic paths and network delay as remote typed evidence.",
        OutputSourceId = "raven-loopback",
        MicSourceIds = ["raven-scarlett-input-1", "raven-scarlett-input-2"],
        PersistenceDocument = "mimir.acoustic_path_state",
        Notes = "Raven decodes locally; Starfire receives timing/path documents, not raw authority."
    };

    public static MimirAcousticPathLearningConfiguration PhoneWitness { get; } = StarfireRoom with
    {
        Id = "phone-witness-path-learning",
        Description = "Learn robust reduced-band paths for a phone microphone in the room.",
        MicSourceIds = ["phone-mic"],
        SampleRate = 48_000,
        Stages =
        [
            new("usable-band", MimirPathLearningTarget.MagnitudeResponse, 6.0, 0.25, ["left", "right"], ["mel-band-energy", "noise-floor"]),
            new("global-delay", MimirPathLearningTarget.DelayHypothesis, 8.0, 0.40, ["left", "right"], ["timeline-offset", "residual", "anchor-coverage"]),
            new("adapt-codebook", MimirPathLearningTarget.CodebookAdaptation, 4.0, 0.40, ["left", "right"], ["reliable-words", "recommended-profile"])
        ],
        Notes = "A reduced receiver must still place itself on the canonical source timeline."
    };

    public static IReadOnlyList<MimirAcousticPathLearningConfiguration> BuiltIn { get; } =
    [
        StarfireRoom,
        RavenRoundTrip,
        PhoneWitness
    ];
}
