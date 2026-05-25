namespace Mimir.Runtime.Synchronization;

public enum MimirObsVideoPublicationKind
{
    Spout2,
    SharedD3D12Texture,
    EveNativeD3D12Stream,
    SrtDiagnosticBridge,
    WindowCaptureDiagnostic
}

public enum MimirObsAudioPublicationKind
{
    FaustStemBus,
    AsioOutputPair,
    WasapiLoopbackDiagnostic,
    SrtDiagnosticBridge
}

public sealed record MimirObsAudioStem(
    string Id,
    string DisplayName,
    int ChannelCount,
    bool UserMixable,
    bool CarriesTimingWitness,
    string Notes);

public sealed record MimirObsPublicationConfiguration(
    string Id,
    string Description,
    MimirObsVideoPublicationKind VideoKind,
    string VideoSourceName,
    MimirObsAudioPublicationKind AudioKind,
    IReadOnlyList<MimirObsAudioStem> AudioStems,
    double TargetPresentationDelaySeconds,
    bool DiagnosticOnly);

public static class MimirObsPublicationConfigurations
{
    public static IReadOnlyList<MimirObsAudioStem> ProgramStems { get; } =
    [
        new("host_voice", "Host voice", 1, UserMixable: true, CarriesTimingWitness: false, "Primary Starfire dialogue stem."),
        new("co_streamer_voice", "Co-streamer voice", 1, UserMixable: true, CarriesTimingWitness: false, "Raven dialogue after network clock fit."),
        new("ambient", "Ambient room", 2, UserMixable: true, CarriesTimingWitness: false, "Room bed after suppression/separation."),
        new("transients", "Transients", 2, UserMixable: true, CarriesTimingWitness: false, "Keyboard, desk, impacts, and short events."),
        new("local_loopback", "Local loopback", 2, UserMixable: true, CarriesTimingWitness: true, "Starfire program loopback and watermark witness."),
        new("remote_loopback", "Remote loopback", 2, UserMixable: true, CarriesTimingWitness: true, "Raven program loopback and network-delay witness."),
        new("spatial_bed", "Spatial bed", 4, UserMixable: true, CarriesTimingWitness: false, "Encoded volumetric ambience or ambisonic fold.")
    ];

    public static MimirObsPublicationConfiguration NativeProgram { get; } = new(
        "native-program-spout-faust",
        "Production target: Fensalir publishes synchronized video, Faust publishes aligned stems.",
        MimirObsVideoPublicationKind.Spout2,
        "Mimir Point Cloud",
        MimirObsAudioPublicationKind.FaustStemBus,
        ProgramStems,
        TargetPresentationDelaySeconds: 5.0,
        DiagnosticOnly: false);

    public static MimirObsPublicationConfiguration TextureInteropProgram { get; } = NativeProgram with
    {
        Id = "native-program-d3d12-shared-texture",
        VideoKind = MimirObsVideoPublicationKind.SharedD3D12Texture,
        VideoSourceName = "Mimir D3D12 Program Texture"
    };

    public static MimirObsPublicationConfiguration DiagnosticSrtBridge { get; } = new(
        "diagnostic-srt-bridge",
        "OBS-facing fallback for bridge tests only. It does not own synchronized program truth.",
        MimirObsVideoPublicationKind.SrtDiagnosticBridge,
        "Mimir Diagnostic SRT",
        MimirObsAudioPublicationKind.SrtDiagnosticBridge,
        ProgramStems.Where(stem => stem.Id is "host_voice" or "local_loopback").ToArray(),
        TargetPresentationDelaySeconds: 1.5,
        DiagnosticOnly: true);

    public static MimirObsPublicationConfiguration EveNativeProgram { get; } = NativeProgram with
    {
        Id = "eve-native-d3d12-stream",
        Description = "EVE-facing native program output: Fensalir publishes a shared D3D12 texture for hardware encode; EVE receives decoded pixels without WebKit layout authority.",
        VideoKind = MimirObsVideoPublicationKind.EveNativeD3D12Stream,
        VideoSourceName = MimirEveProgramOutputConfigurations.DefaultSharedTextureName,
        DiagnosticOnly = false
    };

    public static IReadOnlyList<MimirObsPublicationConfiguration> BuiltIn { get; } =
    [
        NativeProgram,
        TextureInteropProgram,
        EveNativeProgram,
        DiagnosticSrtBridge
    ];
}
