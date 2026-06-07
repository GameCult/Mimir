namespace Mimir.Runtime.Synchronization;

public enum MimirNetworkPayloadKind
{
    TypedTimingState,
    RawAudioDebugWindow,
    CompressedAudioProgramFeed,
    CompressedVideoProgramFeed,
    RawVideoDiagnosticWindow,
    NativeDashboardState,
    MediaStreamFrame,
    MediaBodyShard
}

public enum MimirNetworkTransportKind
{
    CultMeshTypedState,
    CultMeshReliableUdpMedia,
    CultMeshReliableUdpBody,
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

    public static MimirNetworkTransportConfiguration CultMeshStreamFrameMedia { get; } = new(
        "cultmesh-reliable-udp-stream-frame",
        "Primary Verse media lane: typed per-frame envelopes over CultMesh reliable UDP with capture time, source identity, resource/body refs, and backpressure.",
        MimirNetworkTransportKind.CultMeshReliableUdpMedia,
        MimirNetworkPayloadKind.MediaStreamFrame,
        MayAffectClock: false,
        CarriesRawMedia: true,
        TargetLatencyMilliseconds: 35.0,
        RequiredDocuments: ["mimir.cultmesh_stream_frame.v1"],
        RejectionConditions: ["used-as-clock-authority", "missing-source-identity", "missing-backpressure", "schema-version-mismatch"],
        "Media is welcome in the Verse, but timing authority still comes from decoded evidence and clock-domain state.");

    public static MimirNetworkTransportConfiguration CultMeshBodyShardLane { get; } = new(
        "cultmesh-reliable-udp-body-shard",
        "Primary Verse body lane: bounded CultCache media/page shards over CultMesh reliable UDP with hashes, cursors, and receiver backpressure.",
        MimirNetworkTransportKind.CultMeshReliableUdpBody,
        MimirNetworkPayloadKind.MediaBodyShard,
        MayAffectClock: false,
        CarriesRawMedia: true,
        TargetLatencyMilliseconds: 60.0,
        RequiredDocuments: ["mimir.media_body_shard.v1", "mimir.recorder_body_ref.v1"],
        RejectionConditions: ["missing-hash", "missing-cursor", "missing-backpressure", "unbounded-body"],
        "Large bodies move through shards and refs, not inline base64 and not private transport sidecars.");

    public static MimirNetworkTransportConfiguration SrtProgramBridge { get; } = new(
        "srt-program-bridge",
        "Existing LAN media bridge for OBS diagnostics and legacy remote program feeds.",
        MimirNetworkTransportKind.SrtDiagnosticMedia,
        MimirNetworkPayloadKind.CompressedVideoProgramFeed,
        MayAffectClock: false,
        CarriesRawMedia: true,
        TargetLatencyMilliseconds: 1500.0,
        RequiredDocuments: [],
        RejectionConditions: ["used-as-clock-authority", "used-for-local-hot-path", "used-as-verse-media"],
        "Useful OBS bridge. Not the Verse media transport and not the Perfect Machine's synchronization core.");

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

    public static MimirNetworkTransportConfiguration EveDashboardState { get; } = new(
        "eve-dashboard-state",
        "Native retained dashboard trees and provider manifests for Eve-rendered control surfaces.",
        MimirNetworkTransportKind.CultMeshTypedState,
        MimirNetworkPayloadKind.NativeDashboardState,
        MayAffectClock: false,
        CarriesRawMedia: false,
        TargetLatencyMilliseconds: 50.0,
        RequiredDocuments: ["mimir.eve_dashboard_manifest", "mimir.eve_dashboard_state"],
        RejectionConditions: ["unknown-provider", "untrusted-provider", "schema-version-mismatch", "command-owner-conflict"],
        "Dashboard providers own state and commands. Eve owns native rendering and touch. No remote UI code runs on Eve.");

    public static IReadOnlyList<MimirNetworkTransportConfiguration> BuiltIn { get; } =
    [
        CultMeshTimingState,
        CultMeshStreamFrameMedia,
        CultMeshBodyShardLane,
        RawAudioDebugWindow,
        SrtProgramBridge,
        WebRtcExperiment,
        EveDashboardState
    ];
}
