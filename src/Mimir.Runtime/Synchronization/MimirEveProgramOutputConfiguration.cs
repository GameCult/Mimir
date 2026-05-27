namespace Mimir.Runtime.Synchronization;

public enum MimirEveProgramOutputTransport
{
    D3D12SharedTextureHardwareEncode,
    SunshineMoonlightProbe,
    DesktopDuplicationDiagnostic
}

public sealed record MimirEveProgramOutputConfiguration(
    string Id,
    string Description,
    string TargetHost,
    int TargetPort,
    string SharedTextureName,
    int TargetWidth,
    int TargetHeight,
    int TargetFramesPerSecond,
    MimirEveProgramOutputTransport Transport,
    bool UsesWebKit,
    bool DiagnosticOnly,
    string[] RequiredEnvironment,
    string Notes);

public static class MimirEveProgramOutputConfigurations
{
    public const string DefaultSharedTextureName = "Global\\MimirFensalirProgramTexture";
    public const string DefaultSharedFenceName = "Global\\MimirFensalirProgramFence";

    public static MimirEveProgramOutputConfiguration NativeSharedTexture { get; } = new(
        "eve-native-d3d12-hardware-stream",
        "Production direction: Fensalir copies the completed D3D12 backbuffer into a named shared texture; a hardware encoder consumes that texture and EVE displays decoded frames natively.",
        "eve",
        44044,
        DefaultSharedTextureName,
        1920,
        1440,
        60,
        MimirEveProgramOutputTransport.D3D12SharedTextureHardwareEncode,
        UsesWebKit: false,
        DiagnosticOnly: false,
        RequiredEnvironment:
        [
            "FENSALIR_PROGRAM_OUTPUT_D3D12=1",
            $"FENSALIR_PROGRAM_OUTPUT_NAME={DefaultSharedTextureName}",
            $"FENSALIR_PROGRAM_OUTPUT_FENCE_NAME={DefaultSharedFenceName}"
        ],
        "EVE owns decode/composite only. It does not own layout, DOM, timing authority, or Chromium compatibility.");

    public static MimirEveProgramOutputConfiguration MoonlightProbe { get; } = NativeSharedTexture with
    {
        Id = "eve-moonlight-probe",
        Description = "Latency probe only: Sunshine/Moonlight can validate network and decode budget before the custom encoder path is finished.",
        Transport = MimirEveProgramOutputTransport.SunshineMoonlightProbe,
        SharedTextureName = "",
        DiagnosticOnly = true,
        RequiredEnvironment = [],
        Notes = "This proves the EVE display/network budget, not Mimir's shared-texture authority."
    };

    public static IReadOnlyList<MimirEveProgramOutputConfiguration> BuiltIn { get; } =
    [
        NativeSharedTexture,
        MoonlightProbe
    ];
}
