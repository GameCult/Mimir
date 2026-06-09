namespace Mimir.Runtime.Synchronization;

public enum MimirReservoirStorageStrategy
{
    ManagedRollingBuffers,
    NativeSharedEdgeRing,
    GpuResidentTemporalField
}

public sealed record MimirReservoirViewConfiguration(
    string Id,
    string PayloadKind,
    string Owner,
    bool RetainsHistory,
    string Notes);

public sealed record MimirReservoirConfiguration(
    string Id,
    string Description,
    MimirReservoirStorageStrategy Strategy,
    double WindowSeconds,
    IReadOnlyList<MimirReservoirViewConfiguration> Views,
    string ExpiryPolicy,
    string Notes);

public static class MimirReservoirConfigurations
{
    public static IReadOnlyList<MimirReservoirViewConfiguration> StandardViews { get; } =
    [
        new("camera_frame", "video payload/native handle", "Mimir.Runtime/native capture", RetainsHistory: false, "Raw frame evidence inside the rolling window."),
        new("camera_feature", "sparse visual feature", "Fensalir", RetainsHistory: false, "Derived visual claims."),
        new("audio_block", "PCM/native audio block", "Mimir.Runtime/native audio", RetainsHistory: false, "Timing and field evidence."),
        new("phase_claim", "audio phase/group-delay evidence", "Mimir.Runtime", RetainsHistory: false, "Calibration/normalization input."),
        new("event_claim", "timeline anchor", "Mimir.Runtime", RetainsHistory: false, "Bioacoustic/passive/calibration events."),
        new("surface_claim", "spatial surface observation", "Fensalir", RetainsHistory: false, "Fusion candidate."),
        new("render_packet", "Mimir program packet", "Mimir/Fensalir/Faust", RetainsHistory: false, "Output-ready program view.")
    ];

    public static MimirReservoirConfiguration ManagedRuntime { get; } = new(
        "managed-runtime-rolling-buffers",
        "Current app-level five-second rolling buffers.",
        MimirReservoirStorageStrategy.ManagedRollingBuffers,
        WindowSeconds: 5.0,
        StandardViews,
        "accept-inside-window-expire-outside",
        "Best for correctness and UI inspection while native rings mature.");

    public static MimirReservoirConfiguration NativeSharedEdge { get; } = ManagedRuntime with
    {
        Id = "native-shared-edge-ring",
        Description = "Production lower reservoir: native shared-edge retention with typed views.",
        Strategy = MimirReservoirStorageStrategy.NativeSharedEdgeRing,
        Notes = "Native owns retention mechanics; Mimir owns policy and typed meaning."
    };

    public static MimirReservoirConfiguration FensalirTemporalField { get; } = ManagedRuntime with
    {
        Id = "fensalir-temporal-field",
        Description = "Renderer-owned stable evidence field after Mimir lowers observations.",
        Strategy = MimirReservoirStorageStrategy.GpuResidentTemporalField,
        ExpiryPolicy = "fensalir-evidence-lifetime",
        Notes = "This is derived field memory, not raw capture history."
    };

    public static IReadOnlyList<MimirReservoirConfiguration> BuiltIn { get; } =
    [
        ManagedRuntime,
        NativeSharedEdge,
        FensalirTemporalField
    ];
}
