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

public enum MimirPointCloudProjectionKind
{
    StereoDisparityProjection,
    VisualFeatureTriangulation,
    ExternalDepthSensor
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
    int MinDisparity,
    int CensusRadius,
    double SmoothnessPenaltySmall,
    double SmoothnessPenaltyLarge,
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
    int MinDisparity,
    int DisparityLevels,
    int AggregationPathCount,
    int CensusRadius,
    double SmoothnessPenaltySmall,
    double SmoothnessPenaltyLarge,
    double MinDepthMeters,
    double MaxDepthMeters,
    double Confidence,
    long ObservedTimeNs);

public sealed record MimirPointCloudProjectionProfile(
    string Id,
    string Description,
    MimirPointCloudProjectionKind ProjectionKind,
    string Owner,
    string Provenance,
    string License,
    bool LiveDependency,
    string[] RequiredInputResources,
    string[] OutputResources,
    int DefaultSampleStride,
    double BaselineMeters,
    double FocalLengthPixels,
    double PrincipalPointX,
    double PrincipalPointY,
    string[] NegativeChecks);

public sealed record MimirPointCloudFieldCandidate(
    string CandidateKey,
    string CalibrationId,
    string CameraRigId,
    string ProducerKey,
    string ProfileId,
    string SourceDisparityResourceKey,
    string SourceConfidenceResourceKey,
    string PointCloudResourceKey,
    int Width,
    int Height,
    int SampleStride,
    int MaxPointCount,
    double BaselineMeters,
    double FocalLengthPixels,
    double PrincipalPointX,
    double PrincipalPointY,
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
        MinDisparity: 0,
        CensusRadius: 2,
        SmoothnessPenaltySmall: 8.0,
        SmoothnessPenaltyLarge: 96.0,
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
        MinDisparity: 0,
        CensusRadius: 0,
        SmoothnessPenaltySmall: 0.0,
        SmoothnessPenaltyLarge: 0.0,
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
        MinDisparity: 0,
        CensusRadius: 0,
        SmoothnessPenaltySmall: 0.0,
        SmoothnessPenaltyLarge: 0.0,
        NegativeChecks: ["does not own metric scene geometry", "does not bypass calibrated stereo"]);

    public static IReadOnlyList<MimirStereoDepthKernelProfile> BuiltIn { get; } =
    [
        D3D12SgmLibSgmProvenance,
        FastAcvNetReference,
        DepthAnythingSmallReference
    ];
}

public static class MimirPointCloudConfigurations
{
    public static MimirPointCloudProjectionProfile LeapDisparityPointCloudRoot { get; } = new(
        "leap-disparity-point-cloud-root",
        "D3D12 point-cloud projection rooted in the Leap packed-stereo disparity SurfacePage.",
        MimirPointCloudProjectionKind.StereoDisparityProjection,
        "Fensalir D3D12 compute",
        "Derived from calibrated stereo pinhole projection: z = f * baseline / disparity; disparity source follows the libSGM-provenance D3D12 lane.",
        "Repo-native implementation; upstream stereo kernel provenance remains Apache-2.0 libSGM.",
        LiveDependency: false,
        RequiredInputResources:
        [
            "R16Float disparity SurfacePage",
            "R8_UNorm confidence Texture2D",
            "stereo intrinsics/extrinsics calibration"
        ],
        OutputResources: ["FieldMesh PointList PositionNormalUvColor"],
        DefaultSampleStride: 2,
        BaselineMeters: 0.04,
        FocalLengthPixels: 250.0,
        PrincipalPointX: 320.0,
        PrincipalPointY: 120.0,
        NegativeChecks:
        [
            "no synthetic point cloud without a live disparity resource",
            "no RGB monocular depth accepted as metric geometry owner",
            "no calibration optimizer update before residual ownership exists",
            "no CPU point array in the live D3D12 path"
        ]);

    public static IReadOnlyList<MimirPointCloudProjectionProfile> BuiltIn { get; } =
    [
        LeapDisparityPointCloudRoot
    ];
}
