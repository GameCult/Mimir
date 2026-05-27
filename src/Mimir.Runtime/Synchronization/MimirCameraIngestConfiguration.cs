namespace Mimir.Runtime.Synchronization;

public enum MimirCameraIngestStrategy
{
    JsonCadenceProbe,
    ManagedNativeWrapper,
    NativeSpscFrameRing,
    FensalirTextureLease,
    DeviceDirectTextureProducer
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
        "Managed polling seam for bring-up only; useful for proving timing and descriptor shape.",
        MimirCameraIngestStrategy.ManagedNativeWrapper,
        "Mimir.Runtime + native worker",
        CarriesPixels: true,
        HotPathCandidate: true,
        DeviceProfiles: ["leap-left-ir", "leap-right-ir"],
        RequiredProofs: ["one-camera sustained cadence", "bounded allocation", "device timestamp preserved", "copy count reported"],
        "This is a diagnostic bring-up seam, not the destination. Promote only when it is the shortest path to evidence.");

    public static MimirCameraIngestConfiguration NativeSpscRing { get; } = new(
        "native-spsc-frame-ring",
        "Native producer ring for device APIs that must expose system-memory frames.",
        MimirCameraIngestStrategy.NativeSpscFrameRing,
        "native capture + native/reservoir",
        CarriesPixels: true,
        HotPathCandidate: true,
        DeviceProfiles: MimirNativeCaptureConfigurations.LocalSixCameraProfiles.Select(profile => profile.Id).ToArray(),
        RequiredProofs: ["all six cameras sustained", "no stdout transport", "bounded memory", "consumer lag telemetry", "unavoidable-copy ledger"],
        "Use only when the device stack cannot write or decode into the Fensalir texture path directly.");

    public static MimirCameraIngestConfiguration FensalirTextureLease { get; } = NativeSpscRing with
    {
        Id = "fensalir-texture-lease",
        Description = "Preferred render-input path: device/decode producer writes into a Fensalir-owned Texture2D lease.",
        Strategy = MimirCameraIngestStrategy.FensalirTextureLease,
        Owner = "native capture/decode producer + Fensalir",
        RequiredProofs = ["producer receives Fensalir lease", "producer signals fence", "Fensalir waits before sampling", "copy count is zero or justified"],
        Notes = "This is the hot path for decoded camera images. Imported foreign handles are fallback edges."
    };

    public static MimirCameraIngestConfiguration DeviceDirectTextureProducer { get; } = FensalirTextureLease with
    {
        Id = "device-direct-texture-producer",
        Description = "Closest-to-zero-copy target: speak to the device stack directly and populate the Fensalir lease without middlemen.",
        Strategy = MimirCameraIngestStrategy.DeviceDirectTextureProducer,
        Owner = "device-specific native producer + Fensalir",
        RequiredProofs = ["device API path identified", "no process bridge", "no convenience transcode", "GPU/system copy count measured", "cadence survives full camera set"],
        Notes = "Backend choice is device-specific: KS/WinUSB raw sources may still land in system memory; GPU-decodable compressed sources should avoid CPU decode."
    };

    public static IReadOnlyList<MimirCameraIngestConfiguration> BuiltIn { get; } =
    [
        CadenceProbe,
        ManagedWrapper,
        NativeSpscRing,
        FensalirTextureLease,
        DeviceDirectTextureProducer
    ];
}
