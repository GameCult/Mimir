namespace Mimir.Runtime.Synchronization;

public enum MimirProgramVideoPublicationKind
{
    FensalirProgramTexture,
    SharedD3D12Texture,
    CultMeshMediaBody,
    SrtDiagnosticBridge,
    ObsCompatibilityAdapter
}

public enum MimirProgramAudioPublicationKind
{
    FaustStemBus,
    AsioOutputPair,
    CultMeshMediaBody,
    WasapiLoopbackDiagnostic,
    ObsCompatibilityAdapter
}

public enum MimirProgramControlPublicationKind
{
    EveGui,
    EveTui,
    CultMeshCommandSurface
}

public sealed record MimirProgramAudioStem(
    string Id,
    string DisplayName,
    int ChannelCount,
    bool UserMixable,
    bool CarriesTimingWitness,
    string Notes);

public sealed record MimirProgramControlSurface(
    string Id,
    string Description,
    MimirProgramControlPublicationKind Kind,
    bool CanPreviewProgram,
    bool CanEditComposition,
    bool CanSelectSources,
    bool CanShowStats);

public sealed record MimirProgramPublicationConfiguration(
    string Id,
    string Description,
    MimirProgramVideoPublicationKind VideoKind,
    string VideoSurfaceName,
    MimirProgramAudioPublicationKind AudioKind,
    IReadOnlyList<MimirProgramAudioStem> AudioStems,
    IReadOnlyList<MimirProgramControlSurface> ControlSurfaces,
    bool PublishesSiteFeed,
    string SitePublisherRoute,
    double TargetPresentationDelaySeconds,
    bool DiagnosticOnly);

public static class MimirProgramPublicationConfigurations
{
    public static IReadOnlyList<MimirProgramAudioStem> ProgramStems { get; } =
    [
        new("host_voice", "Host voice", 1, UserMixable: true, CarriesTimingWitness: false, "Primary Starfire dialogue stem."),
        new("co_streamer_voice", "Co-streamer voice", 1, UserMixable: true, CarriesTimingWitness: false, "Raven dialogue after network clock fit."),
        new("ambient", "Ambient room", 2, UserMixable: true, CarriesTimingWitness: false, "Room bed after suppression/separation."),
        new("transients", "Transients", 2, UserMixable: true, CarriesTimingWitness: false, "Keyboard, desk, impacts, and short events."),
        new("local_loopback", "Local loopback", 2, UserMixable: true, CarriesTimingWitness: true, "Starfire program loopback and watermark witness."),
        new("remote_loopback", "Remote loopback", 2, UserMixable: true, CarriesTimingWitness: true, "Raven program loopback and network-delay witness."),
        new("spatial_bed", "Spatial bed", 4, UserMixable: true, CarriesTimingWitness: false, "Encoded volumetric ambience or ambisonic fold.")
    ];

    public static IReadOnlyList<MimirProgramControlSurface> OperatorSurfaces { get; } =
    [
        new(
            "eve-gui-compositor",
            "Primary Eve GUI composition surface: preview, source selection, transforms, crop/key controls, audio stem controls, and stream health.",
            MimirProgramControlPublicationKind.EveGui,
            CanPreviewProgram: true,
            CanEditComposition: true,
            CanSelectSources: true,
            CanShowStats: true),
        new(
            "eve-tui-operator",
            "Compact Eve TUI operator surface for source health, live stats, scene selection, and emergency command paths.",
            MimirProgramControlPublicationKind.EveTui,
            CanPreviewProgram: false,
            CanEditComposition: false,
            CanSelectSources: true,
            CanShowStats: true),
        new(
            "cultmesh-command-surface",
            "Typed CultMesh command surface consumed by Eve lowerers and trusted devices; Mimir remains the scene authority.",
            MimirProgramControlPublicationKind.CultMeshCommandSurface,
            CanPreviewProgram: false,
            CanEditComposition: true,
            CanSelectSources: true,
            CanShowStats: true)
    ];

    public static MimirProgramPublicationConfiguration NativeProgram { get; } = new(
        "mimir-native-program",
        "Production target: Mimir owns composition, Fensalir publishes synchronized program video, Faust publishes aligned stems, and Eve exposes control/preview/stat surfaces.",
        MimirProgramVideoPublicationKind.FensalirProgramTexture,
        "Mimir Program Texture",
        MimirProgramAudioPublicationKind.FaustStemBus,
        ProgramStems,
        OperatorSurfaces,
        PublishesSiteFeed: false,
        SitePublisherRoute: "",
        TargetPresentationDelaySeconds: 5.0,
        DiagnosticOnly: false);

    public static MimirProgramPublicationConfiguration YggdrasilSiteProgram { get; } = NativeProgram with
    {
        Id = "mimir-yggdrasil-site-program",
        Description = "Site publishing target: a Yggdrasil-facing daemon consumes the same Mimir program output and publishes it to the public site without owning a second composition.",
        VideoKind = MimirProgramVideoPublicationKind.CultMeshMediaBody,
        AudioKind = MimirProgramAudioPublicationKind.CultMeshMediaBody,
        PublishesSiteFeed = true,
        SitePublisherRoute = "cultmesh://yggdrasil/gamecult.site/mimir/program"
    };

    public static MimirProgramPublicationConfiguration TextureInteropProgram { get; } = NativeProgram with
    {
        Id = "native-program-d3d12-shared-texture",
        VideoKind = MimirProgramVideoPublicationKind.SharedD3D12Texture,
        VideoSurfaceName = "Mimir D3D12 Program Texture"
    };

    public static MimirProgramPublicationConfiguration ObsCompatibilityAdapter { get; } = new(
        "obs-compatibility-adapter",
        "Temporary OBS compatibility sink for preview/testing while Mimir owns composition and Yggdrasil/site publication matures.",
        MimirProgramVideoPublicationKind.ObsCompatibilityAdapter,
        "Mimir OBS Compatibility Feed",
        MimirProgramAudioPublicationKind.ObsCompatibilityAdapter,
        ProgramStems.Where(stem => stem.Id is "host_voice" or "local_loopback").ToArray(),
        OperatorSurfaces,
        PublishesSiteFeed: false,
        SitePublisherRoute: "",
        TargetPresentationDelaySeconds: 1.5,
        DiagnosticOnly: true);

    public static MimirProgramPublicationConfiguration DiagnosticSrtBridge { get; } = ObsCompatibilityAdapter with
    {
        Id = "diagnostic-srt-bridge",
        Description = "Legacy SRT bridge for bridge tests only. It is not scene, composition, or broadcast authority.",
        VideoKind = MimirProgramVideoPublicationKind.SrtDiagnosticBridge,
        AudioKind = MimirProgramAudioPublicationKind.WasapiLoopbackDiagnostic,
        VideoSurfaceName = "Mimir Diagnostic SRT"
    };

    public static IReadOnlyList<MimirProgramPublicationConfiguration> BuiltIn { get; } =
    [
        NativeProgram,
        YggdrasilSiteProgram,
        TextureInteropProgram,
        ObsCompatibilityAdapter,
        DiagnosticSrtBridge
    ];
}
