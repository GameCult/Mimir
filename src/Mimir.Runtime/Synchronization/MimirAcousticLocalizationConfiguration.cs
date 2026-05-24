namespace Mimir.Runtime.Synchronization;

public enum MimirAcousticLocalizationModel
{
    PairwiseTdoa,
    SrpPhatGrid,
    DelayAndSumBeamformer,
    EquivalentSparseSources,
    VisualConstrainedHybrid
}

public sealed record MimirAcousticLocalizationConfiguration(
    string Id,
    string Description,
    MimirAcousticLocalizationModel Model,
    string[] RequiredInputs,
    string[] OptionalInputs,
    double GridSpacingMeters,
    double SpeedOfSoundMetersPerSecond,
    double MinimumTimingConfidence,
    double TargetPositionUncertaintyMeters,
    bool EmitsFensalirConstraints,
    string Notes);

public static class MimirAcousticLocalizationConfigurations
{
    public static MimirAcousticLocalizationConfiguration PairwiseTdoa { get; } = new(
        "pairwise-tdoa-baseline",
        "Smallest position proof from calibrated pair delays.",
        MimirAcousticLocalizationModel.PairwiseTdoa,
        RequiredInputs: ["mic-geometry", "pairwise-delay-state"],
        OptionalInputs: ["speaker-position", "visual-prior"],
        GridSpacingMeters: 0.25,
        SpeedOfSoundMetersPerSecond: 343.0,
        MinimumTimingConfidence: 0.70,
        TargetPositionUncertaintyMeters: 0.50,
        EmitsFensalirConstraints: true,
        "Good first proof after real mic timing works.");

    public static MimirAcousticLocalizationConfiguration SrpPhatGrid { get; } = new(
        "srp-phat-grid",
        "Grid search over calibrated mic pair delays with PHAT-style robustness.",
        MimirAcousticLocalizationModel.SrpPhatGrid,
        RequiredInputs: ["mic-geometry", "pairwise-delay-state", "band-response"],
        OptionalInputs: ["visual-prior", "room-bounds"],
        GridSpacingMeters: 0.10,
        SpeedOfSoundMetersPerSecond: 343.0,
        MinimumTimingConfidence: 0.75,
        TargetPositionUncertaintyMeters: 0.25,
        EmitsFensalirConstraints: true,
        "Promote when pairwise baseline survives reflection enough to justify a grid.");

    public static MimirAcousticLocalizationConfiguration EquivalentSources { get; } = SrpPhatGrid with
    {
        Id = "equivalent-sparse-sources",
        Description = "Sparse physical source dictionary for volumetric field reconstruction.",
        Model = MimirAcousticLocalizationModel.EquivalentSparseSources,
        GridSpacingMeters = 0.15,
        MinimumTimingConfidence = 0.80,
        TargetPositionUncertaintyMeters = 0.20,
        Notes = "Use only after calibration gives a credible measurement model; inverse problems lie politely."
    };

    public static MimirAcousticLocalizationConfiguration VisualConstrainedHybrid { get; } = SrpPhatGrid with
    {
        Id = "visual-constrained-hybrid",
        Description = "Likely Mimir field model: acoustic candidates constrained by Fensalir visual tracks.",
        Model = MimirAcousticLocalizationModel.VisualConstrainedHybrid,
        RequiredInputs = ["mic-geometry", "pairwise-delay-state", "band-response", "fensalir-visual-claims"],
        OptionalInputs = ["speaker-position", "room-bounds", "motion-tracks"],
        TargetPositionUncertaintyMeters = 0.15,
        Notes = "Acoustic evidence becomes weighted constraints in the temporal field, not a solo oracle."
    };

    public static IReadOnlyList<MimirAcousticLocalizationConfiguration> BuiltIn { get; } =
    [
        PairwiseTdoa,
        SrpPhatGrid,
        EquivalentSources,
        VisualConstrainedHybrid
    ];
}
