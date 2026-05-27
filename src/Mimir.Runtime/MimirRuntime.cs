using Aquarium.Engine;
using Aquarium.Engine.Audio;
using Aquarium.Engine.Input;
using Aquarium.Engine.Render;
using Aquarium.Engine.Ui;
using CultMath;
using Mimir.Runtime.Synchronization;
using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace Mimir.Runtime;

public sealed class MimirRuntime : IAquariumRuntime
{
    private const float DefaultAudioSyncUpdateIntervalSeconds = 0.1f;
    private const double HybridPassiveConfidenceThreshold = 0.12;
    private const double CalibrationStartSeconds = 0.5;
    private const int CalibrationBatchSegments = 4;
    private const int HybridWatermarkIntervalSegments = 4;
    private const float DefaultSpectrumUpdateIntervalSeconds = 0.2f;
    private const int SpectrumHistoryWindowCount = 40;
    private const float SpectrumChannelSeparation = 1.75f;
    private const float SpectrumWindowDepthSeparation = 0.1f;
    private const float SpectrumWidth = 12.0f;
    private const float SpectrumAmplitudeHeight = 1.1f;
    private const float SpectrumCameraDefaultDistanceMultiplier = 10.0f;
    private const float SpectrumCameraDefaultAngleDegrees = 25.0f;
    private const float SpectrumCameraMinimumAngleDegrees = 0.0f;
    private const float SpectrumCameraMaximumAngleDegrees = 45.0f;
    private const float SpectrumCameraFitPadding = 1.12f;
    private const float SpectrumFrustumMinimumNear = 0.01f;
    private const float SpectrumSplineTubePadding = 0.18f;
    private const int DefaultSpectrumSourceLaneCapacity = 8;
    private const string SpectrumFieldResourceKey = "mimir:resource:spectrum:field-upload";
    private const string SpectrumRampResourceKey = "aquarium:resource:ramp:blackbody";
    private const string SpectrumRampTexturePath = @"D:\WIP4\Projects\Aetheria\Assets\Resources\Ramps\blackbody.png";
    private readonly MimirSynchronizationHub synchronization;
    private readonly MimirFensalirFieldLowering fieldLowering;
    private readonly MimirAudioSpectrumAnalyzer spectrumAnalyzer;
    private readonly IReadOnlyList<MimirStreamSourceFactory> sourceFactories;
    private readonly AquariumUiDocument ui;
    private readonly AquariumAudioDocument audio = new();
    private readonly MimirAudioSynchronizationSettings audioSyncSettings;
    private readonly float telemetryIntervalSeconds;
    private readonly float audioSyncUpdateIntervalSeconds;
    private readonly float spectrumUpdateIntervalSeconds;
    private readonly int spectrumTubeSubdivisions;
    private readonly int spectrumSourceLaneCapacity;
    private readonly float calibrationGain;
    private readonly float watermarkGain;
    private readonly bool syntheticSpectrumPreview;
    private readonly MimirBioacousticContestantRenderer? complexContourWitness;
    private int lastPollCount;
    private float runtimeSeconds;
    private float nextAudioSyncSeconds;
    private float nextSpectrumSeconds;
    private float nextTelemetrySeconds;
    private bool sceneReady;
    private ulong calibrationSegmentIndex;
    private long lastHybridWatermarkSegment = -1;
    private double lastAudioSyncAnalysisMilliseconds;
    private double lastSpectrumAnalysisMilliseconds;
    private double lastPassiveSynchronizationConfidence;
    private int lastDroppedSpectrumSourceLaneCount;
    private float spectrumCameraDistanceMultiplier = SpectrumCameraDefaultDistanceMultiplier;
    private float spectrumCameraAngleDegrees = SpectrumCameraDefaultAngleDegrees;
    private AquariumCameraFrustum lastSpectrumFrustum = AquariumCameraFrustum.Default;
    private Vector3 lastSpectrumCameraPosition;
    private Vector3 lastSpectrumCameraTarget;
    private (Vector3 Min, Vector3 Max) lastSpectrumAabb = (Vector3.Zero, Vector3.One);
    private IReadOnlyList<MimirAudioSynchronizationReport> lastAudioSynchronizationReports = [];
    private IReadOnlyList<MimirAudioSpectrumSnapshot> lastAudioSpectra = [];
    private long spectrumHistorySequence;
    private readonly Queue<MimirSpectrumHistoryFrame> spectrumHistory = new();

    public MimirRuntime(AquariumRuntimeOptions options)
        : this(options, MimirRuntimeConfiguration.Load())
    {
    }

    public MimirRuntime(AquariumRuntimeOptions options, MimirRuntimeConfiguration configuration)
        : this(options, configuration.Settings, configuration.SourceFactories)
    {
    }

    public MimirRuntime(AquariumRuntimeOptions options, MimirSynchronizationSettings settings)
        : this(options, settings, Array.Empty<IMimirStreamSource>())
    {
    }

    public MimirRuntime(
        AquariumRuntimeOptions options,
        MimirSynchronizationSettings settings,
        IEnumerable<IMimirStreamSource> streamSources)
        : this(options, settings, Array.Empty<MimirStreamSourceFactory>())
    {
        foreach (var source in streamSources)
        {
            synchronization.AddSource(source);
        }
    }

    private MimirRuntime(
        AquariumRuntimeOptions options,
        MimirSynchronizationSettings settings,
        IEnumerable<MimirStreamSourceFactory> sourceFactories)
    {
        Options = options;
        synchronization = new MimirSynchronizationHub(settings);
        fieldLowering = new MimirFensalirFieldLowering(new MimirFensalirLoweringOptions(
            settings.BufferDuration.TotalSeconds,
            settings.BufferDuration.TotalSeconds));
        spectrumAnalyzer = new MimirAudioSpectrumAnalyzer(ParseSpectrumFftSize(), ParseSpectrumBandCount());
        this.sourceFactories = sourceFactories.ToArray();
        audioSyncSettings = settings.Audio;
        telemetryIntervalSeconds = ParseTelemetryIntervalSeconds();
        audioSyncUpdateIntervalSeconds = ParseAudioSyncIntervalSeconds();
        spectrumUpdateIntervalSeconds = ParseSpectrumUpdateIntervalSeconds();
        spectrumTubeSubdivisions = ParseSpectrumTubeSubdivisions();
        spectrumSourceLaneCapacity = ParseSpectrumSourceLaneCapacity();
        calibrationGain = settings.Audio.CalibrationGain;
        watermarkGain = settings.Audio.WatermarkGain;
        syntheticSpectrumPreview = IsTruthy(Environment.GetEnvironmentVariable("MIMIR_SYNTHETIC_SPECTRUM_PREVIEW"));
        complexContourWitness = settings.Audio.EnableComplexContourRuntime
            ? new MimirBioacousticContestantRenderer(
                MimirBioacousticContestants.BuiltIn.FirstOrDefault(profile =>
                    string.Equals(profile.Id, settings.Audio.BioacousticWitnessProfileId, StringComparison.OrdinalIgnoreCase))
                ?? MimirBioacousticContestants.CanaryPacketTrill)
            : null;
        nextAudioSyncSeconds = audioSyncUpdateIntervalSeconds;
        nextTelemetrySeconds = telemetryIntervalSeconds;
        ui = CreateUi();
    }

    public AquariumRuntimeOptions Options { get; }

    public AquariumFrame Frame => CreateFrame();

    public GraphicsSettings GraphicsSettings { get; set; } = GraphicsSettings.Default;

    public AquariumRenderPlan RenderPlan { get; } = CreateRenderPlan();

    public AquariumUiDocument Ui => ui;

    public AquariumAudioDocument Audio => audio;

    private AquariumFrame CreateFrame()
    {
        var channelCount = Math.Max(1, lastAudioSpectra.Count);
        var fieldEvidenceFrame = BuildFieldEvidenceFrame();
        var windowCount = Math.Max(1, spectrumHistory.Count);
        var (splineMin, splineMax) = SpectrumSplineAabb(channelCount, windowCount);
        var cameraTarget = (splineMin + splineMax) * 0.5f;
        var cameraPosition = SpectrumCameraPosition(
            channelCount,
            windowCount,
            cameraTarget,
            spectrumCameraDistanceMultiplier,
            spectrumCameraAngleDegrees);
        var cameraFrustum = FitSpectrumCameraFrustum(splineMin, splineMax, cameraPosition, cameraTarget);
        lastSpectrumFrustum = cameraFrustum;
        lastSpectrumCameraPosition = cameraPosition;
        lastSpectrumCameraTarget = cameraTarget;
        lastSpectrumAabb = (splineMin, splineMax);
        return new AquariumFrame(
            new ViewFrame(Vector2.Zero, 24.0f) { Frustum = cameraFrustum },
            cameraPosition,
            cameraTarget,
            runtimeSeconds,
            Vector2.Zero,
            new AquariumSceneState
            {
                TraceHeightFieldSurface = false,
                UseStarfieldBackground = false,
                FieldEvidenceFrame = fieldEvidenceFrame,
                BufferFieldFrame = AquariumBufferFieldFrame.Empty,
                SplineFrame = AquariumSplineFrame.Empty,
            });
    }

    private AquariumFieldEvidenceFrame BuildFieldEvidenceFrame()
    {
        var buffers = synchronization.Buffers.Buffers;
        var windows = new List<MimirRollingStreamWindow>(buffers.Count);
        var observations = new List<MimirObservation>(buffers.Count);
        MimirFensalirBridgeMapper.MapWindows(buffers, windows);
        MimirFensalirBridgeMapper.MapLatestObservations(buffers, observations);

        var constraints = new List<MimirCalibrationConstraint>(synchronization.AudioSynchronizationStates.Count);
        MimirFensalirBridgeMapper.MapCalibrationConstraints(synchronization.AudioSynchronizationStates, constraints);

        var observationsByWindow = observations.ToDictionary(observation => observation.WindowId, StringComparer.Ordinal);
        var intents = new List<MimirSurfaceIntent>(observations.Count);
        foreach (var window in windows)
        {
            if (!observationsByWindow.TryGetValue(window.WindowId, out var observation))
            {
                continue;
            }

            if (window.SourceKind == MimirStreamKind.Audio)
            {
                continue;
            }

            intents.Add(BuildSurfaceIntent(window, observation));
        }

        var frame = fieldLowering.BuildFieldEvidenceFrame(windows, observations, constraints, intents);
        return AddSpectrumFieldEvidence(frame);
    }

    private static MimirSurfaceIntent BuildSurfaceIntent(MimirRollingStreamWindow window, MimirObservation observation)
    {
        if (window.SourceKind == MimirStreamKind.Audio)
        {
            return new MimirSurfaceIntent(
                IntentKey: $"{window.WindowId}:spectrum",
                SourceObservationKeys: [observation.ObservationKey],
                Domain: MimirSurfaceDomain.AudioSpectrum,
                Axes: new MimirSurfaceAxes("frequency", "amplitude", "time-age", "stream"),
                SupportPolicy: new MimirSurfaceSupportPolicy("rolling-spectrum", 0.0, window.Duration.TotalSeconds),
                MaterialGraph: new MimirSurfaceMaterialIntent("spectrum-evidence", "source-evidence", observation.Confidence),
                UpdateBudget: new MimirSurfaceUpdateBudget(0.0, 1, Math.Max(0, window.Payload.ByteLength)),
                Purpose: MimirSurfaceIntentPurpose.Debug);
        }

        return MimirFensalirBridgeMapper.MapDefaultSurfaceIntent(window, observation);
    }

    private AquariumFieldEvidenceFrame AddSpectrumFieldEvidence(AquariumFieldEvidenceFrame frame)
    {
        if (lastAudioSpectra.Count == 0)
        {
            return frame;
        }

        var spectra = lastAudioSpectra
            .Where(static spectrum => spectrum.BandDecibels.Count >= 2)
            .OrderBy(static spectrum => spectrum.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (spectra.Length == 0)
        {
            return frame;
        }

        var historyFrames = spectrumHistory.Count > 0
            ? spectrumHistory.Reverse().ToArray()
            : [new MimirSpectrumHistoryFrame(spectrumHistorySequence, runtimeSeconds, spectra)];
        var allSourceIds = historyFrames
            .SelectMany(static history => history.Spectra)
            .Where(static spectrum => spectrum.BandDecibels.Count >= 2)
            .Select(static spectrum => spectrum.SourceId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static sourceId => sourceId, StringComparer.Ordinal)
            .ToArray();
        var sourceIds = allSourceIds
            .Take(spectrumSourceLaneCapacity)
            .ToArray();
        if (sourceIds.Length == 0)
        {
            lastDroppedSpectrumSourceLaneCount = 0;
            return frame;
        }

        lastDroppedSpectrumSourceLaneCount = allSourceIds.Length - sourceIds.Length;
        var sourceIndexById = sourceIds
            .Select((sourceId, index) => (sourceId, index))
            .ToDictionary(static item => item.sourceId, static item => item.index, StringComparer.Ordinal);
        var width = Math.Max(2, historyFrames
            .SelectMany(static history => history.Spectra)
            .Where(static spectrum => spectrum.BandDecibels.Count >= 2)
            .Select(static spectrum => spectrum.BandDecibels.Count)
            .DefaultIfEmpty(2)
            .Max());
        var historyCount = historyFrames.Length;
        var sourceCount = sourceIds.Length;
        var activeColumnCount = checked(sourceCount * historyCount);
        var resourceColumnCapacity = checked(spectrumSourceLaneCapacity * SpectrumHistoryWindowCount);
        var newestHistorySlot = PositiveModulo(-historyFrames[0].Sequence, SpectrumHistoryWindowCount);
        var rollingOffset = checked(newestHistorySlot * spectrumSourceLaneCapacity);
        var samples = new float[width * resourceColumnCapacity];
        for (var historyIndex = 0; historyIndex < historyFrames.Length; historyIndex++)
        {
            var historySlot = PositiveModulo(-historyFrames[historyIndex].Sequence, SpectrumHistoryWindowCount);
            foreach (var spectrum in historyFrames[historyIndex].Spectra)
            {
                if (!sourceIndexById.TryGetValue(spectrum.SourceId, out var sourceIndex))
                {
                    continue;
                }

                var physicalColumn = historySlot * spectrumSourceLaneCapacity + sourceIndex;
                WriteNormalizedSpectrum(spectrum, samples.AsSpan(physicalColumn * width, width));
            }
        }

        const string domainKey = "mimir:domain:spectrum:field-upload";
        var version = unchecked((ulong)Math.Max(0, spectrumHistorySequence));
        var confidence = (float)Math.Clamp(spectra.Average(static spectrum => spectrum.Rms > 0.0 ? 1.0 : 0.65), 0.0, 1.0);
        var maxY = Math.Max(0, sourceCount - 1) * SpectrumChannelSeparation + SpectrumAmplitudeHeight;
        var maxZ = Math.Max(0, SpectrumHistoryWindowCount - 1) * SpectrumWindowDepthSeparation;
        var support = new AquariumFieldSupport(
            Center: new Vector3(0.0f, maxY * 0.5f, maxZ * 0.5f),
            Radius: new Vector3(5.0f, Math.Max(SpectrumAmplitudeHeight, maxY), Math.Max(0.1f, maxZ)),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: 5.0f,
            ProjectedError: 0.0f,
            Curvature: 0.0f,
            TemporalUncertainty: (float)Math.Max(0.0, spectrumUpdateIntervalSeconds));
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.DebugIntent,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: activeColumnCount,
            Seed: 0x5EC7_0001u);
        var resource = new AquariumFieldResourceDeclaration(
            ResourceKey: SpectrumFieldResourceKey,
            Kind: AquariumFieldResourceKind.StructuredBuffer,
            Residency: AquariumFieldResourceResidency.GpuResident,
            Access: AquariumFieldShaderAccess.ShaderResource,
            Format: "Float32",
            Width: width,
            Height: resourceColumnCapacity,
            DepthOrCount: samples.Length,
            StrideBytes: sizeof(float),
            ValidFromNs: 0,
            ValidUntilNs: 0,
            Version: version,
            NativeHandle: IntPtr.Zero,
            NativeHandleKind: "fensalir-owned-spectrum-upload");
        var ramp = AquariumFieldResourceDeclaration.LocalTexture2D(
            SpectrumRampResourceKey,
            SpectrumRampTexturePath,
            version: 1);
        var axisStepX = width > 1 ? 10.0f / (width - 1) : 10.0f;
        var claims = new List<AquariumFieldClaim>(sourceCount);
        var candidates = new List<AquariumFieldCandidate>(sourceCount);
        var lowerings = new List<AquariumFieldTubeSplineLowering>(sourceCount);
        for (var sourceIndex = 0; sourceIndex < sourceIds.Length; sourceIndex++)
        {
            var sourceId = sourceIds[sourceIndex];
            var claimKey = $"intent:mimir:spectrum:field-upload:{sourceId}";
            var sourceSupport = support with
            {
                Center = new Vector3(0.0f, sourceIndex * SpectrumChannelSeparation + SpectrumAmplitudeHeight * 0.5f, maxZ * 0.5f),
                Radius = new Vector3(5.0f, SpectrumAmplitudeHeight, Math.Max(0.1f, maxZ)),
            };
            var sourceProposal = proposal with
            {
                RepresentedCandidateCount = historyCount,
                Seed = unchecked(proposal.Seed + (uint)sourceIndex),
            };
            claims.Add(new AquariumFieldClaim(
                ClaimKey: claimKey,
                DomainKey: domainKey,
                ProducerKey: "Mimir.Runtime",
                Layer: AquariumFieldLayer.Form,
                Encoding: AquariumFieldEncoding.Tube,
                Support: sourceSupport,
                Proposal: sourceProposal,
                PayloadHandle: SpectrumFieldResourceKey,
                ObservedTimeNs: 0,
                Confidence: confidence));
            candidates.Add(new AquariumFieldCandidate(
                $"{claimKey}:candidate",
                claimKey,
                AquariumFieldLayer.Form,
                AquariumFieldEncoding.Tube,
                sourceProposal,
                AquariumFieldGuide.Valid(confidence, spectrumUpdateIntervalSeconds)));
            lowerings.Add(new AquariumFieldTubeSplineLowering(
                LoweringKey: $"tube-spline:mimir:spectrum:field-upload:{sourceId}",
                ClaimKey: claimKey,
                ResourceKey: SpectrumFieldResourceKey,
                Width: width,
                Height: resourceColumnCapacity,
                StrideBytes: sizeof(float),
                FirstColumn: sourceIndex,
                ColumnCount: historyCount,
                ColumnStride: spectrumSourceLaneCapacity,
                RollingModulo: resourceColumnCapacity,
                RollingOffset: rollingOffset,
                Origin: new Vector3(-5.0f, sourceIndex * SpectrumChannelSeparation, 0.0f),
                AxisStep: new Vector3(axisStepX, 0.0f, 0.0f),
                ColumnStep: new Vector3(0.0f, 0.0f, SpectrumWindowDepthSeparation),
                AmplitudePower: 2.0f,
                AmplitudeScale: SpectrumAmplitudeHeight,
                NormalizeMin: 0.0f,
                NormalizeMax: 1.0f,
                BaseRadius: 0.012f,
                RadiusScale: 0.030f,
                Alpha: 0.92f,
                Feather: 0.20f,
                RampTexturePath: SpectrumRampTexturePath,
                RampResourceKey: SpectrumRampResourceKey,
                EmissionScale: 10.0f,
                CatmullRomSubdivisions: spectrumTubeSubdivisions).Normalized());
        }

        return new AquariumFieldEvidenceFrame
        {
            Domains =
            [
                .. frame.Domains,
                new AquariumFieldDomain(
                    domainKey,
                    "",
                    AquariumFieldDomainKind.RollingBuffer,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    new Vector3(-5.0f, 0.0f, 0.0f),
                    new Vector3(5.0f, maxY, Math.Max(0.1f, maxZ)),
                    Vector3.Zero,
                    "Mimir.Runtime"),
            ],
            Claims = [.. frame.Claims, .. claims],
            Candidates = [.. frame.Candidates, .. candidates],
            BackendPackets = frame.BackendPackets,
            Resources = [.. frame.Resources, resource, ramp],
            ResourceUploads =
            [
                .. frame.ResourceUploads,
                new AquariumFieldResourceUpload
                {
                    ResourceKey = SpectrumFieldResourceKey,
                    Version = version,
                    Float32Data = samples,
                },
            ],
            TubeSplineLowerings = [.. frame.TubeSplineLowerings, .. lowerings],
            AccumulationWindowSeconds = frame.AccumulationWindowSeconds,
            PresentationDelaySeconds = frame.PresentationDelaySeconds,
        };
    }

    private static void WriteNormalizedSpectrum(MimirAudioSpectrumSnapshot spectrum, Span<float> destination)
    {
        destination.Clear();
        var bands = spectrum.BandDecibels;
        if (bands.Count == 0)
        {
            return;
        }

        var floor = Math.Min(spectrum.NoiseFloorDb, bands.Min());
        var ceiling = Math.Max(-24.0, bands.Max());
        var span = Math.Max(18.0, ceiling - floor);
        var count = Math.Min(destination.Length, bands.Count);
        for (var index = 0; index < count; index++)
        {
            destination[index] = (float)Math.Clamp((bands[index] - floor) / span, 0.0, 1.0);
        }
    }

    private static int PositiveModulo(long value, int modulo)
    {
        var remainder = value % modulo;
        return (int)(remainder < 0 ? remainder + modulo : remainder);
    }

    private static Vector3 SpectrumCameraPosition(
        int spectrumCount,
        int windowCount,
        Vector3 target,
        float distanceMultiplier,
        float angleDegrees)
    {
        var oldDistance = math.sqrt(2.0f) * math.max(1.0f, spectrumCount) * SpectrumChannelSeparation;
        var aabbDepth = math.max(1.0f, windowCount) * SpectrumWindowDepthSeparation;
        var distance = math.max(oldDistance * math.clamp(distanceMultiplier, 1.0f, 80.0f), aabbDepth + 0.1f);
        var angleRadians = math.radians(math.clamp(
            angleDegrees,
            SpectrumCameraMinimumAngleDegrees,
            SpectrumCameraMaximumAngleDegrees));
        var viewBack = math.normalize(new float3(0.0f, math.sin(angleRadians), -math.cos(angleRadians)));
        return ToVector3(ToFloat3(target) + viewBack * distance);
    }

    private static (Vector3 Min, Vector3 Max) SpectrumSplineAabb(int spectrumCount, int windowCount)
    {
        var halfWidth = SpectrumWidth * 0.5f;
        var maxY = Math.Max(0, spectrumCount - 1) * SpectrumChannelSeparation + SpectrumAmplitudeHeight;
        var maxZ = Math.Max(0, windowCount - 1) * SpectrumWindowDepthSeparation;
        var padding = new Vector3(SpectrumSplineTubePadding, SpectrumSplineTubePadding, SpectrumSplineTubePadding);
        return (
            new Vector3(-halfWidth, 0.0f, 0.0f) - padding,
            new Vector3(halfWidth, maxY, maxZ) + padding);
    }

    private static AquariumCameraFrustum FitSpectrumCameraFrustum(
        Vector3 aabbMin,
        Vector3 aabbMax,
        Vector3 cameraPosition,
        Vector3 cameraTarget)
    {
        var camera = ToFloat3(cameraPosition);
        var target = ToFloat3(cameraTarget);
        SpectrumCameraBasis(camera, target, out var forward, out var right, out var up);
        var maxSlopeX = 0.001f;
        var maxSlopeY = 0.001f;
        var minZ = float.PositiveInfinity;
        var maxZ = float.NegativeInfinity;
        var corners = SpectrumAabbCorners(aabbMin, aabbMax).ToArray();

        foreach (var corner in corners)
        {
            var delta = ToFloat3(corner) - camera;
            var z = math.max(math.dot(delta, forward), SpectrumFrustumMinimumNear);
            minZ = math.min(minZ, z);
            maxZ = math.max(maxZ, z);
            maxSlopeX = math.max(maxSlopeX, math.abs(math.dot(delta, right) / z));
            maxSlopeY = math.max(maxSlopeY, math.abs(math.dot(delta, up) / z));
        }

        var depthPadding = math.max((maxZ - minZ) * 0.08f, SpectrumSplineTubePadding);
        var near = math.max(SpectrumFrustumMinimumNear, minZ - depthPadding);
        var far = math.max(near + 0.001f, maxZ + depthPadding);
        var halfWidth = maxSlopeX * near * SpectrumCameraFitPadding;
        var halfHeight = maxSlopeY * near * SpectrumCameraFitPadding;

        return new AquariumCameraFrustum(
            -halfWidth,
            halfWidth,
            -halfHeight,
            halfHeight,
            near,
            far).Normalized();
    }

    private static void SpectrumCameraBasis(
        float3 cameraPosition,
        float3 cameraTarget,
        out float3 forward,
        out float3 right,
        out float3 up)
    {
        forward = math.normalize(cameraTarget - cameraPosition);
        var worldUp = math.abs(forward.y) > 0.96f ? new float3(0.0f, 0.0f, 1.0f) : new float3(0.0f, 1.0f, 0.0f);
        right = math.normalize(math.cross(worldUp, forward));
        up = math.normalize(math.cross(forward, right));
    }

    private static float4 ProjectSpectrumPoint(
        Vector3 worldPosition,
        Vector3 cameraPosition,
        Vector3 cameraTarget,
        AquariumCameraFrustum frustum)
    {
        var camera = ToFloat3(cameraPosition);
        SpectrumCameraBasis(camera, ToFloat3(cameraTarget), out var forward, out var right, out var up);
        var view = ToCameraSpace(ToFloat3(worldPosition), camera, forward, right, up);
        return ProjectCameraSpace(view, frustum);
    }

    private static float3 ToCameraSpace(float3 world, float3 camera, float3 forward, float3 right, float3 up)
    {
        var delta = world - camera;
        return new float3(math.dot(delta, right), math.dot(delta, up), math.dot(delta, forward));
    }

    private static float4 ProjectCameraSpace(float3 view, AquariumCameraFrustum frustum)
    {
        var normalized = frustum.Normalized();
        var z = math.max(view.z, 0.0001f);
        var slope = view.xy / z;
        var min = new float2(normalized.Left / normalized.Near, normalized.Bottom / normalized.Near);
        var max = new float2(normalized.Right / normalized.Near, normalized.Top / normalized.Near);
        var uv = (slope - min) / math.max(max - min, new float2(0.0001f, 0.0001f));
        return new float4(uv * 2.0f - 1.0f, math.saturate(z / math.max(normalized.Far, 0.0001f)), 1.0f);
    }

    private static float3 ToFloat3(Vector3 value) => new(value.X, value.Y, value.Z);

    private static Vector3 ToVector3(float3 value) => new(value.x, value.y, value.z);

    private static IEnumerable<Vector3> SpectrumAabbCorners(Vector3 min, Vector3 max)
    {
        for (var x = 0; x < 2; x++)
        {
            for (var y = 0; y < 2; y++)
            {
                for (var z = 0; z < 2; z++)
                {
                    yield return new Vector3(
                        x == 0 ? min.X : max.X,
                        y == 0 ? min.Y : max.Y,
                        z == 0 ? min.Z : max.Z);
                }
            }
        }
    }

    public AquariumSynthDocument Synth => AquariumSynthDocument.Empty;

    public void RegisterStreamSource(IMimirStreamSource source)
    {
        synchronization.AddSource(source);
    }

    public void Start()
    {
        Console.WriteLine($"Mimir runtime sync buffers: {synchronization.Summary()} @ {synchronization.Settings.BufferDuration.TotalSeconds:0.###}s audioSync={audioSyncSettings.Mode} reference={audioSyncSettings.ReferenceSourceId}");
        Console.WriteLine("Mimir empty scene runtime booted.");
    }

    public void Update(float deltaSeconds, InputState input)
    {
        if (!sceneReady)
        {
            return;
        }

        runtimeSeconds += Math.Max(deltaSeconds, 0.0f);
        lastPollCount = synchronization.PollSources();
        QueueCalibrationTimeline();
        UpdateAudioSpectra();
        UpdateAudioSynchronization();
        EmitTelemetry();
    }

    public void OnSceneReady()
    {
        if (sceneReady)
        {
            return;
        }

        sceneReady = true;
        StartConfiguredSources();
        nextAudioSyncSeconds = runtimeSeconds + audioSyncUpdateIntervalSeconds;
        nextSpectrumSeconds = runtimeSeconds;
        Console.WriteLine($"Mimir Fensalir scene ready at {runtimeSeconds:0.000}s; runtime audio tests and spectra enabled.");
    }

    public AquariumFrame ComposeFrame(AquariumFrame frame, AquariumFrameInput input)
    {
        return frame;
    }

    public void FlushState()
    {
    }

    public void Dispose()
    {
        synchronization.Dispose();
    }

    private AquariumUiDocument CreateUi()
    {
        return new AquariumUiDocument()
            .Panel("Mimir Sync", 18.0f, 82.0f, 390.0f, panel =>
            {
                panel.Section("Rolling Buffers");
                panel.Readout("Window", () => $"{synchronization.Settings.BufferDuration.TotalSeconds:0.###}s");
                panel.Readout("Streams", synchronization.Summary);
                panel.Readout("Sources", () => $"{synchronization.SourceCount}");
                panel.Readout("Last poll", () => $"{lastPollCount} samples");
                panel.Readout("Ingested", () => $"{synchronization.IngestedSamples}");
                panel.Readout("Buffer details", DescribeBuffers);
                panel.Readout("Audio sync", DescribeAudioSync);
                panel.Readout("Audio sync state", DescribeAudioSyncState);
                panel.Readout("Aligned audio", DescribeAlignedAudio);
                panel.Readout("Chirplet reference", DescribeChirpletReference);
                panel.Section("Live FFT");
                panel.Readout("Spectrum cadence", () => sceneReady
                    ? $"{lastAudioSpectra.Count} spectra fft={lastAudioSpectra.FirstOrDefault()?.FftSize ?? 0} analyze={lastSpectrumAnalysisMilliseconds:0.00}ms"
                    : "waiting for Fensalir scene ready");
                panel.Slider(
                    "Perspective",
                    () => spectrumCameraDistanceMultiplier,
                    value => spectrumCameraDistanceMultiplier = Math.Clamp(value, 1.0f, 80.0f),
                    1.0f,
                    80.0f,
                    "0.0x",
                    "Dolly distance multiplier. The projection frustum is fitted to the full spectrum trail AABB.");
                panel.Slider(
                    "Angle",
                    () => spectrumCameraAngleDegrees,
                    value => spectrumCameraAngleDegrees = Math.Clamp(
                        value,
                        SpectrumCameraMinimumAngleDegrees,
                        SpectrumCameraMaximumAngleDegrees),
                    SpectrumCameraMinimumAngleDegrees,
                    SpectrumCameraMaximumAngleDegrees,
                    "0.0 deg",
                    "Polar camera pitch from -Z toward +Y around the spectrum trail AABB.");
                panel.Readout("Frustum", DescribeSpectrumFrustum);
                panel.Readout(
                    "Tube budget",
                    () =>
                    {
                        var dropped = lastDroppedSpectrumSourceLaneCount > 0
                            ? $" dropped={lastDroppedSpectrumSourceLaneCount}"
                            : "";
                        return $"{spectrumHistory.Count}/{SpectrumHistoryWindowCount} age columns x {Math.Min(lastAudioSpectra.Count, spectrumSourceLaneCapacity)}/{spectrumSourceLaneCapacity} lanes x {spectrumTubeSubdivisions} subdivisions{dropped}";
                    },
                    "Current Mimir-side TubeField subdivision cost before Fensalir applies its fixed geometry budget.");
                panel.TextBox(
                    "Spectra",
                    DescribeSpectra,
                    _ => { },
                    lines: 12,
                    acceptsReturn: false,
                    monospace: true,
                    tooltip: "Cached FFT spectra from the runtime rolling audio buffers.");
            });
    }

    private static AquariumRenderPlan CreateRenderPlan()
    {
        var app = new AquariumApp();
        var scene = app.RenderTargets.Hdr("scene");
        app.Cameras.Perspective("main");
        app.Graph.Pass("scene").Fullscreen();
        app.Features.Presentation(scene.Color);
        app.Features.DirectWriteOverlay();
        app.Debug.View("Scene", scene.Color);
        return app.Plan;
    }

    private void QueueCalibrationTimeline()
    {
        if (!ShouldEmitCalibrationTimeline())
        {
            return;
        }

        var currentSegmentIndex = CurrentCalibrationSegmentIndex();
        if (audioSyncSettings.Mode == MimirAudioSyncMode.Hybrid)
        {
            calibrationSegmentIndex = currentSegmentIndex;
            if (calibrationSegmentIndex % HybridWatermarkIntervalSegments != 0UL ||
                (long)calibrationSegmentIndex == lastHybridWatermarkSegment)
            {
                return;
            }
        }
        else if (calibrationSegmentIndex + 1UL < currentSegmentIndex)
        {
            calibrationSegmentIndex = currentSegmentIndex;
        }

        var nextSegmentSeconds = CalibrationStartSeconds + calibrationSegmentIndex * MimirBioacousticTimeline.SegmentSeconds;
        if (runtimeSeconds < nextSegmentSeconds)
        {
            return;
        }

        var segmentCount = audioSyncSettings.Mode == MimirAudioSyncMode.Hybrid ? 1 : CalibrationBatchSegments;
        var outputGain = audioSyncSettings.Mode == MimirAudioSyncMode.Hybrid ? watermarkGain : calibrationGain;
        var leftBatch = RenderCalibrationBatchPcm16Base64(
            calibrationSegmentIndex,
            segmentCount,
            MimirBioacousticSpeaker.Left,
            out var peak);
        var rightBatch = RenderCalibrationBatchPcm16Base64(
            calibrationSegmentIndex,
            segmentCount,
            MimirBioacousticSpeaker.Right,
            out var rightPeak);
        peak = Math.Max(peak, rightPeak);
        audio.EnqueuePcm16Base64(
            leftBatch,
            MimirBioacousticTimeline.SampleRate,
            channels: 1,
            gain: outputGain,
            pan: -1.0f);
        audio.EnqueuePcm16Base64(
            rightBatch,
            MimirBioacousticTimeline.SampleRate,
            channels: 1,
            gain: outputGain,
            pan: 1.0f);
        Console.WriteLine(
            $"mimir-bioacoustic-batch mode={DescribeAudioSyncMode()} firstSegment={calibrationSegmentIndex} segments={segmentCount} seconds={segmentCount * MimirBioacousticTimeline.SegmentSeconds:0.00} peak={peak:0.000000} gain={outputGain:0.###} leftBase64Bytes={leftBatch.Length} rightBase64Bytes={rightBatch.Length}");
        if (audioSyncSettings.Mode == MimirAudioSyncMode.Hybrid)
        {
            lastHybridWatermarkSegment = (long)calibrationSegmentIndex;
        }
        else
        {
            calibrationSegmentIndex += (ulong)segmentCount;
        }
    }

    private ulong CurrentCalibrationSegmentIndex()
    {
        if (runtimeSeconds <= CalibrationStartSeconds)
        {
            return 0UL;
        }

        return (ulong)Math.Floor((runtimeSeconds - CalibrationStartSeconds) / MimirBioacousticTimeline.SegmentSeconds);
    }

    private string DescribeChirpletReference()
    {
        if (!ShouldEmitCalibrationTimeline())
        {
            return $"{audioSyncSettings.ReferenceSourceId} passive mode; calibration emission disabled";
        }

        var emittedUntilSeconds = calibrationSegmentIndex * MimirBioacousticTimeline.SegmentSeconds;
        return $"{audioSyncSettings.ReferenceSourceId} {DescribeAudioSyncMode()} bioacoustic timeline {MimirBioacousticTimeline.SegmentSeconds:0.00}s segments emitted to {emittedUntilSeconds:0.00}s passiveConfidence={lastPassiveSynchronizationConfidence:0.000}";
    }

    private string RenderCalibrationBatchPcm16Base64(
        ulong firstSegment,
        int segmentCount,
        MimirBioacousticSpeaker speaker,
        out float peak)
    {
        var samplesPerSegment = (int)Math.Round(MimirBioacousticTimeline.SegmentSeconds * MimirBioacousticTimeline.SampleRate);
        var bytes = new byte[samplesPerSegment * Math.Max(1, segmentCount) * sizeof(short)];
        var byteIndex = 0;
        peak = 0.0f;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var samples = complexContourWitness == null
                ? MimirBioacousticTimeline.Default.RenderSegmentMonoFloat(
                    firstSegment + (ulong)segment,
                    MimirBioacousticTimeline.SampleRate,
                    speaker)
                : complexContourWitness.RenderSegmentMonoFloat(
                    firstSegment + (ulong)segment,
                    MimirBioacousticTimeline.SegmentSeconds,
                    MimirBioacousticTimeline.SampleRate,
                    speaker);
            for (var index = 0; index < samples.Length; index++)
            {
                peak = Math.Max(peak, Math.Abs(samples[index]));
                var sample = (short)Math.Round(Math.Clamp(samples[index], -1.0f, 1.0f) * short.MaxValue);
                bytes[byteIndex++] = (byte)(sample & 0xff);
                bytes[byteIndex++] = (byte)((sample >> 8) & 0xff);
            }
        }

        return Convert.ToBase64String(bytes);
    }

    private string DescribeBuffers()
    {
        return string.Join(" | ", synchronization.Buffers.Buffers.Select(DescribeBuffer));
    }

    private void UpdateAudioSynchronization()
    {
        if (runtimeSeconds < nextAudioSyncSeconds)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        synchronization.AnalyzeAudioSynchronizationStep(audioSyncSettings.ReferenceSourceId, audioSyncSettings.Mode);
        if (synchronization.ComplexContourRuntimeEnabled)
        {
            synchronization.AnalyzeComplexContourSynchronizationStep(audioSyncSettings.ReferenceSourceId, runtimeSeconds);
        }
        stopwatch.Stop();
        lastAudioSyncAnalysisMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        lastAudioSynchronizationReports = synchronization.AudioSynchronizationReports;
        UpdatePassiveSynchronizationConfidence(lastAudioSynchronizationReports);
        nextAudioSyncSeconds = runtimeSeconds + audioSyncUpdateIntervalSeconds;
    }

    private void UpdateAudioSpectra()
    {
        if (runtimeSeconds < nextSpectrumSeconds)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        lastAudioSpectra = spectrumAnalyzer.Analyze(synchronization.Buffers.Buffers, audioSyncSettings.ReferenceSourceId);
        if (lastAudioSpectra.Count == 0 && syntheticSpectrumPreview)
        {
            lastAudioSpectra = GenerateSyntheticSpectrumPreview(runtimeSeconds);
        }

        if (lastAudioSpectra.Count > 0)
        {
            spectrumHistory.Enqueue(new MimirSpectrumHistoryFrame(
                spectrumHistorySequence++,
                runtimeSeconds,
                lastAudioSpectra
                    .OrderBy(spectrum => spectrum.SourceId, StringComparer.Ordinal)
                    .ToArray()));
            while (spectrumHistory.Count > SpectrumHistoryWindowCount)
            {
                spectrumHistory.Dequeue();
            }
        }
        stopwatch.Stop();
        lastSpectrumAnalysisMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        nextSpectrumSeconds = runtimeSeconds + spectrumUpdateIntervalSeconds;
    }

    private void StartConfiguredSources()
    {
        foreach (var factory in sourceFactories)
        {
            var source = factory.Create();
            if (source != null)
            {
                synchronization.AddSource(source);
            }
        }
    }

    private void EmitTelemetry()
    {
        if (telemetryIntervalSeconds <= 0.0f || runtimeSeconds < nextTelemetrySeconds)
        {
            return;
        }

        var loopback = synchronization.Buffers.Buffers.FirstOrDefault(buffer =>
            string.Equals(buffer.Descriptor.SourceId, audioSyncSettings.ReferenceSourceId, StringComparison.Ordinal));
        var states = synchronization.AudioSynchronizationStates;
        Console.WriteLine(
            $"mimir-sync-telemetry t={runtimeSeconds:0.00}s audioSync={audioSyncSettings.Mode} loopbackCount={loopback?.Count ?? 0} loopbackEdgeNs={loopback?.EdgeNs ?? 0} reports={lastAudioSynchronizationReports.Count} states={states.Count} analyzeMs={lastAudioSyncAnalysisMilliseconds:0.0} aligned={DescribeAlignedAudio()}");
        Console.WriteLine($"mimir-sync-buffers {DescribeAudioBuffers()}");
        Console.WriteLine($"mimir-spectrum-frustum {DescribeSpectrumFrustum()}");
        foreach (var spectrum in lastAudioSpectra.OrderBy(snapshot => snapshot.SourceId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"mimir-spectrum {spectrum.SourceId} label=\"{spectrum.Label}\" rate={spectrum.SampleRate} fft={spectrum.FftSize} rms={spectrum.Rms:0.000000} peak={spectrum.Peak:0.000000} floorDb={spectrum.NoiseFloorDb:0.0} peaks={DescribeSpectrumPeaks(spectrum)}");
        }

        foreach (var report in lastAudioSynchronizationReports.OrderBy(report => report.SourceId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"mimir-sync-report {report.ReferenceSourceId}->{report.SourceId} evidence={report.EvidenceKind} delaySamples={report.FractionalDelaySamples:0.000000} delayUs={report.DelayMicroseconds:0.000} delayMs={report.DelayMilliseconds:0.000} confidence={report.Confidence:0.000} timelineEvents={report.TimelineMatchedEvents} timelineConfidence={report.TimelineConfidence:0.000}");
        }

        foreach (var report in synchronization.ComplexContourSynchronizationReports.OrderBy(report => report.SourceId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"mimir-complex-contour-report {report.ReferenceSourceId}->{report.SourceId} delaySamples={report.FractionalDelaySamples:0.000000} delayUs={report.DelayMicroseconds:0.000} confidence={report.Confidence:0.000} directHits={report.TimelineMatchedEvents}");
        }

        foreach (var state in states)
        {
            Console.WriteLine(
                $"mimir-sync-state {state.ReferenceSourceId}->{state.SourceId} delaySamples={state.SmoothedDelaySamples:0.000000} delayUs={state.DelayMicroseconds:0.000} delayMs={state.DelayMilliseconds:0.000} sroPpm={state.SamplingRateOffsetPpm:0.000} confidence={state.Confidence:0.000}");
        }

        foreach (var trace in synchronization.AudioSynchronizationDecodeTraces.OrderBy(trace => trace.SourceId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"mimir-sync-decode {trace.ReferenceSourceId}->{trace.SourceId} status={trace.Status} compared={trace.ComparedSamples} rate={trace.SampleRate} refFrames={trace.ReferenceFrames} refAnchors={trace.ReferenceAnchors} refClock={trace.ReferenceClockConfidence:0.000} refEnergy={trace.ReferenceBestEnergy:0.000} candFrames={trace.CandidateFrames} candAnchors={trace.CandidateAnchors} candClock={trace.CandidateClockConfidence:0.000} candEnergy={trace.CandidateBestEnergy:0.000} matched={trace.MatchedEvents} confidence={trace.Confidence:0.000}");
        }

        nextTelemetrySeconds += telemetryIntervalSeconds;
    }

    private static float ParseTelemetryIntervalSeconds()
    {
        return float.TryParse(Environment.GetEnvironmentVariable("MIMIR_SYNC_TELEMETRY_SECONDS"), out var seconds)
            ? Math.Clamp(seconds, 0.0f, 60.0f)
            : 0.0f;
    }

    private static float ParseAudioSyncIntervalSeconds()
    {
        return float.TryParse(Environment.GetEnvironmentVariable("MIMIR_AUDIO_SYNC_INTERVAL_SECONDS"), out var seconds)
            ? Math.Clamp(seconds, 0.1f, 10.0f)
            : DefaultAudioSyncUpdateIntervalSeconds;
    }

    private static float ParseSpectrumUpdateIntervalSeconds()
    {
        return float.TryParse(Environment.GetEnvironmentVariable("MIMIR_SPECTRUM_INTERVAL_SECONDS"), out var seconds)
            ? Math.Clamp(seconds, 0.05f, 10.0f)
            : DefaultSpectrumUpdateIntervalSeconds;
    }

    private static int ParseSpectrumFftSize()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("MIMIR_SPECTRUM_FFT_SIZE"), out var value)
            ? Math.Clamp(value, 1024, 32768)
            : 8192;
    }

    private static int ParseSpectrumBandCount()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("MIMIR_SPECTRUM_BANDS"), out var value)
            ? Math.Clamp(value, 32, 192)
            : 96;
    }

    private static int ParseSpectrumTubeSubdivisions()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("MIMIR_SPECTRUM_TUBE_SUBDIVISIONS"), out var value)
            ? Math.Clamp(value, 1, 16)
            : 4;
    }

    private static int ParseSpectrumSourceLaneCapacity()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("MIMIR_SPECTRUM_SOURCE_LANES"), out var value)
            ? Math.Clamp(value, 1, 32)
            : DefaultSpectrumSourceLaneCapacity;
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<MimirAudioSpectrumSnapshot> GenerateSyntheticSpectrumPreview(float timeSeconds)
    {
        const int bandCount = 48;
        var snapshots = new MimirAudioSpectrumSnapshot[4];
        for (var channel = 0; channel < snapshots.Length; channel++)
        {
            var bands = new double[bandCount];
            for (var band = 0; band < bandCount; band++)
            {
                var x = band / (double)Math.Max(1, bandCount - 1);
                var drift = timeSeconds * (0.18 + channel * 0.035);
                var formantA = Math.Exp(-Math.Pow((x - (0.18 + 0.05 * Math.Sin(drift + channel))) / 0.055, 2.0));
                var formantB = Math.Exp(-Math.Pow((x - (0.48 + 0.08 * Math.Sin(drift * 1.7 + channel * 0.4))) / 0.075, 2.0));
                var formantC = Math.Exp(-Math.Pow((x - (0.78 + 0.03 * Math.Cos(drift * 2.1 + channel))) / 0.045, 2.0));
                var ripple = 0.5 + 0.5 * Math.Sin((band * 0.73) + drift * 9.0 + channel);
                var energy = Math.Clamp(formantA * 0.8 + formantB * 0.95 + formantC * 0.7 + ripple * 0.10, 0.0, 1.0);
                bands[band] = -78.0 + energy * 58.0;
            }

            snapshots[channel] = new MimirAudioSpectrumSnapshot(
                $"preview-ch{channel + 1}",
                $"Synthetic Preview {channel + 1}",
                192000,
                8192,
                8192,
                0.16 + channel * 0.02,
                0.42 + channel * 0.03,
                -78.0,
                [],
                bands,
                (long)(timeSeconds * 1_000_000_000.0));
        }

        return snapshots;
    }

    private bool ShouldEmitCalibrationTimeline()
    {
        if (!HasReferenceAudioBuffer())
        {
            return false;
        }

        return audioSyncSettings.Mode switch
        {
            MimirAudioSyncMode.ChirpOnly => true,
            MimirAudioSyncMode.Passive => false,
            MimirAudioSyncMode.Hybrid => lastPassiveSynchronizationConfidence < HybridPassiveConfidenceThreshold,
            _ => true,
        };
    }

    private bool HasReferenceAudioBuffer()
    {
        return synchronization.Buffers.Buffers.Any(buffer =>
            buffer.Descriptor.Kind == MimirStreamKind.Audio &&
            string.Equals(buffer.Descriptor.SourceId, audioSyncSettings.ReferenceSourceId, StringComparison.Ordinal));
    }

    private string DescribeAudioSyncMode()
    {
        return audioSyncSettings.Mode switch
        {
            MimirAudioSyncMode.ChirpOnly => "chirp-only",
            MimirAudioSyncMode.Passive => "passive",
            MimirAudioSyncMode.Hybrid => "hybrid",
            _ => audioSyncSettings.Mode.ToString(),
        };
    }

    private string DescribeAudioBuffers()
    {
        return string.Join(" | ", synchronization.Buffers.Buffers
            .Where(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio)
            .OrderBy(buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal)
            .Select(buffer => $"{buffer.Descriptor.Label}[{buffer.Descriptor.SourceId}]:{buffer.Count}@{buffer.EdgeNs}"));
    }

    private string DescribeSpectra()
    {
        if (!sceneReady)
        {
            return "Fensalir scene not ready.";
        }

        if (lastAudioSpectra.Count == 0)
        {
            return "No sample-bearing audio buffers yet.";
        }

        var builder = new StringBuilder();
        foreach (var spectrum in lastAudioSpectra)
        {
            builder
                .Append(spectrum.Label)
                .Append(" [")
                .Append(spectrum.SourceId)
                .Append("]")
                .Append(" rms=")
                .Append(spectrum.Rms.ToString("0.000000"))
                .Append(" peak=")
                .Append(spectrum.Peak.ToString("0.000000"))
                .Append(" ")
                .Append(SpectrumBars(spectrum))
                .AppendLine();
            builder
                .Append("  ")
                .Append(DescribeSpectrumPeaks(spectrum))
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private string DescribeSpectrumFrustum()
    {
        var frustum = lastSpectrumFrustum.Normalized();
        var clip = SpectrumClipBounds(
            lastSpectrumAabb.Min,
            lastSpectrumAabb.Max,
            lastSpectrumCameraPosition,
            lastSpectrumCameraTarget,
            frustum);
        return $"dolly={spectrumCameraDistanceMultiplier:0.0}x angle={spectrumCameraAngleDegrees:0.0}deg L/R={frustum.Left:0.###}/{frustum.Right:0.###} B/T={frustum.Bottom:0.###}/{frustum.Top:0.###} N/F={frustum.Near:0.###}/{frustum.Far:0.###} clip={clip.Min.x:0.###},{clip.Min.y:0.###}->{clip.Max.x:0.###},{clip.Max.y:0.###}";
    }

    private static (float2 Min, float2 Max) SpectrumClipBounds(
        Vector3 aabbMin,
        Vector3 aabbMax,
        Vector3 cameraPosition,
        Vector3 cameraTarget,
        AquariumCameraFrustum frustum)
    {
        var min = new float2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new float2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var corner in SpectrumAabbCorners(aabbMin, aabbMax))
        {
            var clip = ProjectSpectrumPoint(corner, cameraPosition, cameraTarget, frustum);
            min = math.min(min, clip.xy);
            max = math.max(max, clip.xy);
        }

        return (min, max);
    }

    private static string SpectrumBars(MimirAudioSpectrumSnapshot spectrum)
    {
        const string ramp = " .:-=+*#%@";
        if (spectrum.BandDecibels.Count == 0)
        {
            return "";
        }

        var floor = Math.Min(spectrum.NoiseFloorDb, spectrum.BandDecibels.Min());
        var ceiling = Math.Max(-24.0, spectrum.BandDecibels.Max());
        var span = Math.Max(12.0, ceiling - floor);
        var chars = spectrum.BandDecibels.Select(value =>
        {
            var normalized = Math.Clamp((value - floor) / span, 0.0, 1.0);
            return ramp[(int)Math.Round(normalized * (ramp.Length - 1))];
        });
        return new string(chars.ToArray());
    }

    private static string DescribeSpectrumPeaks(MimirAudioSpectrumSnapshot spectrum)
    {
        return spectrum.Peaks.Count == 0
            ? "no FFT peaks"
            : string.Join(", ", spectrum.Peaks.Select(peak => $"{peak.FrequencyHz / 1000.0:0.00}kHz {peak.Decibels:0.0}dB"));
    }

    private void UpdatePassiveSynchronizationConfidence(IReadOnlyList<MimirAudioSynchronizationReport> reports)
    {
        var passiveConfidence = reports
            .Where(report => string.Equals(report.EvidenceKind, "passive", StringComparison.Ordinal))
            .Select(report => report.Confidence)
            .DefaultIfEmpty(0.0)
            .Max();
        lastPassiveSynchronizationConfidence = passiveConfidence > 0.0
            ? passiveConfidence
            : lastPassiveSynchronizationConfidence * 0.95;
    }

    private static string DescribeBuffer(MimirRollingStreamBuffer buffer)
    {
        var latest = buffer.Latest;
        if (latest?.VideoFrame is { } frame)
        {
            return $"{buffer.Descriptor.Label} [{buffer.Descriptor.SourceId}]: {buffer.Count} {frame.Width}x{frame.Height} {frame.PixelFormat} bytes {latest.Value.ByteLength} edge {buffer.EdgeNs}";
        }

        if (latest?.AudioBlock is { } block)
        {
            return $"{buffer.Descriptor.Label} [{buffer.Descriptor.SourceId}]: {buffer.Count} {block.Channels}ch {block.SampleRate}Hz {block.SampleFormat} frames {block.FrameCount} bytes {latest.Value.ByteLength} edge {buffer.EdgeNs}";
        }

        return $"{buffer.Descriptor.Label} [{buffer.Descriptor.SourceId}]: {buffer.Count} edge {buffer.EdgeNs}";
    }

    private string DescribeAudioSync()
    {
        if (audioSyncSettings.Mode == MimirAudioSyncMode.Passive)
        {
            return "passive mode; waiting for program-audio coherence";
        }

        var reports = synchronization.AudioSynchronizationReports;
        return reports.Count == 0
            ? "no payload windows"
            : string.Join(" | ", reports.Select(report => $"{report.SourceId}: {report.FractionalDelaySamples:0.000} samples {report.DelayMicroseconds:0.0}us c={report.Confidence:0.00} {report.EvidenceKind} events={report.TimelineMatchedEvents}"));
    }

    private string DescribeAlignedAudio()
    {
        var states = synchronization.AudioSynchronizationStates;
        return states.Count == 0
            ? "no aligned state"
            : $"{states.Count + 1}ch state-ready";
    }

    private string DescribeAudioSyncState()
    {
        var states = synchronization.AudioSynchronizationStates;
        return states.Count == 0
            ? "no sync state"
            : string.Join(" | ", states.Select(state => $"{state.SourceId}: {state.SmoothedDelaySamples:0.000} samples {state.DelayMicroseconds:0.0}us sro={state.SamplingRateOffsetPpm:0.0}ppm c={state.Confidence:0.00}"));
    }

    private readonly record struct MimirSpectrumHistoryFrame(
        long Sequence,
        float RuntimeSeconds,
        IReadOnlyList<MimirAudioSpectrumSnapshot> Spectra);
}
