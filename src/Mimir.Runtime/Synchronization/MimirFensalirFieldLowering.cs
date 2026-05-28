using System.Numerics;
using Aquarium.Engine.Render;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirFensalirLoweringOptions(
    double AccumulationWindowSeconds = 5.0,
    double PresentationDelaySeconds = 5.0,
    double DefaultTimingUncertaintyMicroseconds = 1000.0,
    float DefaultSupportRadius = 0.01f);

public sealed class MimirFensalirFieldLowering(MimirFensalirLoweringOptions? options = null)
{
    private readonly MimirFensalirLoweringOptions options = options ?? new();

    public AquariumFieldEvidenceFrame BuildFieldEvidenceFrame(
        IEnumerable<MimirRollingStreamWindow> windows,
        IEnumerable<MimirObservation> observations,
        IEnumerable<MimirCalibrationConstraint> calibrationConstraints,
        IEnumerable<MimirSurfaceIntent> surfaceIntents)
    {
        var observationByKey = observations.ToDictionary(static observation => observation.ObservationKey, StringComparer.Ordinal);
        var domains = new List<AquariumFieldDomain>();
        var claims = new List<AquariumFieldClaim>();
        var candidates = new List<AquariumFieldCandidate>();
        var packets = new List<AquariumFieldBackendPacket>();
        var resources = new List<AquariumFieldResourceDeclaration>();
        var tubeSplineLowerings = new List<AquariumFieldTubeSplineLowering>();
        var seenResources = new HashSet<string>(StringComparer.Ordinal);
        var seenDomains = new HashSet<string>(StringComparer.Ordinal);

        foreach (var window in windows)
        {
            AddDomain(domains, seenDomains, DomainForWindow(window));
            AddResource(resources, seenResources, ResourceForWindow(window));
        }

        foreach (var observation in observations)
        {
            var domain = DomainForObservation(observation);
            AddDomain(domains, seenDomains, domain);
            AddResource(resources, seenResources, ResourceForObservation(observation));

            var claim = ClaimForObservation(observation, domain.DomainKey);
            claims.Add(claim);
            candidates.Add(CandidateForClaim(claim, AquariumFieldGuide.Valid(claim.Confidence)));
        }

        foreach (var constraint in calibrationConstraints)
        {
            var domain = DomainForCalibrationConstraint(constraint);
            AddDomain(domains, seenDomains, domain);

            var claim = ClaimForCalibrationConstraint(constraint, domain.DomainKey);
            claims.Add(claim);
            candidates.Add(CandidateForClaim(claim, AquariumFieldGuide.Valid(claim.Confidence)));
        }

        foreach (var intent in surfaceIntents)
        {
            var domain = DomainForSurfaceIntent(intent);
            AddDomain(domains, seenDomains, domain);

            var claim = ClaimForSurfaceIntent(intent, domain.DomainKey, observationByKey);
            claims.Add(claim);

            var guide = AquariumFieldGuide.Valid(
                claim.Confidence,
                Math.Max(0.0f, (float)intent.SupportPolicy.MaximumAgeSeconds));
            candidates.Add(CandidateForClaim(claim, guide));
            if (TryBuildTubeSplineLowering(intent, claim, resources, out var tubeSplineLowering))
            {
                tubeSplineLowerings.Add(tubeSplineLowering);
            }
        }

        return new AquariumFieldEvidenceFrame
        {
            Domains = domains,
            Claims = claims,
            Candidates = candidates,
            BackendPackets = packets,
            Resources = resources,
            TubeSplineLowerings = tubeSplineLowerings,
            AccumulationWindowSeconds = (float)options.AccumulationWindowSeconds,
            PresentationDelaySeconds = (float)options.PresentationDelaySeconds
        };
    }

    public AquariumFieldEvidenceFrame BuildCameraObservationFrame(IEnumerable<MimirRollingStreamBuffer> buffers)
    {
        var videoBuffers = buffers
            .Where(static buffer => buffer.Descriptor.Kind == MimirStreamKind.Video)
            .ToArray();
        var windows = new List<MimirRollingStreamWindow>(videoBuffers.Length);
        var observations = new List<MimirObservation>(videoBuffers.Length);
        MimirFensalirBridgeMapper.MapWindows(videoBuffers, windows);
        MimirFensalirBridgeMapper.MapLatestObservations(videoBuffers, observations);

        var observationsByWindow = observations.ToDictionary(static observation => observation.WindowId, StringComparer.Ordinal);
        var intents = new List<MimirSurfaceIntent>(windows.Count);
        foreach (var window in windows)
        {
            if (window.Status != MimirBridgeWindowStatus.Live ||
                !window.Payload.HasResource ||
                !observationsByWindow.TryGetValue(window.WindowId, out var observation))
            {
                continue;
            }

            intents.Add(MimirFensalirBridgeMapper.MapDefaultSurfaceIntent(window, observation));
        }

        return BuildFieldEvidenceFrame(windows, observations, [], intents);
    }

    public AquariumFieldEvidenceFrame BuildAcousticSourceCandidateFrame(
        IEnumerable<MimirAcousticSourceFieldCandidate> sourceCandidates)
    {
        var domains = new List<AquariumFieldDomain>();
        var claims = new List<AquariumFieldClaim>();
        var candidates = new List<AquariumFieldCandidate>();
        var seenDomains = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceCandidate in sourceCandidates)
        {
            if (string.IsNullOrWhiteSpace(sourceCandidate.CandidateKey))
            {
                continue;
            }

            var domain = DomainForAcousticSourceCandidate(sourceCandidate);
            AddDomain(domains, seenDomains, domain);

            var claim = ClaimForAcousticSourceCandidate(sourceCandidate, domain.DomainKey);
            claims.Add(claim);
            candidates.Add(CandidateForClaim(claim, AquariumFieldGuide.Valid(claim.Confidence)));
        }

        return new AquariumFieldEvidenceFrame
        {
            Domains = domains,
            Claims = claims,
            Candidates = candidates,
            AccumulationWindowSeconds = (float)options.AccumulationWindowSeconds,
            PresentationDelaySeconds = (float)options.PresentationDelaySeconds
        };
    }

    public AquariumFieldEvidenceFrame BuildVisualMarkerCandidateFrame(
        IEnumerable<MimirVisualMarkerFieldCandidate> markerCandidates)
    {
        var domains = new List<AquariumFieldDomain>();
        var claims = new List<AquariumFieldClaim>();
        var candidates = new List<AquariumFieldCandidate>();
        var seenDomains = new HashSet<string>(StringComparer.Ordinal);

        foreach (var markerCandidate in markerCandidates)
        {
            if (string.IsNullOrWhiteSpace(markerCandidate.CandidateKey))
            {
                continue;
            }

            var domain = DomainForVisualMarkerCandidate(markerCandidate);
            AddDomain(domains, seenDomains, domain);

            var claim = ClaimForVisualMarkerCandidate(markerCandidate, domain.DomainKey);
            claims.Add(claim);
            candidates.Add(CandidateForClaim(claim, AquariumFieldGuide.Valid(claim.Confidence)));
        }

        return new AquariumFieldEvidenceFrame
        {
            Domains = domains,
            Claims = claims,
            Candidates = candidates,
            AccumulationWindowSeconds = (float)options.AccumulationWindowSeconds,
            PresentationDelaySeconds = (float)options.PresentationDelaySeconds
        };
    }

    private static void AddDomain(
        ICollection<AquariumFieldDomain> domains,
        ISet<string> seenDomains,
        AquariumFieldDomain domain)
    {
        if (domain.HasIdentity && seenDomains.Add(domain.DomainKey))
        {
            domains.Add(domain);
        }
    }

    private static void AddResource(
        ICollection<AquariumFieldResourceDeclaration> resources,
        ISet<string> seenResources,
        AquariumFieldResourceDeclaration resource)
    {
        if (resource.HasIdentity && seenResources.Add(resource.ResourceKey))
        {
            resources.Add(resource);
        }
    }

    private static AquariumFieldDomain DomainForWindow(MimirRollingStreamWindow window) =>
        new(
            DomainKeyForWindow(window.WindowId),
            "",
            AquariumFieldDomainKind.RollingBuffer,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            new Vector3(
                Math.Max(1, window.SampleDescriptor.Width),
                Math.Max(1, window.SampleDescriptor.Height),
                Math.Max(0.0f, (float)window.Duration.TotalSeconds)),
            Vector3.Zero,
            "Mimir.Runtime");

    private static AquariumFieldResourceDeclaration ResourceForWindow(MimirRollingStreamWindow window)
    {
        if (!window.Payload.HasResource)
        {
            return default;
        }

        var resourceKey = MimirFensalirBridgeMapper.ResourceKeyForPayload(window.Payload);
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return default;
        }

        var descriptor = window.SampleDescriptor;
        var isVideo = descriptor.Kind == MimirStreamKind.Video;
        var width = isVideo
            ? Math.Max(1, descriptor.Width)
            : Math.Max(1, descriptor.FrameCount);
        var height = isVideo
            ? Math.Max(1, descriptor.Height)
            : Math.Max(1, descriptor.Channels);
        var stride = isVideo
            ? Math.Max(1, descriptor.StrideBytes)
            : BytesPerAudioFrame(descriptor.AudioSampleFormat) * Math.Max(1, descriptor.Channels);
        var count = isVideo
            ? 1
            : Math.Max(1, descriptor.FrameCount * descriptor.Channels);

        return new AquariumFieldResourceDeclaration(
            ResourceKey: resourceKey,
            Kind: ResourceKindForDescriptor(descriptor, window.Payload.NativeHandleKind),
            Residency: ResourceResidencyForHandle(window.Payload.NativeHandleKind),
            Access: ResourceAccessForDescriptor(descriptor),
            Format: ResourceFormatForDescriptor(descriptor),
            Width: width,
            Height: height,
            DepthOrCount: count,
            StrideBytes: stride,
            ValidFromNs: window.WindowStartNs,
            ValidUntilNs: window.EdgeNs,
            Version: window.SequenceId,
            NativeHandle: new IntPtr(unchecked((long)window.Payload.NativeHandle)),
            NativeHandleKind: window.Payload.NativeHandleKind,
            ProducerFenceHandle: new IntPtr(unchecked((long)window.Payload.ProducerFenceHandle)),
            ProducerFenceValue: window.Payload.ProducerFenceValue);
    }

    private static AquariumFieldResourceDeclaration ResourceForObservation(MimirObservation observation)
    {
        if (!observation.Payload.HasResource)
        {
            return default;
        }

        var resourceKey = MimirFensalirBridgeMapper.ResourceKeyForPayload(observation.Payload);
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return default;
        }

        return new AquariumFieldResourceDeclaration(
            ResourceKey: resourceKey,
            Kind: ResourceKindForObservation(observation.Modality, observation.Payload.NativeHandleKind),
            Residency: ResourceResidencyForHandle(observation.Payload.NativeHandleKind),
            Access: AquariumFieldShaderAccess.ShaderResource,
            Format: observation.Modality == MimirObservationModality.Camera ? "native-video" : "native-audio",
            Width: Math.Max(1, observation.Payload.ByteLength),
            Height: 1,
            DepthOrCount: Math.Max(1, observation.Payload.ByteLength),
            StrideBytes: Math.Max(1, observation.Payload.ByteLength),
            ValidFromNs: observation.ObservedTimeNs,
            ValidUntilNs: observation.CanonicalTimeEstimateNs + Math.Max(0, observation.UncertaintyNs),
            Version: observation.Provenance.SequenceId,
            NativeHandle: new IntPtr(unchecked((long)observation.Payload.NativeHandle)),
            NativeHandleKind: observation.Payload.NativeHandleKind,
            ProducerFenceHandle: new IntPtr(unchecked((long)observation.Payload.ProducerFenceHandle)),
            ProducerFenceValue: observation.Payload.ProducerFenceValue);
    }

    private static AquariumFieldDomain DomainForObservation(MimirObservation observation) =>
        new(
            DomainKeyForObservation(observation.ObservationKey),
            DomainKeyForWindow(observation.WindowId),
            DomainKindForObservation(observation.Modality),
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Vector3.Zero,
            "Mimir.Runtime");

    private static AquariumFieldDomain DomainForCalibrationConstraint(MimirCalibrationConstraint constraint) =>
        new(
            DomainKeyForCalibrationConstraint(constraint.ConstraintKey),
            "",
            AquariumFieldDomainKind.AudioPath,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            new Vector3(1.0f, 1.0f, Math.Max(0.0f, (float)constraint.DelayUncertaintyMicroseconds)),
            Vector3.Zero,
            "Mimir.Runtime");

    private static AquariumFieldDomain DomainForAcousticSourceCandidate(MimirAcousticSourceFieldCandidate candidate)
    {
        var radius = Math.Max(0.01f, (float)candidate.RadiusMeters);
        var center = candidate.PositionMeters;
        var boundsRadius = new Vector3(radius);
        return new AquariumFieldDomain(
            DomainKeyForAcousticSourceCandidate(candidate),
            "",
            AquariumFieldDomainKind.AudioPath,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            center - boundsRadius,
            center + boundsRadius,
            Vector3.Zero,
            "Mimir.Runtime");
    }

    private static AquariumFieldDomain DomainForVisualMarkerCandidate(MimirVisualMarkerFieldCandidate candidate)
    {
        var radius = Math.Max(0.001f, (float)candidate.RadiusMeters);
        var center = candidate.PositionMeters;
        var boundsRadius = new Vector3(radius);
        return new AquariumFieldDomain(
            DomainKeyForVisualMarkerCandidate(candidate),
            "",
            AquariumFieldDomainKind.CameraSensor,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            center - boundsRadius,
            center + boundsRadius,
            Vector3.Zero,
            "Mimir.Runtime");
    }

    private static AquariumFieldDomain DomainForSurfaceIntent(MimirSurfaceIntent intent) =>
        new(
            DomainKeyForSurfaceIntent(intent.IntentKey),
            "",
            DomainKindForSurfaceIntent(intent.Domain),
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Vector3.Zero,
            "Mimir.Runtime");

    private AquariumFieldClaim ClaimForObservation(MimirObservation observation, string domainKey)
    {
        var confidence = Clamp01((float)observation.Confidence);
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.SensorObservation,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: 1,
            Seed: StableSeed(observation.ObservationKey));

        return new AquariumFieldClaim(
            ClaimKey: $"observation:{observation.ObservationKey}",
            DomainKey: domainKey,
            ProducerKey: observation.StreamId,
            Layer: AquariumFieldLayer.Form,
            Encoding: EncodingForObservation(observation.Modality),
            Support: SupportForTiming(observation.UncertaintyNs),
            Proposal: proposal,
            PayloadHandle: PayloadHandle(observation.Payload),
            ObservedTimeNs: observation.ObservedTimeNs,
            Confidence: confidence);
    }

    private AquariumFieldClaim ClaimForCalibrationConstraint(MimirCalibrationConstraint constraint, string domainKey)
    {
        var confidence = Clamp01((float)constraint.Confidence);
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.CalibrationConstraint,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: Math.Max(1, constraint.FrequencyResponse.Count),
            Seed: StableSeed(constraint.ConstraintKey));

        return new AquariumFieldClaim(
            ClaimKey: $"calibration:{constraint.ConstraintKey}",
            DomainKey: domainKey,
            ProducerKey: constraint.PathId,
            Layer: AquariumFieldLayer.Form,
            Encoding: EncodingForCalibrationConstraint(constraint.EvidenceKind),
            Support: SupportForCalibrationConstraint(constraint),
            Proposal: proposal,
            PayloadHandle: constraint.UsableBandMask,
            ObservedTimeNs: 0,
            Confidence: confidence);
    }

    private AquariumFieldClaim ClaimForAcousticSourceCandidate(
        MimirAcousticSourceFieldCandidate sourceCandidate,
        string domainKey)
    {
        var confidence = Clamp01((float)sourceCandidate.Confidence);
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.CalibrationConstraint,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: 1,
            Seed: StableSeed(sourceCandidate.CandidateKey));

        return new AquariumFieldClaim(
            ClaimKey: $"acoustic-source:{sourceCandidate.CalibrationId}:{sourceCandidate.SourceId}:{sourceCandidate.CandidateKey}",
            DomainKey: domainKey,
            ProducerKey: sourceCandidate.ProducerKey,
            Layer: AquariumFieldLayer.Form,
            Encoding: AquariumFieldEncoding.Confidence,
            Support: SupportForAcousticSourceCandidate(sourceCandidate),
            Proposal: proposal,
            PayloadHandle: sourceCandidate.CalibrationId,
            ObservedTimeNs: sourceCandidate.ObservedTimeNs,
            Confidence: confidence);
    }

    private AquariumFieldClaim ClaimForVisualMarkerCandidate(
        MimirVisualMarkerFieldCandidate markerCandidate,
        string domainKey)
    {
        var confidence = Clamp01((float)markerCandidate.Confidence);
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.DeterministicStructural,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: Math.Max(1, markerCandidate.SourceObservationKeys.Count),
            Seed: StableSeed(markerCandidate.CandidateKey));

        return new AquariumFieldClaim(
            ClaimKey: $"visual-marker:{markerCandidate.CalibrationId}:{markerCandidate.MarkerId}:{markerCandidate.CandidateKey}",
            DomainKey: domainKey,
            ProducerKey: markerCandidate.ProducerKey,
            Layer: AquariumFieldLayer.Form,
            Encoding: AquariumFieldEncoding.Feature,
            Support: SupportForVisualMarkerCandidate(markerCandidate),
            Proposal: proposal,
            PayloadHandle: markerCandidate.CalibrationId,
            ObservedTimeNs: markerCandidate.ObservedTimeNs,
            Confidence: confidence);
    }

    private AquariumFieldClaim ClaimForSurfaceIntent(
        MimirSurfaceIntent intent,
        string domainKey,
        IReadOnlyDictionary<string, MimirObservation> observationByKey)
    {
        var confidence = Clamp01((float)intent.MaterialGraph.Confidence);
        var proposal = new AquariumFieldProposalPolicy(
            ProposalKindForSurfaceIntent(intent.Purpose),
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: Math.Max(1, intent.SourceObservationKeys.Count),
            Seed: StableSeed(intent.IntentKey));

        return new AquariumFieldClaim(
            ClaimKey: $"intent:{intent.IntentKey}",
            DomainKey: domainKey,
            ProducerKey: intent.MaterialGraph.IntentId,
            Layer: LayerForSurfaceIntent(intent.MaterialGraph.Role),
            Encoding: EncodingForSurfaceIntent(intent.Domain),
            Support: SupportForSurfaceIntent(intent),
            Proposal: proposal,
            PayloadHandle: PayloadHandleForSurfaceIntent(intent, observationByKey),
            ObservedTimeNs: 0,
            Confidence: confidence);
    }

    private static AquariumFieldCandidate CandidateForClaim(AquariumFieldClaim claim, AquariumFieldGuide guide) =>
        new(
            CandidateKey: $"{claim.ClaimKey}:candidate",
            ClaimKey: claim.ClaimKey,
            Layer: claim.Layer,
            Encoding: claim.Encoding,
            Proposal: claim.Proposal,
            Guide: guide);

    private static bool TryBuildTubeSplineLowering(
        MimirSurfaceIntent intent,
        AquariumFieldClaim claim,
        IReadOnlyList<AquariumFieldResourceDeclaration> resources,
        out AquariumFieldTubeSplineLowering lowering)
    {
        lowering = default;
        if (claim.Encoding != AquariumFieldEncoding.Tube ||
            string.IsNullOrWhiteSpace(claim.PayloadHandle))
        {
            return false;
        }

        var resource = resources.FirstOrDefault(resource => string.Equals(resource.ResourceKey, claim.PayloadHandle, StringComparison.Ordinal));
        if (!resource.HasIdentity ||
            resource.Kind is not (AquariumFieldResourceKind.StructuredBuffer or AquariumFieldResourceKind.CurvePointBuffer))
        {
            return false;
        }

        var width = Math.Max(2, resource.Width);
        var height = Math.Max(1, resource.Height);
        var axisStepX = width > 1 ? 10.0f / (width - 1) : 10.0f;
        lowering = new AquariumFieldTubeSplineLowering(
            LoweringKey: $"tube-spline:{intent.IntentKey}",
            ClaimKey: claim.ClaimKey,
            ResourceKey: resource.ResourceKey,
            Width: width,
            Height: height,
            StrideBytes: Math.Max(4, resource.StrideBytes),
            FirstColumn: 0,
            ColumnCount: height,
            ColumnStride: 1,
            RollingModulo: height,
            RollingOffset: 0,
            Origin: new Vector3(-5.0f, 0.0f, 0.0f),
            AxisStep: new Vector3(axisStepX, 0.0f, 0.0f),
            ColumnStep: new Vector3(0.0f, 0.0f, 0.1f),
            AmplitudePower: 2.0f,
            AmplitudeScale: 1.0f,
            NormalizeMin: 0.0f,
            NormalizeMax: 1.0f,
            BaseRadius: 0.012f,
            RadiusScale: 0.030f,
            Alpha: 0.92f,
            Feather: 0.20f,
            RampTexturePath: "",
            RampResourceKey: "",
            EmissionScale: 10.0f,
            CatmullRomSubdivisions: 4).Normalized();
        return true;
    }

    private AquariumFieldSupport SupportForTiming(long uncertaintyNs)
    {
        var radius = Math.Max(options.DefaultSupportRadius, (float)(Math.Max(0, uncertaintyNs) / 1_000_000_000.0));
        return new AquariumFieldSupport(
            Center: Vector3.Zero,
            Radius: new Vector3(options.DefaultSupportRadius, options.DefaultSupportRadius, radius),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: radius,
            ProjectedError: 0.0f,
            Curvature: 0.0f,
            TemporalUncertainty: radius);
    }

    private AquariumFieldSupport SupportForCalibrationConstraint(MimirCalibrationConstraint constraint)
    {
        var uncertaintySeconds = (float)(Math.Max(0.0, constraint.DelayUncertaintyMicroseconds) / 1_000_000.0);
        var radius = Math.Max(options.DefaultSupportRadius, uncertaintySeconds);
        return new AquariumFieldSupport(
            Center: Vector3.Zero,
            Radius: new Vector3(options.DefaultSupportRadius, options.DefaultSupportRadius, radius),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: radius,
            ProjectedError: uncertaintySeconds,
            Curvature: 0.0f,
            TemporalUncertainty: uncertaintySeconds);
    }

    private AquariumFieldSupport SupportForAcousticSourceCandidate(MimirAcousticSourceFieldCandidate sourceCandidate)
    {
        var radius = Math.Max(options.DefaultSupportRadius, (float)sourceCandidate.RadiusMeters);
        return new AquariumFieldSupport(
            Center: sourceCandidate.PositionMeters,
            Radius: new Vector3(radius),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: radius,
            ProjectedError: radius,
            Curvature: 0.0f,
            TemporalUncertainty: 0.0f);
    }

    private AquariumFieldSupport SupportForVisualMarkerCandidate(MimirVisualMarkerFieldCandidate markerCandidate)
    {
        var radius = Math.Max(options.DefaultSupportRadius, (float)markerCandidate.RadiusMeters);
        return new AquariumFieldSupport(
            Center: markerCandidate.PositionMeters,
            Radius: new Vector3(radius),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: radius,
            ProjectedError: radius,
            Curvature: 0.0f,
            TemporalUncertainty: 0.0f);
    }

    private AquariumFieldSupport SupportForSurfaceIntent(MimirSurfaceIntent intent)
    {
        var age = Math.Max(0.0f, (float)intent.SupportPolicy.MaximumAgeSeconds);
        var radius = Math.Max(options.DefaultSupportRadius, age);
        return new AquariumFieldSupport(
            Center: Vector3.Zero,
            Radius: new Vector3(options.DefaultSupportRadius, options.DefaultSupportRadius, radius),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: radius,
            ProjectedError: 0.0f,
            Curvature: 0.0f,
            TemporalUncertainty: age);
    }

    private static AquariumFieldDomainKind DomainKindForObservation(MimirObservationModality modality) =>
        modality switch
        {
            MimirObservationModality.Camera => AquariumFieldDomainKind.CameraSensor,
            MimirObservationModality.Audio => AquariumFieldDomainKind.AudioPath,
            MimirObservationModality.Network => AquariumFieldDomainKind.SensorRig,
            MimirObservationModality.Timing => AquariumFieldDomainKind.AudioPath,
            MimirObservationModality.Response => AquariumFieldDomainKind.AudioPath,
            _ => AquariumFieldDomainKind.Unknown,
        };

    private static AquariumFieldDomainKind DomainKindForSurfaceIntent(MimirSurfaceDomain domain) =>
        domain switch
        {
            MimirSurfaceDomain.CameraImage => AquariumFieldDomainKind.CameraSensor,
            MimirSurfaceDomain.AudioWaveform => AquariumFieldDomainKind.RollingBuffer,
            MimirSurfaceDomain.AudioSpectrum => AquariumFieldDomainKind.RollingBuffer,
            MimirSurfaceDomain.Timing => AquariumFieldDomainKind.AudioPath,
            MimirSurfaceDomain.AcousticResponse => AquariumFieldDomainKind.AudioPath,
            _ => AquariumFieldDomainKind.Unknown,
        };

    private static AquariumFieldEncoding EncodingForObservation(MimirObservationModality modality) =>
        modality switch
        {
            MimirObservationModality.Timing => AquariumFieldEncoding.Phase,
            MimirObservationModality.Response => AquariumFieldEncoding.Confidence,
            _ => AquariumFieldEncoding.Feature,
        };

    private static AquariumFieldEncoding EncodingForCalibrationConstraint(MimirCalibrationEvidenceKind evidenceKind) =>
        evidenceKind switch
        {
            MimirCalibrationEvidenceKind.Loopback or
            MimirCalibrationEvidenceKind.Bioacoustic or
            MimirCalibrationEvidenceKind.Chirplet => AquariumFieldEncoding.Phase,
            MimirCalibrationEvidenceKind.Passive or
            MimirCalibrationEvidenceKind.ComplexContour => AquariumFieldEncoding.Confidence,
            _ => AquariumFieldEncoding.Feature,
        };

    private static AquariumFieldEncoding EncodingForSurfaceIntent(MimirSurfaceDomain domain) =>
        domain switch
        {
            MimirSurfaceDomain.AudioSpectrum or MimirSurfaceDomain.AudioWaveform => AquariumFieldEncoding.Tube,
            MimirSurfaceDomain.Timing => AquariumFieldEncoding.Phase,
            MimirSurfaceDomain.AcousticResponse => AquariumFieldEncoding.Confidence,
            _ => AquariumFieldEncoding.Feature,
        };

    private static AquariumFieldLayer LayerForSurfaceIntent(string role) =>
        role.Contains("material", StringComparison.OrdinalIgnoreCase) ||
        role.Contains("appearance", StringComparison.OrdinalIgnoreCase)
            ? AquariumFieldLayer.Appearance
            : AquariumFieldLayer.Form;

    private static AquariumFieldProposalKind ProposalKindForSurfaceIntent(MimirSurfaceIntentPurpose purpose) =>
        purpose == MimirSurfaceIntentPurpose.Debug
            ? AquariumFieldProposalKind.DebugIntent
            : AquariumFieldProposalKind.DeterministicStructural;

    private static string PayloadHandle(MimirBridgePayloadView payload)
    {
        return MimirFensalirBridgeMapper.ResourceKeyForPayload(payload);
    }

    private static string PayloadHandleForSurfaceIntent(
        MimirSurfaceIntent intent,
        IReadOnlyDictionary<string, MimirObservation> observationByKey)
    {
        foreach (var observationKey in intent.SourceObservationKeys)
        {
            if (observationByKey.TryGetValue(observationKey, out var observation))
            {
                var resourceKey = PayloadHandle(observation.Payload);
                if (!string.IsNullOrWhiteSpace(resourceKey))
                {
                    return resourceKey;
                }
            }
        }

        return intent.SupportPolicy.PolicyId;
    }

    private static AquariumFieldResourceKind ResourceKindForDescriptor(
        MimirBridgeSampleDescriptor descriptor,
        string nativeHandleKind)
    {
        if (descriptor.Kind == MimirStreamKind.Audio)
        {
            return AquariumFieldResourceKind.StructuredBuffer;
        }

        if (nativeHandleKind.Contains("mesh", StringComparison.OrdinalIgnoreCase))
        {
            return AquariumFieldResourceKind.Mesh;
        }

        return nativeHandleKind.Contains("rolling", StringComparison.OrdinalIgnoreCase)
            ? AquariumFieldResourceKind.RollingTexture
            : AquariumFieldResourceKind.Texture2D;
    }

    private static AquariumFieldResourceKind ResourceKindForObservation(
        MimirObservationModality modality,
        string nativeHandleKind)
    {
        if (nativeHandleKind.Contains("curve", StringComparison.OrdinalIgnoreCase) ||
            nativeHandleKind.Contains("spline", StringComparison.OrdinalIgnoreCase))
        {
            return AquariumFieldResourceKind.CurvePointBuffer;
        }

        return modality == MimirObservationModality.Camera
            ? AquariumFieldResourceKind.Texture2D
            : AquariumFieldResourceKind.StructuredBuffer;
    }

    private static AquariumFieldResourceResidency ResourceResidencyForHandle(string nativeHandleKind) =>
        nativeHandleKind.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            ? AquariumFieldResourceResidency.CpuVisible
            : AquariumFieldResourceResidency.SharedGpu;

    private static AquariumFieldShaderAccess ResourceAccessForDescriptor(MimirBridgeSampleDescriptor descriptor) =>
        descriptor.Kind == MimirStreamKind.Video
            ? AquariumFieldShaderAccess.ShaderResource
            : AquariumFieldShaderAccess.ShaderResource;

    private static string ResourceFormatForDescriptor(MimirBridgeSampleDescriptor descriptor) =>
        descriptor.Kind == MimirStreamKind.Video
            ? descriptor.PixelFormat.ToString()
            : descriptor.AudioSampleFormat.ToString();

    private static int BytesPerAudioFrame(MimirAudioSampleFormat format) =>
        format switch
        {
            MimirAudioSampleFormat.Float32 or MimirAudioSampleFormat.Int32 => 4,
            MimirAudioSampleFormat.Int24 => 3,
            MimirAudioSampleFormat.Int16 => 2,
            _ => 1,
        };

    private static string DomainKeyForWindow(string windowId) => $"mimir:window:{windowId}";

    private static string DomainKeyForObservation(string observationKey) => $"mimir:observation:{observationKey}";

    private static string DomainKeyForCalibrationConstraint(string constraintKey) => $"mimir:calibration:{constraintKey}";

    private static string DomainKeyForAcousticSourceCandidate(MimirAcousticSourceFieldCandidate candidate) =>
        $"mimir:acoustic-source:{candidate.CalibrationId}:{candidate.SourceId}:{candidate.CandidateKey}";

    private static string DomainKeyForVisualMarkerCandidate(MimirVisualMarkerFieldCandidate candidate) =>
        $"mimir:visual-marker:{candidate.CalibrationId}:{candidate.MarkerId}:{candidate.CandidateKey}";

    private static string DomainKeyForSurfaceIntent(string intentKey) => $"mimir:intent:{intentKey}";

    private static float Clamp01(float value) => Math.Clamp(value, 0.0f, 1.0f);

    private static uint StableSeed(string key)
    {
        const uint fnvPrime = 16777619u;
        var hash = 2166136261u;
        foreach (var character in key)
        {
            hash ^= character;
            hash *= fnvPrime;
        }

        return hash == 0 ? 1u : hash;
    }
}
