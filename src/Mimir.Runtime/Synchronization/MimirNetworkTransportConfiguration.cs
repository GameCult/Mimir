namespace Mimir.Runtime.Synchronization;

public enum MimirNetworkPayloadKind
{
    TypedTimingState,
    RawAudioDebugWindow,
    CompressedAudioProgramFeed,
    CompressedVideoProgramFeed,
    RawVideoDiagnosticWindow
}

public enum MimirNetworkTransportKind
{
    CultMeshTypedState,
    LanUdpDatagrams,
    SrtDiagnosticMedia,
    WebRtcExperimental,
    LocalPipeDiagnostic
}

public sealed record MimirNetworkTransportConfiguration(
    string Id,
    string Description,
    MimirNetworkTransportKind TransportKind,
    MimirNetworkPayloadKind PayloadKind,
    bool MayAffectClock,
    bool CarriesRawMedia,
    double TargetLatencyMilliseconds,
    string[] RequiredDocuments,
    string[] RejectionConditions,
    string Notes);

public static class MimirNetworkTransportConfigurations
{
    public static MimirNetworkTransportConfiguration CultMeshTimingState { get; } = new(
        "cultmesh-timing-state",
        "Primary remote-witness path: compact decoded anchors, clock fits, response profiles, and health.",
        MimirNetworkTransportKind.CultMeshTypedState,
        MimirNetworkPayloadKind.TypedTimingState,
        MayAffectClock: true,
        CarriesRawMedia: false,
        TargetLatencyMilliseconds: 20.0,
        RequiredDocuments: ["mimir.bioacoustic_codebook_state", "mimir.bioacoustic_decoder_state", "mimir.acoustic_path_state"],
        RejectionConditions: ["unknown-node", "wrong-codebook", "low-confidence", "clock-authority-conflict"],
        "Network timestamps are metadata. Decoded anchors are timing evidence.");

    public static MimirNetworkTransportConfiguration RawAudioDebugWindow { get; } = new(
        "raw-audio-debug-window",
        "Short raw PCM window for debugging remote witness failures.",
        MimirNetworkTransportKind.LanUdpDatagrams,
        MimirNetworkPayloadKind.RawAudioDebugWindow,
        MayAffectClock: false,
        CarriesRawMedia: true,
        TargetLatencyMilliseconds: 50.0,
        RequiredDocuments: ["mimir.acoustic_path_state"],
        RejectionConditions: ["not-requested", "outside-debug-session", "oversized-window"],
        "Raw audio can explain failures; it should not become the normal timing authority.");

    public static MimirNetworkTransportConfiguration SrtProgramBridge { get; } = new(
        "srt-program-bridge",
        "Existing LAN media bridge for OBS diagnostics and remote program feeds.",
        MimirNetworkTransportKind.SrtDiagnosticMedia,
        MimirNetworkPayloadKind.CompressedVideoProgramFeed,
        MayAffectClock: false,
        CarriesRawMedia: true,
        TargetLatencyMilliseconds: 1500.0,
        RequiredDocuments: [],
        RejectionConditions: ["used-as-clock-authority", "used-for-local-hot-path"],
        "Useful bridge. Not the Perfect Machine's synchronization core.");

    public static MimirNetworkTransportConfiguration WebRtcExperiment { get; } = CultMeshTimingState with
    {
        Id = "webrtc-experimental-media",
        Description = "Experimental phone/browser media path; typed timing state still carries authority.",
        TransportKind = MimirNetworkTransportKind.WebRtcExperimental,
        PayloadKind = MimirNetworkPayloadKind.CompressedAudioProgramFeed,
        CarriesRawMedia = true,
        TargetLatencyMilliseconds = 80.0,
        RejectionConditions = ["used-without-decoded-anchors", "browser-audio-processing-unknown"],
        Notes = "Do not trust browser media timestamps without decoded witness state."
    };

    public static IReadOnlyList<MimirNetworkTransportConfiguration> BuiltIn { get; } =
    [
        CultMeshTimingState,
        RawAudioDebugWindow,
        SrtProgramBridge,
        WebRtcExperiment
    ];
}
