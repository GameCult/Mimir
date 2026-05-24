namespace Mimir.Runtime.Synchronization;

public enum MimirNativeCaptureTransport
{
    VendorSdk,
    LibUsbIsochronous,
    UvcDirect,
    AsioCallback,
    NetworkCultMesh,
    NetworkCompressedAv
}

public enum MimirCaptureClockDomain
{
    DeviceHardware,
    InterfaceAsio,
    HostQpc,
    NetworkSender,
    CanonicalFitted
}

public sealed record MimirNativeCaptureDeviceProfile(
    string Id,
    string DisplayName,
    MimirStreamKind StreamKind,
    MimirNativeCaptureTransport Transport,
    MimirCaptureClockDomain ClockDomain,
    int ChannelCount,
    int PreferredWidth,
    int PreferredHeight,
    int PreferredSampleRate,
    double PreferredFramesPerSecond,
    double RollingBufferSeconds,
    bool Required,
    string[] OwnedOutputs,
    string Notes);

public static class MimirNativeCaptureConfigurations
{
    public const double DefaultRollingBufferSeconds = 5.0;

    public static MimirNativeCaptureDeviceProfile LeapLeftIr { get; } = new(
        "leap-left-ir",
        "Ultraleap stereo IR left",
        MimirStreamKind.Video,
        MimirNativeCaptureTransport.VendorSdk,
        MimirCaptureClockDomain.DeviceHardware,
        ChannelCount: 1,
        PreferredWidth: 640,
        PreferredHeight: 480,
        PreferredSampleRate: 0,
        PreferredFramesPerSecond: 120.0,
        DefaultRollingBufferSeconds,
        Required: true,
        OwnedOutputs: ["near-field-feature-claims", "timing-camera-claims"],
        "Ground-truth near-field timing camera. Keep full frame; tracking eats pixels.");

    public static MimirNativeCaptureDeviceProfile LeapRightIr { get; } = LeapLeftIr with
    {
        Id = "leap-right-ir",
        DisplayName = "Ultraleap stereo IR right"
    };

    public static MimirNativeCaptureDeviceProfile KiyoProRgb { get; } = new(
        "kiyo-pro-rgb",
        "Razer Kiyo Pro RGB ground truth",
        MimirStreamKind.Video,
        MimirNativeCaptureTransport.UvcDirect,
        MimirCaptureClockDomain.DeviceHardware,
        ChannelCount: 1,
        PreferredWidth: 1920,
        PreferredHeight: 1080,
        PreferredSampleRate: 0,
        PreferredFramesPerSecond: 60.0,
        DefaultRollingBufferSeconds,
        Required: true,
        OwnedOutputs: ["rgb-ground-truth", "skin/material-observations"],
        "RGB quality anchor. Prefer direct UVC control; do not put Media Foundation in the hot loop.");

    public static MimirNativeCaptureDeviceProfile KiyoBasicRgb { get; } = KiyoProRgb with
    {
        Id = "kiyo-basic-rgb",
        DisplayName = "Razer Kiyo RGB stereo/context",
        OwnedOutputs = ["rgb-context", "stereo-rgb-claims"]
    };

    public static MimirNativeCaptureDeviceProfile Ps3EyeLeft { get; } = new(
        "ps3eye-left",
        "PlayStation Eye high-rate tracker left",
        MimirStreamKind.Video,
        MimirNativeCaptureTransport.LibUsbIsochronous,
        MimirCaptureClockDomain.DeviceHardware,
        ChannelCount: 1,
        PreferredWidth: 640,
        PreferredHeight: 480,
        PreferredSampleRate: 0,
        PreferredFramesPerSecond: 60.0,
        DefaultRollingBufferSeconds,
        Required: true,
        OwnedOutputs: ["wide-feature-tracks", "motion-field-claims"],
        "Tracking witness. Prefer frame rate and low latency over prettiness.");

    public static MimirNativeCaptureDeviceProfile Ps3EyeRight { get; } = Ps3EyeLeft with
    {
        Id = "ps3eye-right",
        DisplayName = "PlayStation Eye high-rate tracker right"
    };

    public static MimirNativeCaptureDeviceProfile ScarlettStarfire { get; } = new(
        "starfire-scarlett-4th-gen",
        "Starfire Focusrite Scarlett ASIO",
        MimirStreamKind.Audio,
        MimirNativeCaptureTransport.AsioCallback,
        MimirCaptureClockDomain.InterfaceAsio,
        ChannelCount: 4,
        PreferredWidth: 0,
        PreferredHeight: 0,
        PreferredSampleRate: 192_000,
        PreferredFramesPerSecond: 0.0,
        DefaultRollingBufferSeconds,
        Required: true,
        OwnedOutputs: ["host-dialogue", "loopback-clock", "room-response"],
        "Heavy local audio authority: mic inputs plus loopback in one ASIO clock domain.");

    public static MimirNativeCaptureDeviceProfile ScarlettRaven { get; } = ScarlettStarfire with
    {
        Id = "raven-scarlett",
        DisplayName = "Raven Focusrite Scarlett ASIO",
        Transport = MimirNativeCaptureTransport.NetworkCultMesh,
        ClockDomain = MimirCaptureClockDomain.NetworkSender,
        OwnedOutputs = ["remote-dialogue", "remote-loopback-clock", "network-delay-evidence"],
        Notes = "Remote co-streamer audio witness. It decodes locally and ships typed timing/path state back over CultMesh."
    };

    public static IReadOnlyList<MimirNativeCaptureDeviceProfile> LocalSixCameraProfiles { get; } =
    [
        LeapLeftIr,
        LeapRightIr,
        KiyoProRgb,
        KiyoBasicRgb,
        Ps3EyeLeft,
        Ps3EyeRight
    ];

    public static IReadOnlyList<MimirNativeCaptureDeviceProfile> BuiltIn { get; } =
    [
        .. LocalSixCameraProfiles,
        ScarlettStarfire,
        ScarlettRaven
    ];
}
