namespace Mimir.Runtime.Synchronization;

public enum MimirStereoDepthAlgorithm
{
    SemiGlobalMatching,
    LearnedStereoReference,
    MonocularReference
}

public enum MimirStereoDepthOutputKind
{
    DisparitySurfacePage,
    MetricDepthSurfacePage,
    ConfidenceTexture
}

public sealed record MimirStereoDepthKernelProfile(
    string Id,
    string Description,
    MimirStereoDepthAlgorithm Algorithm,
    string Owner,
    string Provenance,
    string License,
    bool LiveDependency,
    int DisparityLevels,
    int AggregationPathCount,
    bool RequiresRectifiedInputs,
    bool RequiresCalibration,
    string[] RequiredInputResources,
    string[] OutputResources,
    string[] NegativeChecks);

public sealed record MimirStereoDepthFieldCandidate(
    string CandidateKey,
    string CalibrationId,
    string CameraPairId,
    string ProducerKey,
    string ProfileId,
    string LeftObservationKey,
    string RightObservationKey,
    string LeftResourceKey,
    string RightResourceKey,
    string DisparityResourceKey,
    string ConfidenceResourceKey,
    int Width,
    int Height,
    int DisparityLevels,
    int AggregationPathCount,
    double MinDepthMeters,
    double MaxDepthMeters,
    double Confidence,
    long ObservedTimeNs);

public static class MimirStereoDepthConfigurations
{
    public static MimirStereoDepthKernelProfile D3D12SgmLibSgmProvenance { get; } = new(
        "d3d12-sgm-libsgm-provenance",
        "Fensalir-owned HLSL/D3D12 stereo SGM lane modeled on libSGM's CUDA pipeline shape.",
        MimirStereoDepthAlgorithm.SemiGlobalMatching,
        "Fensalir D3D12 compute",
        "https://github.com/fixstars/libSGM",
        "Apache-2.0",
        LiveDependency: false,
        DisparityLevels: 128,
        AggregationPathCount: 4,
        RequiresRectifiedInputs: true,
        RequiresCalibration: true,
        RequiredInputResources: ["left Texture2D", "right Texture2D", "stereo calibration"],
        OutputResources: ["R16Float disparity SurfacePage", "R8_UNorm confidence Texture2D"],
        NegativeChecks:
        [
            "no CUDA runtime dependency",
            "no CPU disparity image in the live path",
            "no monocular model accepted as metric depth without scale authority",
            "no untyped texture handle without resource and fence provenance"
        ]);

    public static MimirStereoDepthKernelProfile FastAcvNetReference { get; } = new(
        "fast-acvnet-reference",
        "MIT learned-stereo reference for quality/failure comparison; not the first live kernel.",
        MimirStereoDepthAlgorithm.LearnedStereoReference,
        "research/provenance",
        "https://github.com/gangweiX/Fast-ACVNet",
        "MIT",
        LiveDependency: false,
        DisparityLevels: 192,
        AggregationPathCount: 0,
        RequiresRectifiedInputs: true,
        RequiresCalibration: true,
        RequiredInputResources: ["left Texture2D", "right Texture2D", "model weights"],
        OutputResources: ["learned disparity/depth reference"],
        NegativeChecks: ["does not own live geometry", "does not introduce model runtime before D3D12 SGM socket exists"]);

    public static MimirStereoDepthKernelProfile DepthAnythingSmallReference { get; } = new(
        "depth-anything-v2-small-reference",
        "Apache-2.0 monocular relative-depth reference for assistive evidence only.",
        MimirStereoDepthAlgorithm.MonocularReference,
        "research/provenance",
        "https://github.com/DepthAnything/Depth-Anything-V2",
        "Apache-2.0 for V2 Small only; larger variants are non-commercial",
        LiveDependency: false,
        DisparityLevels: 0,
        AggregationPathCount: 0,
        RequiresRectifiedInputs: false,
        RequiresCalibration: false,
        RequiredInputResources: ["single camera Texture2D", "model weights"],
        OutputResources: ["relative depth reference"],
        NegativeChecks: ["does not own metric scene geometry", "does not bypass calibrated stereo"]);

    public static IReadOnlyList<MimirStereoDepthKernelProfile> BuiltIn { get; } =
    [
        D3D12SgmLibSgmProvenance,
        FastAcvNetReference,
        DepthAnythingSmallReference
    ];
}
