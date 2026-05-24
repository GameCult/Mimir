namespace Mimir.Runtime.Synchronization;

public sealed record MimirCepstralDegradationProfile(
    string Id,
    double WarpFrames,
    double WarpCoefficients,
    int BlurPasses);

public sealed record MimirBioacousticDecoderConfiguration(
    string Id,
    string Description,
    int FftSize,
    int HopSize,
    int MelBins,
    int CepstralCoefficients,
    double MinFrequencyHz,
    double MaxFrequencyHz,
    int ProjectionTableCount,
    int ProjectionHashBits,
    int NearHashRadius,
    double DenseStepSeconds,
    double ProposalBudgetMultiplier,
    IReadOnlyList<MimirCepstralDegradationProfile> TemplateAugmentations)
{
    public static MimirBioacousticDecoderConfiguration BaselineMfccIndex { get; } = new(
        "baseline-mfcc-index",
        "Balanced indexed MFCC/log-mel receiver shape from the current bioacoustic smoke path.",
        FftSize: 1024,
        HopSize: 256,
        MelBins: 40,
        CepstralCoefficients: 14,
        MinFrequencyHz: 180.0,
        MaxFrequencyHz: 15_000.0,
        ProjectionTableCount: 4,
        ProjectionHashBits: 14,
        NearHashRadius: 4,
        DenseStepSeconds: 0.040,
        ProposalBudgetMultiplier: 8.0,
        TemplateAugmentations:
        [
            CepstralDegradationProfiles.Clean,
            CepstralDegradationProfiles.Blur,
            CepstralDegradationProfiles.WarpLight,
            CepstralDegradationProfiles.WarpBlur
        ]);

    public static MimirBioacousticDecoderConfiguration CompactFastIndex { get; } = new(
        "compact-fast-index",
        "Smaller feature and projection surface for clean-throughput pressure.",
        FftSize: 1024,
        HopSize: 256,
        MelBins: 32,
        CepstralCoefficients: 10,
        MinFrequencyHz: 300.0,
        MaxFrequencyHz: 14_500.0,
        ProjectionTableCount: 3,
        ProjectionHashBits: 12,
        NearHashRadius: 3,
        DenseStepSeconds: 0.040,
        ProposalBudgetMultiplier: 6.0,
        TemplateAugmentations:
        [
            CepstralDegradationProfiles.Clean,
            CepstralDegradationProfiles.Blur,
            CepstralDegradationProfiles.WarpBlur
        ]);

    public static MimirBioacousticDecoderConfiguration PacketSprintIndex { get; } = new(
        "packet-sprint-index",
        "Smallest packet-song receiver profile: fewer mel bins, fewer projections, and a tighter proposal budget for language-score throughput pressure.",
        FftSize: 1024,
        HopSize: 256,
        MelBins: 24,
        CepstralCoefficients: 8,
        MinFrequencyHz: 600.0,
        MaxFrequencyHz: 14_500.0,
        ProjectionTableCount: 2,
        ProjectionHashBits: 10,
        NearHashRadius: 2,
        DenseStepSeconds: 0.050,
        ProposalBudgetMultiplier: 4.0,
        TemplateAugmentations:
        [
            CepstralDegradationProfiles.Clean,
            CepstralDegradationProfiles.Blur,
            CepstralDegradationProfiles.WarpBlur
        ]);

    public static MimirBioacousticDecoderConfiguration PacketNeedleIndex { get; } = new(
        "packet-needle-index",
        "Tiny fast receiver for short packet-song words; tests whether smaller FFT/features beat the loss in frequency identity.",
        FftSize: 512,
        HopSize: 128,
        MelBins: 20,
        CepstralCoefficients: 7,
        MinFrequencyHz: 700.0,
        MaxFrequencyHz: 14_500.0,
        ProjectionTableCount: 2,
        ProjectionHashBits: 9,
        NearHashRadius: 2,
        DenseStepSeconds: 0.060,
        ProposalBudgetMultiplier: 3.0,
        TemplateAugmentations:
        [
            CepstralDegradationProfiles.Clean,
            CepstralDegradationProfiles.Blur,
            CepstralDegradationProfiles.WarpBlur
        ]);

    public static MimirBioacousticDecoderConfiguration PacketRazorIndex { get; } = new(
        "packet-razor-index",
        "Aggressive speed floor for packet-song decoding; intentionally risks identity for realtime multiplier evidence.",
        FftSize: 512,
        HopSize: 128,
        MelBins: 16,
        CepstralCoefficients: 6,
        MinFrequencyHz: 850.0,
        MaxFrequencyHz: 14_000.0,
        ProjectionTableCount: 1,
        ProjectionHashBits: 8,
        NearHashRadius: 1,
        DenseStepSeconds: 0.075,
        ProposalBudgetMultiplier: 2.25,
        TemplateAugmentations:
        [
            CepstralDegradationProfiles.Clean,
            CepstralDegradationProfiles.WarpLight,
            CepstralDegradationProfiles.WarpBlur
        ]);

    public static MimirBioacousticDecoderConfiguration RobustWideIndex { get; } = new(
        "robust-wide-index",
        "Wider cepstral and augmentation surface for path-damaged acoustic evidence.",
        FftSize: 1024,
        HopSize: 192,
        MelBins: 48,
        CepstralCoefficients: 18,
        MinFrequencyHz: 180.0,
        MaxFrequencyHz: 15_500.0,
        ProjectionTableCount: 5,
        ProjectionHashBits: 15,
        NearHashRadius: 4,
        DenseStepSeconds: 0.030,
        ProposalBudgetMultiplier: 9.0,
        TemplateAugmentations:
        [
            CepstralDegradationProfiles.Clean,
            CepstralDegradationProfiles.Blur,
            CepstralDegradationProfiles.WarpLight,
            CepstralDegradationProfiles.WarpBlur,
            CepstralDegradationProfiles.WarpHeavy
        ]);

    public static MimirBioacousticDecoderConfiguration HighbandRoomIndex { get; } = new(
        "highband-room-index",
        "Upper-band-biased receiver to avoid low room junk and test monitor/mic highband survival.",
        FftSize: 1024,
        HopSize: 256,
        MelBins: 40,
        CepstralCoefficients: 14,
        MinFrequencyHz: 1_200.0,
        MaxFrequencyHz: 16_000.0,
        ProjectionTableCount: 4,
        ProjectionHashBits: 14,
        NearHashRadius: 4,
        DenseStepSeconds: 0.035,
        ProposalBudgetMultiplier: 8.0,
        TemplateAugmentations:
        [
            CepstralDegradationProfiles.Clean,
            CepstralDegradationProfiles.Blur,
            CepstralDegradationProfiles.WarpLight,
            CepstralDegradationProfiles.WarpBlur
        ]);

    public static IReadOnlyList<MimirBioacousticDecoderConfiguration> BuiltInProfiles { get; } =
    [
        BaselineMfccIndex,
        CompactFastIndex,
        PacketSprintIndex,
        PacketNeedleIndex,
        PacketRazorIndex,
        RobustWideIndex,
        HighbandRoomIndex
    ];
}

public static class CepstralDegradationProfiles
{
    public static MimirCepstralDegradationProfile Clean { get; } = new("template-clean", 0.0, 0.0, 0);
    public static MimirCepstralDegradationProfile Blur { get; } = new("template-blur", 0.0, 0.0, 1);
    public static MimirCepstralDegradationProfile WarpLight { get; } = new("template-warp-light", 0.75, 1.25, 0);
    public static MimirCepstralDegradationProfile WarpBlur { get; } = new("template-warp-blur", 0.75, 1.25, 1);
    public static MimirCepstralDegradationProfile WarpHeavy { get; } = new("template-warp-heavy", 1.25, 1.75, 1);
}
