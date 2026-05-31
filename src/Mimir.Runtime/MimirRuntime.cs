using Aquarium.Engine;
using Aquarium.Engine.Audio;
using Aquarium.Engine.Input;
using Aquarium.Engine.Render;
using Aquarium.Engine.Ui;
using CultMath;
using Mimir.Runtime.Synchronization;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace Mimir.Runtime;

public sealed class MimirRuntime : IAquariumRuntime, IAquariumRuntimeServicesReceiver
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
    private const string SpectrumRampResourceKey = AquariumBuiltInFieldResources.BlackbodyRampResourceKey;
    private static readonly string SpectrumRampTexturePath = AquariumBuiltInFieldResources.ResolveBlackbodyRampPath();
    private readonly MimirSynchronizationHub synchronization;
    private readonly MimirFensalirFieldLowering fieldLowering;
    private readonly MimirAudioSpectrumAnalyzer spectrumAnalyzer;
    private readonly MimirAlignmentActuatorBank audioActuatorBank = new();
    private readonly MimirPresentationControlState presentationControls = new();
    private readonly MimirSceneEditorState sceneEditor = new();
    private readonly MimirObsStemPublicationState obsStemPublication = new(MimirObsPublicationConfigurations.AlignmentActuatorStemBus);
    private readonly MimirObsStemSharedMemoryPublisher? obsStemPublisher;
    private readonly IReadOnlyList<MimirStreamSourceFactory> sourceFactories;
    private readonly AquariumUiDocument ui;
    private readonly AquariumAudioDocument audio = new();
    private readonly MimirAudioSynchronizationSettings audioSyncSettings;
    private readonly float telemetryIntervalSeconds;
    private readonly float audioSyncUpdateIntervalSeconds;
    private readonly float spectrumUpdateIntervalSeconds;
    private readonly int spectrumTubeSubdivisions;
    private readonly int spectrumSourceLaneCapacity;
    private readonly bool obsProofVisualEnabled;
    private readonly float calibrationGain;
    private readonly float watermarkGain;
    private readonly bool syntheticSpectrumPreview;
    private readonly bool syntheticSingleTubePreview;
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
    private MimirObsStemPublicationSnapshot lastObsStemPublication = MimirObsStemPublicationSnapshot.Empty;
    private long spectrumHistorySequence;
    private long audioActuatorFrameSequence;
    private bool audioActuatorProgramQueued;
    private readonly Queue<MimirSpectrumHistoryFrame> spectrumHistory = new();
    private AquariumRuntimeServices runtimeServices = AquariumRuntimeServices.Empty;
    private MimirActuatorFrame lastAudioActuatorFrame = MimirActuatorFrame.Empty;

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
        obsProofVisualEnabled = IsTruthy(Environment.GetEnvironmentVariable("MIMIR_OBS_PROOF_VISUAL"));
        calibrationGain = settings.Audio.CalibrationGain;
        watermarkGain = settings.Audio.WatermarkGain;
        syntheticSpectrumPreview = IsTruthy(Environment.GetEnvironmentVariable("MIMIR_SYNTHETIC_SPECTRUM_PREVIEW"));
        syntheticSingleTubePreview = IsTruthy(Environment.GetEnvironmentVariable("MIMIR_SYNTHETIC_SINGLE_TUBE_PREVIEW"));
        if (syntheticSingleTubePreview)
        {
            GraphicsSettings = new GraphicsSettings(
                RenderDebugMode: 0,
                SceneExposure: 1.0f,
                BloomIntensity: 0.075f,
                BloomVeilIntensity: 0.0f,
                FieldReservoirMode: GraphicsSettings.FieldReservoirModeNativeDomain,
                FieldReservoirScale: 0.5f,
                FieldReservoirSpatialReuseBudget: GraphicsSettings.Default.FieldReservoirSpatialReuseBudget);
        }

        obsStemPublisher = CreateObsStemPublisher();
        complexContourWitness = settings.Audio.EnableComplexContourRuntime
            ? new MimirBioacousticContestantRenderer(
                MimirBioacousticContestants.BuiltIn.FirstOrDefault(profile =>
                    string.Equals(profile.Id, settings.Audio.BioacousticWitnessProfileId, StringComparison.OrdinalIgnoreCase))
                ?? MimirBioacousticContestants.CanaryPacketTrill)
            : null;
        presentationControls.SyncFromBuffers(synchronization.Buffers.Buffers);
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

    public void AttachServices(AquariumRuntimeServices services)
    {
        runtimeServices = services;
        synchronization.AttachTextureLeaseClient(new MimirFensalirTextureLeaseClient(services.FieldResources));
    }

    private AquariumFrame CreateFrame()
    {
        var channelCount = Math.Max(1, lastAudioSpectra.Count);
        var fieldEvidenceFrame = BuildFieldEvidenceFrame();
        var editorSplineFrame = sceneEditor.Enabled
            ? sceneEditor.BuildEditorSplineFrame()
            : AquariumSplineFrame.Empty;
        var editorSdfObjects = sceneEditor.Enabled
            ? sceneEditor.BuildEditorSdfObjects()
            : [];
        var editorSdfLights = sceneEditor.Enabled
            ? sceneEditor.BuildEditorSdfLights()
            : [];
        var windowCount = Math.Max(1, spectrumHistory.Count);
        var (splineMin, splineMax) = syntheticSingleTubePreview
            ? (new Vector3(-5.76643f, -1.0f, 0.0f), new Vector3(5.76643f, 1.0f, 0.0f))
            : SpectrumSplineAabb(channelCount, windowCount);
        var spectrumCameraTarget = (splineMin + splineMax) * 0.5f;
        var spectrumCameraPosition = syntheticSingleTubePreview
            ? new Vector3(0.0f, 0.0f, -18.367355f)
            : SpectrumCameraPosition(
                channelCount,
                windowCount,
                spectrumCameraTarget,
                spectrumCameraDistanceMultiplier,
                spectrumCameraAngleDegrees);
        var spectrumCameraFrustum = syntheticSingleTubePreview
            ? new AquariumCameraFrustum(-0.036f, 0.036f, -0.02025f, 0.02025f, 0.1f, 100.0f)
            : FitSpectrumCameraFrustum(splineMin, splineMax, spectrumCameraPosition, spectrumCameraTarget);
        var cameraTarget = sceneEditor.Enabled
            ? sceneEditor.CameraTarget
            : spectrumCameraTarget;
        var cameraPosition = sceneEditor.Enabled
            ? sceneEditor.CameraPosition
            : spectrumCameraPosition;
        var cameraFrustum = sceneEditor.Enabled
            ? sceneEditor.CameraFrustum
            : spectrumCameraFrustum;
        lastSpectrumFrustum = spectrumCameraFrustum;
        lastSpectrumCameraPosition = spectrumCameraPosition;
        lastSpectrumCameraTarget = spectrumCameraTarget;
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
                UseStudioBackground = !syntheticSingleTubePreview,
                UseStarfieldBackground = obsProofVisualEnabled,
                SdfObjects = MergeSdfObjects(
                    obsProofVisualEnabled ? BuildObsProofSdfObjects(runtimeSeconds) : [],
                    editorSdfObjects),
                SdfLights = MergeSdfLights(
                    obsProofVisualEnabled ? BuildObsProofSdfLights(runtimeSeconds) : [],
                    editorSdfLights),
                FieldEvidenceFrame = fieldEvidenceFrame,
                BufferFieldFrame = AquariumBufferFieldFrame.Empty,
                SplineFrame = editorSplineFrame,
            });
    }

    private static IReadOnlyList<AquariumSdfObject> MergeSdfObjects(
        IReadOnlyList<AquariumSdfObject> first,
        IReadOnlyList<AquariumSdfObject> second) =>
        first.Count == 0 ? second : second.Count == 0 ? first : first.Concat(second).ToArray();

    private static IReadOnlyList<AquariumSdfLight> MergeSdfLights(
        IReadOnlyList<AquariumSdfLight> first,
        IReadOnlyList<AquariumSdfLight> second) =>
        first.Count == 0 ? second : second.Count == 0 ? first : first.Concat(second).ToArray();

    private static AquariumSdfObject[] BuildObsProofSdfObjects(float timeSeconds)
    {
        var center = new Vector3(MathF.Sin(timeSeconds * 0.8f) * 1.2f, 0.25f, 0.0f);
        return
        [
            new AquariumSdfObject(
                new Vector4(center, 1.2f),
                new Vector4(center, 0.0f),
                new Vector4(1.0f, timeSeconds, 0.0f, 0.0f))
        ];
    }

    private static AquariumSdfLight[] BuildObsProofSdfLights(float timeSeconds)
    {
        var lightOrbit = new Vector3(MathF.Cos(timeSeconds) * 2.0f, 1.8f, MathF.Sin(timeSeconds) * 2.0f);
        return
        [
            new AquariumSdfLight(
                new Vector4(lightOrbit, 3.0f),
                new Vector4(4.0f, 0.9f, 0.2f, 10.0f)),
            new AquariumSdfLight(
                new Vector4(-1.8f, 1.0f, 2.5f, 2.0f),
                new Vector4(0.1f, 1.4f, 3.6f, 10.0f))
        ];
    }

    private AquariumFieldEvidenceFrame BuildFieldEvidenceFrame()
    {
        var buffers = synchronization.Buffers.Buffers;
        var windows = new List<MimirRollingStreamWindow>(buffers.Count);
        var observations = new List<MimirObservation>(buffers.Count);
        MimirFensalirBridgeMapper.MapWindows(buffers, windows);
        MimirFensalirBridgeMapper.MapLatestObservations(buffers, observations);
        CommitProducerLeases(windows);

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

            if (!window.Payload.HasResource)
            {
                continue;
            }

            if (!presentationControls.IncludesVideo(window.StreamId) ||
                !sceneEditor.IncludesVideoSource(window.StreamId))
            {
                continue;
            }

            intents.Add(BuildSurfaceIntent(
                window,
                observation,
                presentationControls.VideoOpacity(window.StreamId),
                sceneEditor.PlacementForSource(window.StreamId)));
        }

        var frame = fieldLowering.BuildFieldEvidenceFrame(windows, observations, constraints, intents);
        frame = AddSpectrumFieldEvidence(frame);
        return AddLeapGeometryFieldEvidence(frame, windows);
    }

    private void CommitProducerLeases(IReadOnlyList<MimirRollingStreamWindow> windows)
    {
        foreach (var window in windows)
        {
            if (window.SourceKind != MimirStreamKind.Video ||
                string.IsNullOrWhiteSpace(window.Payload.ResourceKey) ||
                window.Payload.ProducerFenceValue == 0)
            {
                continue;
            }

            runtimeServices.FieldResources.CommitLeaseVersion(
                window.Payload.ResourceKey,
                window.SequenceId,
                window.Payload.ProducerFenceValue);
        }
    }

    private static MimirSurfaceIntent BuildSurfaceIntent(
        MimirRollingStreamWindow window,
        MimirObservation observation,
        float opacity = 1.0f,
        MimirCompositorPlacement? placement = null)
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

        return new MimirSurfaceIntent(
            IntentKey: $"{window.WindowId}:surface",
            SourceObservationKeys: [observation.ObservationKey],
            Domain: MimirSurfaceDomain.CameraImage,
            Axes: new MimirSurfaceAxes("image-x", "image-y", "time-age", "stream"),
            SupportPolicy: new MimirSurfaceSupportPolicy("program-composite", 0.0, window.Duration.TotalSeconds),
            MaterialGraph: new MimirSurfaceMaterialIntent("program-video", "source-evidence", Math.Clamp(opacity, 0.0f, 1.0f)),
            UpdateBudget: new MimirSurfaceUpdateBudget(0.0, 1, Math.Max(0, window.Payload.ByteLength)),
            Purpose: MimirSurfaceIntentPurpose.Production)
        {
            Placement = placement,
        };
    }

    private AquariumFieldEvidenceFrame AddLeapGeometryFieldEvidence(
        AquariumFieldEvidenceFrame frame,
        IReadOnlyList<MimirRollingStreamWindow> windows)
    {
        var stereoFrame = fieldLowering.BuildLeapPackedStereoDepthCandidateFrame(windows, frame.Resources);
        if (!stereoFrame.HasInput)
        {
            return frame;
        }

        var pointCloudFrame = fieldLowering.BuildLeapPackedStereoPointCloudCandidateFrame(windows);
        return new AquariumFieldEvidenceFrame
        {
            Domains = [.. frame.Domains, .. stereoFrame.Domains, .. pointCloudFrame.Domains],
            Claims = [.. frame.Claims, .. stereoFrame.Claims, .. pointCloudFrame.Claims],
            Candidates = [.. frame.Candidates, .. stereoFrame.Candidates, .. pointCloudFrame.Candidates],
            BackendPackets = [.. frame.BackendPackets, .. stereoFrame.BackendPackets, .. pointCloudFrame.BackendPackets],
            Resources = MergeResources(MergeResources(frame.Resources, stereoFrame.Resources), pointCloudFrame.Resources),
            ResourceUploads = [.. frame.ResourceUploads, .. stereoFrame.ResourceUploads, .. pointCloudFrame.ResourceUploads],
            TubeSplineLowerings = [.. frame.TubeSplineLowerings, .. stereoFrame.TubeSplineLowerings, .. pointCloudFrame.TubeSplineLowerings],
            StereoDepthLowerings = [.. frame.StereoDepthLowerings, .. stereoFrame.StereoDepthLowerings, .. pointCloudFrame.StereoDepthLowerings],
            AccumulationWindowSeconds = frame.AccumulationWindowSeconds,
            PresentationDelaySeconds = frame.PresentationDelaySeconds,
        };
    }

    private static IReadOnlyList<AquariumFieldResourceDeclaration> MergeResources(
        IReadOnlyList<AquariumFieldResourceDeclaration> first,
        IReadOnlyList<AquariumFieldResourceDeclaration> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var resources = new List<AquariumFieldResourceDeclaration>(first.Count + second.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in first.Concat(second))
        {
            if (resource.HasIdentity && seen.Add(resource.ResourceKey))
            {
                resources.Add(resource);
            }
        }

        return resources;
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
        var renderedHistoryCount = syntheticSingleTubePreview ? 1 : historyCount;
        var activeColumnCount = checked(sourceCount * renderedHistoryCount);
        var resourceColumnCapacity = checked(spectrumSourceLaneCapacity * SpectrumHistoryWindowCount);
        var newestHistorySlot = PositiveModulo(-historyFrames[0].Sequence, SpectrumHistoryWindowCount);
        var rollingOffset = checked(newestHistorySlot * spectrumSourceLaneCapacity);
        var resourceUploads = new List<AquariumFieldResourceUpload>(1);
        for (var historyIndex = 0; historyIndex < Math.Min(1, historyFrames.Length); historyIndex++)
        {
            var historySlot = PositiveModulo(-historyFrames[historyIndex].Sequence, SpectrumHistoryWindowCount);
            var uploadColumnOffset = checked(historySlot * spectrumSourceLaneCapacity);
            var samples = new float[width * spectrumSourceLaneCapacity];
            foreach (var spectrum in historyFrames[historyIndex].Spectra)
            {
                if (!sourceIndexById.TryGetValue(spectrum.SourceId, out var sourceIndex))
                {
                    continue;
                }

                WriteNormalizedSpectrum(spectrum, samples.AsSpan(sourceIndex * width, width));
            }

            resourceUploads.Add(new AquariumFieldResourceUpload
            {
                ResourceKey = SpectrumFieldResourceKey,
                Version = unchecked((ulong)Math.Max(0, spectrumHistorySequence)),
                ElementOffset = checked(uploadColumnOffset * width),
                Float32Data = samples,
            });
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
            DepthOrCount: checked(width * resourceColumnCapacity),
            StrideBytes: sizeof(float),
            ValidFromNs: 0,
            ValidUntilNs: 0,
            Version: version,
            NativeHandle: IntPtr.Zero,
            NativeHandleKind: "fensalir-owned-spectrum-upload");
        var ramp = AquariumBuiltInFieldResources.BlackbodyRamp(version: 1);
        var axisLength = syntheticSingleTubePreview ? 11.53286f : 10.0f;
        var axisStepX = width > 1 ? axisLength / (width - 1) : axisLength;
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
                RepresentedCandidateCount = renderedHistoryCount,
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
                ColumnCount: renderedHistoryCount,
                ColumnStride: spectrumSourceLaneCapacity,
                RollingModulo: resourceColumnCapacity,
                RollingOffset: rollingOffset,
                Origin: syntheticSingleTubePreview
                    ? new Vector3(-axisLength * 0.5f, 0.0f, 0.0f)
                    : new Vector3(-5.0f, sourceIndex * SpectrumChannelSeparation, 0.0f),
                AxisStep: new Vector3(axisStepX, 0.0f, 0.0f),
                ColumnStep: new Vector3(0.0f, 0.0f, SpectrumWindowDepthSeparation),
                AmplitudePower: 2.0f,
                AmplitudeScale: syntheticSingleTubePreview ? 0.0f : SpectrumAmplitudeHeight,
                NormalizeMin: 0.0f,
                NormalizeMax: 1.0f,
                BaseRadius: syntheticSingleTubePreview ? 1.0f : 0.012f,
                RadiusScale: syntheticSingleTubePreview ? 0.0f : 0.030f,
                Alpha: 1.0f,
                Feather: syntheticSingleTubePreview ? 0.02f : 0.20f,
                RampTexturePath: SpectrumRampTexturePath,
                RampResourceKey: SpectrumRampResourceKey,
                EmissionScale: syntheticSingleTubePreview ? 16.0f : 40.0f,
                CatmullRomSubdivisions: syntheticSingleTubePreview ? 1 : spectrumTubeSubdivisions).Normalized());
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
                .. resourceUploads,
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
        presentationControls.SyncFromBuffers(synchronization.Buffers.Buffers);
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
        presentationControls.SyncFromBuffers(synchronization.Buffers.Buffers);
        sceneEditor.SyncSensorFeeds(synchronization.Buffers.Buffers);
        sceneEditor.UpdateInput(deltaSeconds, input);
        ApplyPresentationPostprocess();
        UpdateObsStemPublication();
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
        QueueAudioActuatorProgram();
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
        obsStemPublisher?.Dispose();
        synchronization.Dispose();
    }

    private AquariumUiDocument CreateUi()
    {
        return new AquariumUiDocument()
            .Surface("mimir.editor.surface", "Mimir Editor", 18.0f, 56.0f, 1220.0f, 660.0f, surface =>
            {
                surface.Horizontal("mimir.editor.shell", shell =>
                {
                    shell.Pane("mimir.scene-graph", "Scene Graph", graph =>
                    {
                        graph.Toggle("scene.editor-view", "Editor View", () => sceneEditor.Enabled, value => sceneEditor.Enabled = value);
                        graph.Card("scene.tree", tree =>
                        {
                            tree.Text("scene.root", "Scene", "strong");
                            tree.Text("scene.camera", () => $"{(sceneEditor.SelectedNodeId == "editor-camera" ? "> " : "  ")}Editor Camera <Camera>", "mono");
                            tree.Text("scene.feeds", () => $"{(sceneEditor.IsExpanded("feeds") ? "- " : "+ ")}Sensor Feeds (4)", "mono");
                            tree.Text("scene.feed-kiyo", "    [eye] Kiyo Pro RGB context <SensorFeed>", "mono");
                            tree.Text("scene.feed-eve", "    [eye] Eve image/mic pipe <SensorFeed>", "mono");
                            tree.Text("scene.text", () => $"{(sceneEditor.IsExpanded("text") ? "- " : "+ ")}SDF Text", "mono");
                            tree.Text("scene.models", () => $"{(sceneEditor.IsExpanded("models") ? "- " : "+ ")}Models", "mono");
                        }, weight: 1.6f);
                        graph.Text("scene.hierarchy", sceneEditor.DescribeHierarchy, "mono", weight: 2.8f);
                        graph.Row("scene.nav", nav =>
                        {
                            nav.Button("scene.previous", "Previous", sceneEditor.SelectPrevious);
                            nav.Button("scene.next", "Next", sceneEditor.SelectNext);
                        });
                        graph.Text("scene.selected", sceneEditor.DescribeSelection, "caption", weight: 1.2f);
                    }, weight: 0.85f);

                    shell.Vertical("mimir.editor-center", center =>
                    {
                        center.Pane("mimir.scene-view", "Scene View", scene =>
                        {
                            scene.Preview("scene.view-preview", "Program Preview", () => sceneEditor.PreviewItems, weight: 1.0f);
                            scene.Row("scene.view-actions", actions =>
                            {
                                actions.Button("scene.view-frame-selected", "Frame Selected", sceneEditor.ResetCamera);
                                actions.Button("scene.view-reset-camera", "Reset Camera", sceneEditor.ResetCamera);
                            });
                        }, weight: 1.0f);

                        center.Pane("mimir.assets", "Asset Browser", assets =>
                        {
                            assets.Row("assets.quick", quick =>
                            {
                                quick.Button("assets.add-text", "Add SDF Text", sceneEditor.AddSdfTextPanel);
                                quick.Button("assets.import-model", "Import Model", sceneEditor.ImportModelPlaceholder);
                            });
                            assets.Text("assets.text", () => $"Text: {sceneEditor.PendingText}", "mono");
                            assets.Text("assets.model", () => $"Model: {sceneEditor.PendingModelPath}", "mono");
                        }, weight: 0.72f);
                    }, weight: 1.75f);

                    shell.Pane("mimir.inspector", "Inspector", inspector =>
                    {
                        inspector.Text("inspector.selection", sceneEditor.DescribeSelection, "strong");
                        inspector.Toggle("transform.visible", "Visible", () => sceneEditor.SelectedNode?.Visible ?? false, sceneEditor.SetSelectedVisible);
                        inspector.Toggle("transform.locked", "Locked", () => sceneEditor.SelectedNode?.Locked ?? false, sceneEditor.SetSelectedLocked);
                        inspector.Options("transform.mode", "Mode", () => (int)sceneEditor.GizmoMode, value => sceneEditor.GizmoMode = (MimirSceneEditorGizmoMode)value, SceneEditorGizmoOptions());
                        inspector.Slider("transform.x", "X", () => sceneEditor.SelectedNode?.Transform.Position.X ?? 0.0f, sceneEditor.SetSelectedX, -12.0f, 12.0f, "0.00");
                        inspector.Slider("transform.y", "Y", () => sceneEditor.SelectedNode?.Transform.Position.Y ?? 0.0f, sceneEditor.SetSelectedY, -8.0f, 8.0f, "0.00");
                        inspector.Slider("transform.z", "Z", () => sceneEditor.SelectedNode?.Transform.Position.Z ?? 0.0f, sceneEditor.SetSelectedZ, -8.0f, 8.0f, "0.00");
                        inspector.Slider("transform.rotation", "Rotation", () => sceneEditor.SelectedNode?.Transform.RotationRadians ?? 0.0f, sceneEditor.SetSelectedRotation, -MathF.PI, MathF.PI, "0.00");
                        inspector.Slider("transform.scale-x", "Scale X", () => sceneEditor.SelectedNode?.Transform.Scale.X ?? 1.0f, sceneEditor.SetSelectedScaleX, 0.05f, 8.0f, "0.00");
                        inspector.Slider("transform.scale-y", "Scale Y", () => sceneEditor.SelectedNode?.Transform.Scale.Y ?? 1.0f, sceneEditor.SetSelectedScaleY, 0.05f, 8.0f, "0.00");
                        inspector.Text("inspector.program-placement", "Program Frame", "strong");
                        inspector.Slider("program.x", "X", () => sceneEditor.SelectedProgramX, sceneEditor.SetSelectedProgramX, 0.0f, 1.0f, "0.000");
                        inspector.Slider("program.y", "Y", () => sceneEditor.SelectedProgramY, sceneEditor.SetSelectedProgramY, 0.0f, 1.0f, "0.000");
                        inspector.Slider("program.w", "W", () => sceneEditor.SelectedProgramWidth, sceneEditor.SetSelectedProgramWidth, 0.01f, 1.0f, "0.000");
                        inspector.Slider("program.h", "H", () => sceneEditor.SelectedProgramHeight, sceneEditor.SetSelectedProgramHeight, 0.01f, 1.0f, "0.000");
                        inspector.Row("transform.reset", reset =>
                        {
                            reset.Button("transform.reset-node", "Reset Transform", sceneEditor.ResetSelectedTransform);
                            reset.Button("transform.reset-camera", "Reset Camera", sceneEditor.ResetCamera);
                        });
                        inspector.Text("inspector.create-label", "Create", "strong");
                        inspector.Text("create.text", () => $"Text: {sceneEditor.PendingText}", "mono");
                        inspector.Text("create.model", () => $"Model: {sceneEditor.PendingModelPath}", "mono");
                        inspector.Row("inspector.create-actions", actions =>
                        {
                            actions.Button("create.add-text", "Add SDF Text", sceneEditor.AddSdfTextPanel);
                            actions.Button("create.import-model", "Import Model", sceneEditor.ImportModelPlaceholder);
                        });
                    }, weight: 0.95f);
                });
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

    private void ApplyPresentationPostprocess()
    {
        presentationControls.RefreshPostprocess();
        var post = presentationControls.Postprocess;
        GraphicsSettings = GraphicsSettings with
        {
            SceneExposure = Math.Clamp(post.Exposure, GraphicsSettings.MinSceneExposure, GraphicsSettings.MaxSceneExposure),
            BloomIntensity = Math.Clamp(post.BloomIntensity, GraphicsSettings.MinBloomIntensity, GraphicsSettings.MaxBloomIntensity),
            BloomVeilIntensity = Math.Clamp(post.BloomVeilIntensity, GraphicsSettings.MinBloomVeilIntensity, GraphicsSettings.MaxBloomVeilIntensity),
        };
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

    private IReadOnlyList<AquariumUiOption> VideoFeedOptions()
    {
        var feeds = presentationControls.VideoFeeds;
        return feeds.Count == 0
            ? [new AquariumUiOption(0, "No video")]
            : feeds.Select((feed, index) => new AquariumUiOption(index, $"{feed.Layer + 1}. {feed.DisplayName}")).ToArray();
    }

    private IReadOnlyList<AquariumUiOption> AudioFeedOptions()
    {
        var feeds = presentationControls.AudioFeeds;
        return feeds.Count == 0
            ? [new AquariumUiOption(0, "No audio")]
            : feeds.Select((feed, index) => new AquariumUiOption(index, feed.DisplayName)).ToArray();
    }

    private IReadOnlyList<AquariumUiOption> LutPresetOptions() =>
        presentationControls.LutPresets
            .Select((preset, index) => new AquariumUiOption(index, preset.DisplayName))
            .ToArray();

    private static IReadOnlyList<AquariumUiOption> SceneEditorGizmoOptions() =>
    [
        new AquariumUiOption((int)MimirSceneEditorGizmoMode.Translate, "Grab"),
        new AquariumUiOption((int)MimirSceneEditorGizmoMode.Rotate, "Rotate"),
        new AquariumUiOption((int)MimirSceneEditorGizmoMode.Scale, "Resize"),
    ];

    private string DescribeProgramVideo()
    {
        var feeds = presentationControls.VideoFeeds;
        if (feeds.Count == 0)
        {
            return "no video buffers";
        }

        var active = feeds
            .Where(feed => presentationControls.IncludesVideo(feed.SourceId))
            .Select(feed => $"{feed.Layer + 1}:{feed.SourceId}@{feed.Opacity:0.00}");
        return string.Join(" | ", active.DefaultIfEmpty("black"));
    }

    private string DescribeProgramAudio()
    {
        var feeds = presentationControls.AudioFeeds;
        if (feeds.Count == 0)
        {
            return "no audio buffers";
        }

        var active = feeds
            .Select(feed => $"{feed.SourceId}:{presentationControls.AudioGain(feed.SourceId):0.00}")
            .ToArray();
        return string.Join(" | ", active);
    }

    private string DescribePostprocess()
    {
        var post = presentationControls.Postprocess;
        return $"{post.PresetName} strength={post.LutStrength:0.00} exp={post.Exposure:0.000} contrast={post.Contrast:0.00} sat={post.Saturation:0.00} lut={post.LutPath}";
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
        lastAudioActuatorFrame = audioActuatorBank.Update(
            synchronization.AudioSynchronizationStates,
            audioSyncUpdateIntervalSeconds);
        PublishAudioActuatorFrame(lastAudioActuatorFrame);
        nextAudioSyncSeconds = runtimeSeconds + audioSyncUpdateIntervalSeconds;
    }

    private void PublishAudioActuatorFrame(MimirActuatorFrame frame)
    {
        if (frame.Commands.Count == 0)
        {
            return;
        }

        QueueAudioActuatorProgram();
        audio.EnqueueControlFrame(new AquariumAudioControlFrame(
            MimirAlignmentActuatorProfile.SixSourceFaust.Id,
            frame.ReferenceSourceId,
            frame.ReferenceHoldbackSamples,
            frame.Commands
                .Select(command => new AquariumAudioControlCommand(
                    command.SourceId,
                    command.TargetDelaySamples,
                    command.ResampleRatio,
                    command.Confidence,
                    ApplyPresentationGain(command)))
                .ToArray(),
            frame.TruncatedSourceCount,
            ++audioActuatorFrameSequence));
        QueueStreamingActuatorAudioBlock(frame, audioActuatorFrameSequence);
    }

    private void QueueAudioActuatorProgram()
    {
        if (audioActuatorProgramQueued)
        {
            return;
        }

        var profile = MimirAlignmentActuatorProfile.SixSourceFaust;
        var path = ResolveRuntimeAssetPath(profile.FaustDspPath);
        if (path == null)
        {
            Console.WriteLine($"Mimir audio actuator DSP not queued; `{profile.FaustDspPath}` was not found.");
            return;
        }

        audio.EnqueueStreamingDspProgram(new AquariumStreamingDspProgram(
            profile.Id,
            "mimir_alignment_actuator",
            File.ReadAllText(path),
            unchecked((int)File.GetLastWriteTimeUtc(path).Ticks),
            OutputStems: Enumerable.Range(0, profile.SourceCount)
                .Select(index => new AquariumStreamingDspOutputStem(
                    index,
                    $"aligned_source_{index}",
                    $"Aligned source {index}"))
                .ToArray()));
        audioActuatorProgramQueued = true;
    }

    private void QueueStreamingActuatorAudioBlock(MimirActuatorFrame frame, long sequence)
    {
        var commands = frame.Commands.OrderBy(command => command.SourceId, StringComparer.Ordinal).ToArray();
        if (commands.Length == 0)
        {
            return;
        }

        var buffers = synchronization.Buffers.Buffers
            .Where(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio && buffer.Latest?.AudioBlock != null)
            .ToDictionary(buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal);
        var channels = new List<AquariumStreamingAudioChannel>(commands.Length);
        var frameCount = int.MaxValue;
        var sampleRate = 0;
        for (var index = 0; index < commands.Length; index++)
        {
            if (!buffers.TryGetValue(commands[index].SourceId, out var buffer) ||
                buffer.Latest is not { } latest ||
                latest.AudioBlock is not { } block ||
                latest.Data.IsEmpty)
            {
                continue;
            }

            if (sampleRate == 0)
            {
                sampleRate = block.SampleRate;
            }
            else if (block.SampleRate != sampleRate)
            {
                continue;
            }

            var samples = ApplyGain(ExtractMonoAudioBlock(latest, block), presentationControls.AudioGain(commands[index].SourceId));
            if (samples.Length == 0)
            {
                continue;
            }

            frameCount = Math.Min(frameCount, samples.Length);
            var channelIndex = FaustSourceIndex(commands[index]);
            if (channelIndex < 0)
            {
                continue;
            }

            channels.Add(new AquariumStreamingAudioChannel(channelIndex, commands[index].SourceId, samples));
        }

        if (channels.Count == 0 || sampleRate <= 0 || frameCount == int.MaxValue)
        {
            return;
        }

        if (channels.Any(channel => channel.Samples.Length != frameCount))
        {
            channels = channels
                .Select(channel => channel.Samples.Length == frameCount
                    ? channel
                    : channel with { Samples = channel.Samples.AsSpan(0, frameCount).ToArray() })
                .ToList();
        }

        audio.EnqueueStreamingAudioBlock(new AquariumStreamingAudioBlock(
            MimirAlignmentActuatorProfile.SixSourceFaust.Id,
            channels,
            frameCount,
            sampleRate,
            sequence));
    }

    private IReadOnlyDictionary<string, float> ApplyPresentationGain(MimirActuatorCommand command)
    {
        var gain = presentationControls.AudioGain(command.SourceId);
        var controls = new Dictionary<string, float>(command.FaustControls, StringComparer.Ordinal);
        foreach (var key in controls.Keys.Where(key => key.EndsWith("/gain", StringComparison.Ordinal)).ToArray())
        {
            controls[key] = Math.Clamp(controls[key] * gain, 0.0f, 2.0f);
        }

        return controls;
    }

    private void UpdateObsStemPublication()
    {
        var consumed = false;
        foreach (var stemFrame in runtimeServices.AudioStems.DrainPublishedFrames())
        {
            obsStemPublication.Consume(stemFrame);
            consumed = true;
        }

        if (consumed)
        {
            lastObsStemPublication = obsStemPublication.Capture();
            obsStemPublisher?.Publish(lastObsStemPublication);
        }
    }

    private void UpdateAudioSpectra()
    {
        if (runtimeSeconds < nextSpectrumSeconds)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        lastAudioSpectra = spectrumAnalyzer.Analyze(synchronization.Buffers.Buffers, audioSyncSettings.ReferenceSourceId);
        if (lastAudioSpectra.Count == 0 && (syntheticSpectrumPreview || syntheticSingleTubePreview))
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
        var started = 0;
        foreach (var factory in sourceFactories)
        {
            var source = factory.Create();
            if (source != null)
            {
                synchronization.AddSource(source);
                started++;
                Console.WriteLine($"mimir-source-started source={factory.Descriptor.SourceId} kind={factory.Descriptor.Kind}");
            }
        }

        Console.WriteLine($"mimir-source-started total={started} configured={sourceFactories.Count}");
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
            $"mimir-sync-telemetry t={runtimeSeconds:0.00}s sources={synchronization.SourceCount} lastPoll={lastPollCount} ingested={synchronization.IngestedSamples} audioSync={audioSyncSettings.Mode} loopbackCount={loopback?.Count ?? 0} loopbackEdgeNs={loopback?.EdgeNs ?? 0} reports={lastAudioSynchronizationReports.Count} states={states.Count} analyzeMs={lastAudioSyncAnalysisMilliseconds:0.0} aligned={DescribeAlignedAudio()}");
        if (lastObsStemPublication.ReadyStems.Count > 0 || lastObsStemPublication.MissingStemIds.Count > 0)
        {
            Console.WriteLine(
                $"mimir-stem-telemetry profile={lastObsStemPublication.ConfigurationId} ready={lastObsStemPublication.ReadyStems.Count} missing={lastObsStemPublication.MissingStemIds.Count} unconfigured={lastObsStemPublication.UnconfiguredStemIds.Count} seq={lastObsStemPublication.LatestSequence}");
        }

        Console.WriteLine($"mimir-sync-buffers {DescribeAudioBuffers()}");
        foreach (var buffer in synchronization.Buffers.Buffers.Where(static buffer => buffer.Descriptor.Kind == MimirStreamKind.Video))
        {
            var frame = buffer.Latest?.VideoFrame;
            Console.WriteLine(
                $"mimir-video-buffer {buffer.Descriptor.SourceId} count={buffer.Count} latest={(frame == null ? "none" : $"{frame.Width}x{frame.Height} {frame.PixelFormat} bytes={buffer.Latest?.ByteLength ?? 0} resource={frame.ResourceKey} handle={frame.NativeHandle} kind={frame.NativeHandleKind} fence={frame.ProducerFenceHandle}/{frame.ProducerFenceValue}")}");
        }
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

        if (lastAudioActuatorFrame.Commands.Count > 0)
        {
            Console.WriteLine(
                $"mimir-audio-actuator reference={lastAudioActuatorFrame.ReferenceSourceId} referenceHoldbackSamples={lastAudioActuatorFrame.ReferenceHoldbackSamples:0.000000} commands={lastAudioActuatorFrame.Commands.Count} truncated={lastAudioActuatorFrame.TruncatedSourceCount}");
            foreach (var command in lastAudioActuatorFrame.Commands.OrderBy(command => command.SourceId, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    $"mimir-audio-actuator-command source={command.SourceId} delaySamples={command.TargetDelaySamples:0.000000} ratio={command.ResampleRatio:0.000000000} confidence={command.Confidence:0.000} controls={command.FaustControls.Count}");
            }
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

    private static MimirObsStemSharedMemoryPublisher? CreateObsStemPublisher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var mapName = Environment.GetEnvironmentVariable("MIMIR_OBS_STEM_MAP");
        try
        {
            return new MimirObsStemSharedMemoryPublisher(string.IsNullOrWhiteSpace(mapName)
                ? MimirObsStemSharedMemoryPublisher.DefaultMapName
                : mapName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mimir OBS stem shared-memory publisher disabled: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveRuntimeAssetPath(string relativePath)
    {
        if (File.Exists(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    private static float[] ExtractMonoAudioBlock(MimirStreamSample sample, MimirAudioBlockDescriptor block)
    {
        var bytesPerSample = BytesPerSample(block.SampleFormat);
        if (bytesPerSample == 0 || block.Channels <= 0 || block.FrameCount <= 0)
        {
            return [];
        }

        var data = sample.Data.Span;
        var stride = block.Channels * bytesPerSample;
        if (data.Length < stride)
        {
            return [];
        }

        var frameCount = Math.Min(block.FrameCount, data.Length / stride);
        var output = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0.0f;
            var frameOffset = frame * stride;
            for (var channel = 0; channel < block.Channels; channel++)
            {
                var offset = frameOffset + channel * bytesPerSample;
                sum += ReadPcmSample(data.Slice(offset, bytesPerSample), block.SampleFormat);
            }

            output[frame] = Math.Clamp(sum / block.Channels, -1.0f, 1.0f);
        }

        return output;
    }

    private static float[] ApplyGain(float[] samples, float gain)
    {
        var safeGain = Math.Clamp(gain, 0.0f, 2.0f);
        if (samples.Length == 0 || Math.Abs(safeGain - 1.0f) < 0.0001f)
        {
            return samples;
        }

        var output = new float[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            output[index] = Math.Clamp(samples[index] * safeGain, -1.0f, 1.0f);
        }

        return output;
    }

    private static int FaustSourceIndex(MimirActuatorCommand command)
    {
        foreach (var key in command.FaustControls.Keys)
        {
            const string prefix = "source";
            const string suffix = "/delay_samples";
            if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
                !key.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var indexText = key[prefix.Length..^suffix.Length];
            if (int.TryParse(indexText, out var index))
            {
                return index;
            }
        }

        return -1;
    }

    private static int BytesPerSample(MimirAudioSampleFormat format) =>
        format switch
        {
            MimirAudioSampleFormat.Float32 => 4,
            MimirAudioSampleFormat.Int16 => 2,
            MimirAudioSampleFormat.Int24 => 3,
            MimirAudioSampleFormat.Int32 => 4,
            _ => 0
        };

    private static float ReadPcmSample(ReadOnlySpan<byte> data, MimirAudioSampleFormat format) =>
        format switch
        {
            MimirAudioSampleFormat.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data)),
            MimirAudioSampleFormat.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data) / 32768.0f,
            MimirAudioSampleFormat.Int24 => ReadInt24LittleEndian(data) / 8388608.0f,
            MimirAudioSampleFormat.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648.0f,
            _ => 0.0f
        };

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> data)
    {
        var value = data[0] | (data[1] << 8) | (data[2] << 16);
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xff000000);
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
        if (IsTruthy(Environment.GetEnvironmentVariable("MIMIR_SYNTHETIC_SINGLE_TUBE_PREVIEW")))
        {
            const int singleTubeBandCount = 2;
            var bands = Enumerable.Repeat(-40.0, singleTubeBandCount).ToArray();
            return
            [
                new MimirAudioSpectrumSnapshot(
                    "preview-tube",
                    "Single Tube Proof",
                    192000,
                    8192,
                    8192,
                    0.24,
                    0.56,
                    -78.0,
                    [],
                    bands,
                    (long)(timeSeconds * 1_000_000_000.0)),
            ];
        }

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
        var commands = lastAudioActuatorFrame.Commands;
        return commands.Count == 0
            ? "no aligned state"
            : $"{commands.Count + 1}ch commands-ready refHold={lastAudioActuatorFrame.ReferenceHoldbackSamples:0.000} samples";
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
