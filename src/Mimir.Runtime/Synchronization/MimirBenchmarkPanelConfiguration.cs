namespace Mimir.Runtime.Synchronization;

public enum MimirBenchmarkDegradationKind
{
    Clean,
    CepstralWarp,
    CepstralBlur,
    WarpAndBlur,
    BandLimited,
    PacketJitter,
    NetworkDelay,
    CpuBudgetPressure
}

public sealed record MimirBenchmarkDegradation(
    string Id,
    MimirBenchmarkDegradationKind Kind,
    double Strength,
    string Notes);

public sealed record MimirBenchmarkPanelConfiguration(
    string Id,
    string Description,
    string[] DecoderProfiles,
    IReadOnlyList<MimirBenchmarkDegradation> Degradations,
    double MinimumRealtimeFactor,
    double MinimumIdentityScore,
    double MinimumTimingConfidence,
    string ReceiptRoot);

public static class MimirBenchmarkPanelConfigurations
{
    public static MimirBenchmarkPanelConfiguration BioacousticGolf { get; } = new(
        "bioacoustic-golf",
        "Performance golf panel for singing/hearing organ tuning.",
        MimirBioacousticDecoderConfiguration.BuiltInProfiles.Select(profile => profile.Id).ToArray(),
        [
            new("clean", MimirBenchmarkDegradationKind.Clean, 0.0, "Round trip without intentional damage."),
            new("warp-light", MimirBenchmarkDegradationKind.CepstralWarp, 0.75, "Simplex-like domain warp target from the harness."),
            new("blur-light", MimirBenchmarkDegradationKind.CepstralBlur, 1.0, "Separable five-tap Gaussian blur target."),
            new("warp-blur", MimirBenchmarkDegradationKind.WarpAndBlur, 1.0, "Combined lossy-room stress."),
            new("bandlimited-phone", MimirBenchmarkDegradationKind.BandLimited, 0.65, "Consumer mic / codec survival proxy."),
            new("network-jitter", MimirBenchmarkDegradationKind.PacketJitter, 0.40, "Remote witness observation jitter proxy.")
        ],
        MinimumRealtimeFactor: 50.0,
        MinimumIdentityScore: 0.85,
        MinimumTimingConfidence: 0.80,
        "artifacts/bioacoustic-training");

    public static MimirBenchmarkPanelConfiguration MeatspaceAcceptance { get; } = BioacousticGolf with
    {
        Id = "meatspace-acceptance",
        Description = "Physical Scarlett/monitor/mic acceptance panel.",
        MinimumRealtimeFactor = 10.0,
        MinimumIdentityScore = 0.65,
        MinimumTimingConfidence = 0.70,
        ReceiptRoot = "artifacts/meatspace-sync"
    };

    public static IReadOnlyList<MimirBenchmarkPanelConfiguration> BuiltIn { get; } =
    [
        BioacousticGolf,
        MeatspaceAcceptance
    ];
}
