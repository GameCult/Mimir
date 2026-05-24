namespace Mimir.Runtime.Synchronization;

public enum MimirBioacousticVocabularyShape
{
    DirectWordIndex,
    SpeakerSplitWordIndex,
    FormantBirdcall,
    RoomLearnedMotifBank,
    DeviceSpecificWitness
}

public enum MimirBioacousticEmissionDuty
{
    SilentPassive,
    LowGainContinuousWatermark,
    ConfidenceGatedHybrid,
    CalibrationSweep,
    BenchStress
}

public sealed record MimirBioacousticLanguageConfiguration(
    string Id,
    string Description,
    MimirBioacousticVocabularyShape VocabularyShape,
    MimirBioacousticEmissionDuty EmissionDuty,
    int WordCount,
    int SpeakerCount,
    int SyllablesPerWord,
    double SegmentSeconds,
    double MinFrequencyHz,
    double MaxFrequencyHz,
    double TargetRms,
    double MaxPeak,
    bool SpeakerSpecificWords,
    bool SupportsStandaloneReceiver,
    string[] DecoderProfiles,
    string Notes);

public static class MimirBioacousticLanguageConfigurations
{
    public static MimirBioacousticLanguageConfiguration RuntimeBirdcallWatermark { get; } = new(
        "runtime-birdcall-watermark",
        "Current active timing witness: low-gain, self-identifying, speaker-split birdcall words.",
        MimirBioacousticVocabularyShape.FormantBirdcall,
        MimirBioacousticEmissionDuty.LowGainContinuousWatermark,
        MimirBioacousticTimeline.WordCount,
        MimirBioacousticTimeline.SpeakerCount,
        SyllablesPerWord: 4,
        MimirBioacousticTimeline.SegmentSeconds,
        MinFrequencyHz: 180.0,
        MaxFrequencyHz: 15_000.0,
        TargetRms: 0.010,
        MaxPeak: 0.060,
        SpeakerSpecificWords: true,
        SupportsStandaloneReceiver: true,
        DecoderProfiles: MimirBioacousticDecoderConfiguration.BuiltInProfiles.Select(profile => profile.Id).ToArray(),
        "The receiver must recover source time from codebook and schedule alone.");

    public static MimirBioacousticLanguageConfiguration PassiveOnly { get; } = RuntimeBirdcallWatermark with
    {
        Id = "passive-only",
        Description = "No active emission; program audio is the only timing witness until confidence falls.",
        EmissionDuty = MimirBioacousticEmissionDuty.SilentPassive,
        TargetRms = 0.0,
        MaxPeak = 0.0,
        DecoderProfiles = [],
        Notes = "Useful for checking that the watermark is not hiding passive timing regressions."
    };

    public static MimirBioacousticLanguageConfiguration HybridFallback { get; } = RuntimeBirdcallWatermark with
    {
        Id = "hybrid-fallback",
        Description = "Passive by default, then low-gain birdcall emission when confidence is weak.",
        EmissionDuty = MimirBioacousticEmissionDuty.ConfidenceGatedHybrid,
        TargetRms = 0.006,
        MaxPeak = 0.040,
        Notes = "This is the live comfort target: keep the room quiet unless the timing kernel needs active evidence."
    };

    public static MimirBioacousticLanguageConfiguration CalibrationLoud { get; } = RuntimeBirdcallWatermark with
    {
        Id = "calibration-loud",
        Description = "Known louder emission for per-output/mic response, confusion, delay, and group-delay learning.",
        EmissionDuty = MimirBioacousticEmissionDuty.CalibrationSweep,
        TargetRms = 0.040,
        MaxPeak = 0.180,
        Notes = "Not a stream mode. This exists to measure meatspace paths without guessing."
    };

    public static MimirBioacousticLanguageConfiguration PhoneWitness { get; } = RuntimeBirdcallWatermark with
    {
        Id = "phone-witness",
        Description = "Reduced-band word set for consumer phone microphones and lossy room/network paths.",
        VocabularyShape = MimirBioacousticVocabularyShape.DeviceSpecificWitness,
        MinFrequencyHz = 600.0,
        MaxFrequencyHz = 9_500.0,
        TargetRms = 0.012,
        MaxPeak = 0.070,
        DecoderProfiles = [MimirBioacousticDecoderConfiguration.CompactFastIndex.Id, MimirBioacousticDecoderConfiguration.RobustWideIndex.Id],
        Notes = "A phone should self-locate on the canonical timeline with only the schedule/codebook state."
    };

    public static IReadOnlyList<MimirBioacousticLanguageConfiguration> BuiltIn { get; } =
    [
        RuntimeBirdcallWatermark,
        PassiveOnly,
        HybridFallback,
        CalibrationLoud,
        PhoneWitness
    ];
}
