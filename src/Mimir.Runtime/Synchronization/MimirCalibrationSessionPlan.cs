namespace Mimir.Runtime.Synchronization;

public enum MimirCalibrationStimulusKind
{
    BioacousticMotifSong,
    ChirpBinReference,
    PassiveProgramAudio,
    SilenceFloor,
    ImpulseResponse
}

public sealed record MimirCalibrationStimulus(
    string Id,
    MimirCalibrationStimulusKind Kind,
    double DurationSeconds,
    double Gain,
    string Purpose);

public sealed record MimirCalibrationSessionPlan(
    string Id,
    string Description,
    IReadOnlyList<MimirCalibrationStimulus> Stimuli,
    IReadOnlyList<MimirBioacousticDecoderConfiguration> DecoderPanel,
    IReadOnlyList<MimirAudioFieldModel> FieldModels,
    string[] RequiredArtifacts)
{
    public double TotalDurationSeconds => Stimuli.Sum(stimulus => stimulus.DurationSeconds);
}

public static class MimirCalibrationSessionPlans
{
    public static MimirCalibrationSessionPlan QuickSynthetic { get; } = new(
        "quick-synthetic-bioacoustic",
        "Short synthetic panel for CI/smoke: proves identity, clock fit, and actuator handoff without devices.",
        [
            new("bioacoustic-short", MimirCalibrationStimulusKind.BioacousticMotifSong, 0.75, 1.0, "Decode word identity and global clock under synthetic degradation."),
            new("actuator-delay-proof", MimirCalibrationStimulusKind.BioacousticMotifSong, 2.0, 1.0, "Measure delay, apply correction, and remeasure residual.")
        ],
        [
            MimirBioacousticDecoderConfiguration.BaselineMfccIndex,
            MimirBioacousticDecoderConfiguration.CompactFastIndex,
            MimirBioacousticDecoderConfiguration.HighbandRoomIndex
        ],
        [MimirAudioFieldModel.AlignedStems],
        ["bioacoustic-training.cc", "run-summary.json", "source-pre-warp.wav"]);

    public static MimirCalibrationSessionPlan ScarlettLoopback { get; } = new(
        "scarlett-loopback-authority",
        "Electrical loopback proof for emitted timeline and interface clock behavior.",
        [
            new("silence-floor", MimirCalibrationStimulusKind.SilenceFloor, 2.0, 0.0, "Measure noise floor and stale loopback behavior."),
            new("bioacoustic-loopback", MimirCalibrationStimulusKind.BioacousticMotifSong, 8.0, 0.06, "Recover canonical anchors through Scarlett loopback."),
            new("chirp-bin-reference", MimirCalibrationStimulusKind.ChirpBinReference, 6.0, 0.04, "Preserve chirp-bin response/confusion reference evidence.")
        ],
        [
            MimirBioacousticDecoderConfiguration.BaselineMfccIndex,
            MimirBioacousticDecoderConfiguration.CompactFastIndex
        ],
        [MimirAudioFieldModel.AlignedStems],
        ["asio-capture.f32", "bioacoustic-training.cc", "path-state.cc"]);

    public static MimirCalibrationSessionPlan MeatspaceRoom { get; } = new(
        "meatspace-room-paths",
        "Physical monitor/mic calibration: learn usable bands, path delay, group-delay pressure, and anchor density.",
        [
            new("silence-floor", MimirCalibrationStimulusKind.SilenceFloor, 3.0, 0.0, "Measure room and device noise before active evidence."),
            new("bioacoustic-room-low", MimirCalibrationStimulusKind.BioacousticMotifSong, 12.0, 0.03, "Low-gain continuous motif language for unobtrusive decode."),
            new("bioacoustic-room-raised", MimirCalibrationStimulusKind.BioacousticMotifSong, 8.0, 0.08, "Raised-gain pass for weak microphone paths."),
            new("chirp-bin-reference", MimirCalibrationStimulusKind.ChirpBinReference, 8.0, 0.05, "Reference response matrix and group-delay pressure."),
            new("passive-music", MimirCalibrationStimulusKind.PassiveProgramAudio, 20.0, 1.0, "Compare passive program-audio drift evidence against active anchors.")
        ],
        MimirBioacousticDecoderConfiguration.BuiltInProfiles,
        [
            MimirAudioFieldModel.AlignedStems,
            MimirAudioFieldModel.SourceBasedSpatialBus,
            MimirAudioFieldModel.HybridEvidenceField
        ],
        ["room-manifest.json", "bioacoustic-training.cc", "path-state.cc", "actuator-state.cc"]);

    public static IReadOnlyList<MimirCalibrationSessionPlan> BuiltIn { get; } =
    [
        QuickSynthetic,
        ScarlettLoopback,
        MeatspaceRoom
    ];
}
