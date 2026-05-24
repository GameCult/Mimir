namespace Mimir.Runtime.Synchronization;

public enum MimirCameraIngestStrategy
{
    JsonCadenceProbe,
    ManagedNativeWrapper,
    NativeSpscFrameRing,
    SharedGpuTexture
}

public sealed record MimirCameraIngestConfiguration(
    string Id,
    string Description,
    MimirCameraIngestStrategy Strategy,
    string Owner,
    bool CarriesPixels,
    bool HotPathCandidate,
    string[] DeviceProfiles,
    string[] RequiredProofs,
    string Notes);

public static class MimirCameraIngestConfigurations
{
    public static MimirCameraIngestConfiguration CadenceProbe { get; } = new(
        "json-cadence-probe",
        "Metadata-only diagnostic bridge for checking driver cadence.",
        MimirCameraIngestStrategy.JsonCadenceProbe,
        "native probes + Mimir diagnostics",
        CarriesPixels: false,
        HotPathCandidate: false,
        DeviceProfiles: MimirNativeCaptureConfigurations.LocalSixCameraProfiles.Select(profile => profile.Id).ToArray(),
        RequiredProofs: ["source cadence enters rolling buffers"],
        "Keep this diagnostic-only; it is not a pixel transport.");

    public static MimirCameraIngestConfiguration ManagedWrapper { get; } = new(
        "managed-native-wrapper",
        "First real driver integration shape through IMimirVideoCaptureDriver.",
        MimirCameraIngestStrategy.ManagedNativeWrapper,
        "Mimir.Runtime + native worker",
        CarriesPixels: true,
        HotPathCandidate: true,
        DeviceProfiles: ["leap-left-ir", "leap-right-ir"],
        RequiredProofs: ["one-camera sustained cadence", "bounded allocation", "device timestamp preserved"],
        "Good first cut for Leap because observability matters while the ABI is still moving.");

    public static MimirCameraIngestConfiguration NativeSpscRing { get; } = new(
        "native-spsc-frame-ring",
        "Production local-camera ingest: native producer rings with typed payload handles.",
        MimirCameraIngestStrategy.NativeSpscFrameRing,
        "native capture + native/reservoir",
        CarriesPixels: true,
        HotPathCandidate: true,
        DeviceProfiles: MimirNativeCaptureConfigurations.LocalSixCameraProfiles.Select(profile => profile.Id).ToArray(),
        RequiredProofs: ["all six cameras sustained", "no stdout transport", "bounded memory", "consumer lag telemetry"],
        "This is the six-camera throughput answer.");

    public static MimirCameraIngestConfiguration SharedTexture { get; } = NativeSpscRing with
    {
        Id = "shared-gpu-texture",
        Description = "Final copy-avoidance target: native capture provides importable GPU handles for Fensalir.",
        Strategy = MimirCameraIngestStrategy.SharedGpuTexture,
        Owner = "native capture + Fensalir",
        RequiredProofs = ["Fensalir imports frames", "rendered pixels match source cadence", "handle lifetime is explicit"],
        Notes = "Promote only when driver/API friction is paid down; Fensalir owns GPU lifetime."
    };

    public static IReadOnlyList<MimirCameraIngestConfiguration> BuiltIn { get; } =
    [
        CadenceProbe,
        ManagedWrapper,
        NativeSpscRing,
        SharedTexture
    ];
}
