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
        IEnumerable<MimirSurfaceIntent> surfaceIntents,
        bool includeObservationClaims = true,
        bool includeCalibrationClaims = true)
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

        if (includeObservationClaims)
        {
            foreach (var observation in observations)
            {
                var domain = DomainForObservation(observation);
                AddDomain(domains, seenDomains, domain);
                AddResource(resources, seenResources, ResourceForObservation(observation));

                var claim = ClaimForObservation(observation, domain.DomainKey);
                claims.Add(claim);
                candidates.Add(CandidateForClaim(claim, AquariumFieldGuide.Valid(claim.Confidence)));
            }
        }

        if (includeCalibrationClaims)
        {
            foreach (var constraint in calibrationConstraints)
            {
                var domain = DomainForCalibrationConstraint(constraint);
                AddDomain(domains, seenDomains, domain);

                var claim = ClaimForCalibrationConstraint(constraint, domain.DomainKey);
                claims.Add(claim);
                candidates.Add(CandidateForClaim(claim, AquariumFieldGuide.Valid(claim.Confidence)));
            }
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

    public AquariumFieldEvidenceFrame BuildLedSplineCandidateFrame(
        IEnumerable<MimirLedSplineFieldCandidate> splineCandidates)
    {
        var domains = new List<AquariumFieldDomain>();
        var claims = new List<AquariumFieldClaim>();
        var candidates = new List<AquariumFieldCandidate>();
        var seenDomains = new HashSet<string>(StringComparer.Ordinal);

        foreach (var splineCandidate in splineCandidates)
        {
            if (string.IsNullOrWhiteSpace(splineCandidate.CandidateKey) ||
                splineCandidate.CameraObservations.Count == 0)
            {
                continue;
            }

            var domain = DomainForLedSplineCandidate(splineCandidate);
            AddDomain(domains, seenDomains, domain);

            var claim = ClaimForLedSplineCandidate(splineCandidate, domain.DomainKey);
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

    public AquariumFieldEvidenceFrame BuildFeatureTrackCandidateFrame(
        IEnumerable<MimirFeatureTrackFieldCandidate> trackCandidates)
    {
        var domains = new List<AquariumFieldDomain>();
        var claims = new List<AquariumFieldClaim>();
        var candidates = new List<AquariumFieldCandidate>();
        var seenDomains = new HashSet<string>(StringComparer.Ordinal);

        foreach (var trackCandidate in trackCandidates)
        {
            if (string.IsNullOrWhiteSpace(trackCandidate.CandidateKey) ||
                trackCandidate.CameraObservations.Count == 0)
            {
                continue;
            }

            var domain = DomainForFeatureTrackCandidate(trackCandidate);
            AddDomain(domains, seenDomains, domain);

            var claim = ClaimForFeatureTrackCandidate(trackCandidate, domain.DomainKey);
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

    public AquariumFieldEvidenceFrame BuildLeapPackedStereoDepthCandidateFrame(
        IEnumerable<MimirRollingStreamWindow> windows,
        IEnumerable<AquariumFieldResourceDeclaration> inputResources)
    {
        var resourcesByKey = inputResources
            .Where(static resource => resource.HasIdentity)
            .GroupBy(static resource => resource.ResourceKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var leapWindow = windows
            .Where(static window =>
                window.Status == MimirBridgeWindowStatus.Live &&
                window.SourceKind == MimirStreamKind.Video &&
                window.SampleDescriptor.PixelFormat == MimirVideoPixelFormat.LeapStereoIr &&
                window.Payload.HasResource &&
                !string.IsNullOrWhiteSpace(window.Payload.ResourceKey))
            .OrderByDescending(static window => window.EdgeNs)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(leapWindow.WindowId))
        {
            return AquariumFieldEvidenceFrame.Empty;
        }

        var packedResourceKey = MimirFensalirBridgeMapper.ResourceKeyForPayload(leapWindow.Payload);
        if (!resourcesByKey.TryGetValue(packedResourceKey, out var packedInput))
        {
            return AquariumFieldEvidenceFrame.Empty;
        }

        var profile = MimirStereoDepthConfigurations.D3D12SgmLibSgmProvenance;
        var sourceSegment = SanitizeResourceSegment(leapWindow.StreamId);
        var candidate = BuildLeapPackedStereoDepthCandidate(leapWindow, packedResourceKey, profile, sourceSegment);
        return BuildStereoDepthCandidateFrame([candidate], [packedInput]);
    }

    public AquariumFieldEvidenceFrame BuildLeapPackedStereoPointCloudCandidateFrame(
        IEnumerable<MimirRollingStreamWindow> windows)
    {
        var leapWindow = LatestLiveLeapPackedStereoWindow(windows);
        if (string.IsNullOrWhiteSpace(leapWindow.WindowId))
        {
            return AquariumFieldEvidenceFrame.Empty;
        }

        var packedResourceKey = MimirFensalirBridgeMapper.ResourceKeyForPayload(leapWindow.Payload);
        if (string.IsNullOrWhiteSpace(packedResourceKey))
        {
            return AquariumFieldEvidenceFrame.Empty;
        }

        var stereoProfile = MimirStereoDepthConfigurations.D3D12SgmLibSgmProvenance;
        var sourceSegment = SanitizeResourceSegment(leapWindow.StreamId);
        var depthCandidate = BuildLeapPackedStereoDepthCandidate(leapWindow, packedResourceKey, stereoProfile, sourceSegment);
        var pointCloudProfile = MimirPointCloudConfigurations.LeapDisparityPointCloudRoot;
        var stride = Math.Max(1, pointCloudProfile.DefaultSampleStride);
        var width = Math.Max(1, depthCandidate.Width);
        var height = Math.Max(1, depthCandidate.Height);
        var sampledWidth = Math.Max(1, (width + stride - 1) / stride);
        var sampledHeight = Math.Max(1, (height + stride - 1) / stride);
        var candidate = new MimirPointCloudFieldCandidate(
            CandidateKey: $"live-{sourceSegment}-point-cloud-{leapWindow.SequenceId}",
            CalibrationId: depthCandidate.CalibrationId,
            CameraRigId: depthCandidate.CameraPairId,
            ProducerKey: pointCloudProfile.Id,
            ProfileId: pointCloudProfile.Id,
            SourceDisparityResourceKey: depthCandidate.DisparityResourceKey,
            SourceConfidenceResourceKey: depthCandidate.ConfidenceResourceKey,
            PointCloudResourceKey: $"mimir:resource:point-cloud:{sourceSegment}:leap-points",
            Width: width,
            Height: height,
            SampleStride: stride,
            MaxPointCount: sampledWidth * sampledHeight,
            BaselineMeters: pointCloudProfile.BaselineMeters,
            FocalLengthPixels: pointCloudProfile.FocalLengthPixels,
            PrincipalPointX: Math.Clamp(pointCloudProfile.PrincipalPointX, 0.0, width),
            PrincipalPointY: Math.Clamp(pointCloudProfile.PrincipalPointY, 0.0, height),
            MinDepthMeters: depthCandidate.MinDepthMeters,
            MaxDepthMeters: depthCandidate.MaxDepthMeters,
            Confidence: Math.Min(depthCandidate.Confidence, 0.45),
            ObservedTimeNs: depthCandidate.ObservedTimeNs);

        return BuildPointCloudCandidateFrame([candidate]);
    }

    public AquariumFieldEvidenceFrame BuildStereoDepthCandidateFrame(
        IEnumerable<MimirStereoDepthFieldCandidate> depthCandidates,
        IEnumerable<AquariumFieldResourceDeclaration>? inputResources = null)
    {
        var domains = new List<AquariumFieldDomain>();
        var claims = new List<AquariumFieldClaim>();
        var candidates = new List<AquariumFieldCandidate>();
        var resources = new List<AquariumFieldResourceDeclaration>();
        var stereoDepthLowerings = new List<AquariumFieldStereoDepthLowering>();
        var seenDomains = new HashSet<string>(StringComparer.Ordinal);
        var seenResources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var inputResource in inputResources ?? [])
        {
            AddResource(resources, seenResources, inputResource);
        }

        foreach (var depthCandidate in depthCandidates)
        {
            if (string.IsNullOrWhiteSpace(depthCandidate.CandidateKey) ||
                string.IsNullOrWhiteSpace(depthCandidate.DisparityResourceKey))
            {
                continue;
            }

            var domain = DomainForStereoDepthCandidate(depthCandidate);
            AddDomain(domains, seenDomains, domain);
            AddResource(resources, seenResources, DisparityResourceForStereoDepthCandidate(depthCandidate));
            AddResource(resources, seenResources, ConfidenceResourceForStereoDepthCandidate(depthCandidate));

            var claim = ClaimForStereoDepthCandidate(depthCandidate, domain.DomainKey);
            claims.Add(claim);
            candidates.Add(CandidateForClaim(claim, AquariumFieldGuide.Valid(claim.Confidence)));
            stereoDepthLowerings.Add(StereoDepthLoweringForCandidate(depthCandidate, claim.ClaimKey));
        }

        return new AquariumFieldEvidenceFrame
        {
            Domains = domains,
            Claims = claims,
            Candidates = candidates,
            Resources = resources,
            StereoDepthLowerings = stereoDepthLowerings,
            AccumulationWindowSeconds = (float)options.AccumulationWindowSeconds,
            PresentationDelaySeconds = (float)options.PresentationDelaySeconds
        };
    }

    public AquariumFieldEvidenceFrame BuildPointCloudCandidateFrame(
        IEnumerable<MimirPointCloudFieldCandidate> pointCloudCandidates)
    {
        var domains = new List<AquariumFieldDomain>();
        var claims = new List<AquariumFieldClaim>();
        var candidates = new List<AquariumFieldCandidate>();
        var resources = new List<AquariumFieldResourceDeclaration>();
        var seenDomains = new HashSet<string>(StringComparer.Ordinal);
        var seenResources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pointCloudCandidate in pointCloudCandidates)
        {
            if (string.IsNullOrWhiteSpace(pointCloudCandidate.CandidateKey) ||
                string.IsNullOrWhiteSpace(pointCloudCandidate.PointCloudResourceKey) ||
                string.IsNullOrWhiteSpace(pointCloudCandidate.SourceDisparityResourceKey))
            {
                continue;
            }

            var domain = DomainForPointCloudCandidate(pointCloudCandidate);
            AddDomain(domains, seenDomains, domain);
            AddResource(resources, seenResources, PointCloudResourceForCandidate(pointCloudCandidate));

            var claim = ClaimForPointCloudCandidate(pointCloudCandidate, domain.DomainKey);
            claims.Add(claim);
            candidates.Add(CandidateForClaim(claim, AquariumFieldGuide.Valid(claim.Confidence)));
        }

        return new AquariumFieldEvidenceFrame
        {
            Domains = domains,
            Claims = claims,
            Candidates = candidates,
            Resources = resources,
            AccumulationWindowSeconds = (float)options.AccumulationWindowSeconds,
            PresentationDelaySeconds = (float)options.PresentationDelaySeconds
        };
    }

    private static MimirRollingStreamWindow LatestLiveLeapPackedStereoWindow(
        IEnumerable<MimirRollingStreamWindow> windows) =>
        windows
            .Where(static window =>
                window.Status == MimirBridgeWindowStatus.Live &&
                window.SourceKind == MimirStreamKind.Video &&
                window.SampleDescriptor.PixelFormat == MimirVideoPixelFormat.LeapStereoIr &&
                window.Payload.HasResource &&
                !string.IsNullOrWhiteSpace(window.Payload.ResourceKey))
            .OrderByDescending(static window => window.EdgeNs)
            .FirstOrDefault();

    private static MimirStereoDepthFieldCandidate BuildLeapPackedStereoDepthCandidate(
        MimirRollingStreamWindow leapWindow,
        string packedResourceKey,
        MimirStereoDepthKernelProfile profile,
        string sourceSegment) =>
        new(
            CandidateKey: $"live-{sourceSegment}-{leapWindow.SequenceId}",
            CalibrationId: "leapuvc-packed-stereo-ir-calibration-pending",
            CameraPairId: leapWindow.StreamId,
            ProducerKey: profile.Id,
            ProfileId: profile.Id,
            LeftObservationKey: $"{leapWindow.WindowId}:left-ir-luma",
            RightObservationKey: $"{leapWindow.WindowId}:right-ir-luma",
            LeftResourceKey: packedResourceKey,
            RightResourceKey: packedResourceKey,
            DisparityResourceKey: $"mimir:resource:stereo-depth:{sourceSegment}:disparity-r16f",
            ConfidenceResourceKey: $"mimir:resource:stereo-depth:{sourceSegment}:confidence-r8",
            Width: Math.Max(1, leapWindow.SampleDescriptor.Width / 2),
            Height: Math.Max(1, leapWindow.SampleDescriptor.Height),
            MinDisparity: profile.MinDisparity,
            DisparityLevels: profile.DisparityLevels,
            AggregationPathCount: profile.AggregationPathCount,
            CensusRadius: profile.CensusRadius,
            SmoothnessPenaltySmall: profile.SmoothnessPenaltySmall,
            SmoothnessPenaltyLarge: profile.SmoothnessPenaltyLarge,
            MinDepthMeters: 0.20,
            MaxDepthMeters: 4.0,
            Confidence: 0.50,
            ObservedTimeNs: leapWindow.EdgeNs);

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

    private static AquariumFieldDomain DomainForLedSplineCandidate(MimirLedSplineFieldCandidate candidate)
    {
        var points = candidate.CameraObservations.SelectMany(static observation => observation.Points).ToArray();
        var maxWidth = Math.Max(1, candidate.CameraObservations.Max(static observation => observation.Width));
        var maxHeight = Math.Max(1, candidate.CameraObservations.Max(static observation => observation.Height));
        var minX = points.Length == 0 ? 0.0f : Math.Clamp((float)points.Min(static point => point.ImageX), 0.0f, maxWidth);
        var minY = points.Length == 0 ? 0.0f : Math.Clamp((float)points.Min(static point => point.ImageY), 0.0f, maxHeight);
        var maxX = points.Length == 0 ? maxWidth : Math.Clamp((float)points.Max(static point => point.ImageX), minX, maxWidth);
        var maxY = points.Length == 0 ? maxHeight : Math.Clamp((float)points.Max(static point => point.ImageY), minY, maxHeight);
        return new AquariumFieldDomain(
            DomainKeyForLedSplineCandidate(candidate),
            "",
            AquariumFieldDomainKind.CameraSensor,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            new Vector3(minX, minY, 0.0f),
            new Vector3(maxX, maxY, Math.Max(1, candidate.CameraObservations.Count)),
            Vector3.Zero,
            "Mimir.Runtime");
    }

    private static AquariumFieldDomain DomainForFeatureTrackCandidate(MimirFeatureTrackFieldCandidate candidate)
    {
        var tracks = candidate.CameraObservations.SelectMany(static observation => observation.Tracks).ToArray();
        var maxWidth = Math.Max(1, candidate.CameraObservations.Max(static observation => observation.Width));
        var maxHeight = Math.Max(1, candidate.CameraObservations.Max(static observation => observation.Height));
        var minX = tracks.Length == 0 ? 0.0f : Math.Clamp((float)tracks.Min(static track => track.ImageX), 0.0f, maxWidth);
        var minY = tracks.Length == 0 ? 0.0f : Math.Clamp((float)tracks.Min(static track => track.ImageY), 0.0f, maxHeight);
        var maxX = tracks.Length == 0 ? maxWidth : Math.Clamp((float)tracks.Max(static track => track.ImageX), minX, maxWidth);
        var maxY = tracks.Length == 0 ? maxHeight : Math.Clamp((float)tracks.Max(static track => track.ImageY), minY, maxHeight);
        return new AquariumFieldDomain(
            DomainKeyForFeatureTrackCandidate(candidate),
            "",
            AquariumFieldDomainKind.CameraSensor,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            new Vector3(minX, minY, 0.0f),
            new Vector3(maxX, maxY, Math.Max(1, candidate.CameraObservations.Count)),
            Vector3.Zero,
            "Mimir.Runtime");
    }

    private static AquariumFieldDomain DomainForStereoDepthCandidate(MimirStereoDepthFieldCandidate candidate)
    {
        var width = Math.Max(1, candidate.Width);
        var height = Math.Max(1, candidate.Height);
        var maxDepth = Math.Max(candidate.MinDepthMeters, candidate.MaxDepthMeters);
        var depthExtent = Math.Max(0.001f, (float)maxDepth);
        return new AquariumFieldDomain(
            DomainKeyForStereoDepthCandidate(candidate),
            "",
            AquariumFieldDomainKind.Surface2D,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            new Vector3(width, height, depthExtent),
            Vector3.Zero,
            "Fensalir D3D12 compute");
    }

    private static AquariumFieldDomain DomainForPointCloudCandidate(MimirPointCloudFieldCandidate candidate)
    {
        var maxDepth = Math.Max(candidate.MinDepthMeters, candidate.MaxDepthMeters);
        var halfWidthMeters = (float)Math.Max(0.01, maxDepth * candidate.Width / Math.Max(1.0, candidate.FocalLengthPixels) * 0.5);
        var halfHeightMeters = (float)Math.Max(0.01, maxDepth * candidate.Height / Math.Max(1.0, candidate.FocalLengthPixels) * 0.5);
        var minDepth = (float)Math.Max(0.0, candidate.MinDepthMeters);
        var maxDepthFloat = (float)Math.Max(minDepth + 0.001, maxDepth);
        return new AquariumFieldDomain(
            DomainKeyForPointCloudCandidate(candidate),
            DomainKeyForStereoDepthCandidate(candidate.CalibrationId, candidate.CameraRigId, candidate.SourceDisparityResourceKey),
            AquariumFieldDomainKind.Object3D,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            new Vector3(-halfWidthMeters, -halfHeightMeters, minDepth),
            new Vector3(halfWidthMeters, halfHeightMeters, maxDepthFloat),
            Vector3.Zero,
            "Fensalir D3D12 compute");
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

    private AquariumFieldClaim ClaimForLedSplineCandidate(
        MimirLedSplineFieldCandidate splineCandidate,
        string domainKey)
    {
        var confidence = Clamp01((float)splineCandidate.Confidence);
        var represented = Math.Max(1, splineCandidate.CameraObservations.Sum(static observation => observation.Points.Count));
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.DeterministicStructural,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: represented,
            Seed: StableSeed(splineCandidate.CandidateKey));
        var indexState = splineCandidate.HasStableLedIndices ? "indexed" : "unindexed";
        var codeState = splineCandidate.HasTemporalCode ? "coded" : "uncoded";

        return new AquariumFieldClaim(
            ClaimKey: $"led-spline:{splineCandidate.CalibrationId}:{splineCandidate.SplineId}:{splineCandidate.CandidateKey}",
            DomainKey: domainKey,
            ProducerKey: splineCandidate.ProducerKey,
            Layer: AquariumFieldLayer.Form,
            Encoding: AquariumFieldEncoding.Feature,
            Support: SupportForLedSplineCandidate(splineCandidate),
            Proposal: proposal,
            PayloadHandle: $"{splineCandidate.CalibrationId}:{splineCandidate.SplineId}:{indexState}:{codeState}",
            ObservedTimeNs: splineCandidate.ObservedTimeNs,
            Confidence: confidence);
    }

    private AquariumFieldClaim ClaimForFeatureTrackCandidate(
        MimirFeatureTrackFieldCandidate trackCandidate,
        string domainKey)
    {
        var confidence = Clamp01((float)trackCandidate.Confidence);
        var represented = Math.Max(1, trackCandidate.CameraObservations.Sum(static observation => observation.Tracks.Count));
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.SensorObservation,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: represented,
            Seed: StableSeed(trackCandidate.CandidateKey));

        return new AquariumFieldClaim(
            ClaimKey: $"feature-tracks:{trackCandidate.CalibrationId}:{trackCandidate.CandidateKey}",
            DomainKey: domainKey,
            ProducerKey: trackCandidate.ProducerKey,
            Layer: AquariumFieldLayer.Form,
            Encoding: AquariumFieldEncoding.Feature,
            Support: SupportForFeatureTrackCandidate(trackCandidate),
            Proposal: proposal,
            PayloadHandle: $"{trackCandidate.CalibrationId}:stable={trackCandidate.StableTrackCount}",
            ObservedTimeNs: trackCandidate.ObservedTimeNs,
            Confidence: confidence);
    }

    private AquariumFieldClaim ClaimForStereoDepthCandidate(
        MimirStereoDepthFieldCandidate depthCandidate,
        string domainKey)
    {
        var confidence = Clamp01((float)depthCandidate.Confidence);
        var represented = Math.Max(1, depthCandidate.Width * depthCandidate.Height);
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.DeterministicStructural,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: represented,
            Seed: StableSeed(depthCandidate.CandidateKey));

        return new AquariumFieldClaim(
            ClaimKey: $"stereo-depth:{depthCandidate.CalibrationId}:{depthCandidate.CameraPairId}:{depthCandidate.CandidateKey}",
            DomainKey: domainKey,
            ProducerKey: depthCandidate.ProducerKey,
            Layer: AquariumFieldLayer.Form,
            Encoding: AquariumFieldEncoding.Height,
            Support: SupportForStereoDepthCandidate(depthCandidate),
            Proposal: proposal,
            PayloadHandle: depthCandidate.DisparityResourceKey,
            ObservedTimeNs: depthCandidate.ObservedTimeNs,
            Confidence: confidence);
    }

    private AquariumFieldClaim ClaimForPointCloudCandidate(
        MimirPointCloudFieldCandidate pointCloudCandidate,
        string domainKey)
    {
        var confidence = Clamp01((float)pointCloudCandidate.Confidence);
        var represented = Math.Max(1, pointCloudCandidate.MaxPointCount);
        var proposal = new AquariumFieldProposalPolicy(
            AquariumFieldProposalKind.DeterministicStructural,
            SourcePdf: 1.0f,
            TargetContribution: confidence,
            RepresentedCandidateCount: represented,
            Seed: StableSeed(pointCloudCandidate.CandidateKey));

        return new AquariumFieldClaim(
            ClaimKey: $"point-cloud:{pointCloudCandidate.CalibrationId}:{pointCloudCandidate.CameraRigId}:{pointCloudCandidate.CandidateKey}",
            DomainKey: domainKey,
            ProducerKey: pointCloudCandidate.ProducerKey,
            Layer: AquariumFieldLayer.Form,
            Encoding: AquariumFieldEncoding.Mesh,
            Support: SupportForPointCloudCandidate(pointCloudCandidate),
            Proposal: proposal,
            PayloadHandle: pointCloudCandidate.PointCloudResourceKey,
            ObservedTimeNs: pointCloudCandidate.ObservedTimeNs,
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

    private AquariumFieldSupport SupportForLedSplineCandidate(MimirLedSplineFieldCandidate splineCandidate)
    {
        var points = splineCandidate.CameraObservations.SelectMany(static observation => observation.Points).ToArray();
        var radiusPixels = points.Length == 0
            ? 1.0f
            : Math.Max(1.0f, (float)points.Average(static point => Math.Max(point.RadiusPixels, 0.0)));
        var pointConfidence = points.Length == 0
            ? 0.0f
            : (float)points.Average(static point => Math.Clamp(point.Confidence, 0.0, 1.0));
        var length = Math.Max(radiusPixels, (float)splineCandidate.CurveLengthPixels);
        var correspondencePenalty = splineCandidate.HasStableLedIndices ? 0.25f : 1.0f;
        return new AquariumFieldSupport(
            Center: new Vector3(length * 0.5f, 0.0f, Math.Max(1, splineCandidate.CameraObservations.Count)),
            Radius: new Vector3(length * 0.5f, radiusPixels, Math.Max(1.0f, splineCandidate.CameraObservations.Count * 0.5f)),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: Math.Max(radiusPixels, length * 0.5f),
            ProjectedError: correspondencePenalty * (1.0f - pointConfidence),
            Curvature: points.Length >= 3 ? 1.0f / Math.Max(1.0f, length) : 0.0f,
            TemporalUncertainty: splineCandidate.HasTemporalCode ? 0.0f : 1.0f / 187.0f);
    }

    private AquariumFieldSupport SupportForFeatureTrackCandidate(MimirFeatureTrackFieldCandidate trackCandidate)
    {
        var tracks = trackCandidate.CameraObservations.SelectMany(static observation => observation.Tracks).ToArray();
        var radiusPixels = tracks.Length == 0
            ? 1.0f
            : Math.Max(1.0f, (float)(tracks.Average(static track => Math.Max(1, track.AgeFrames)) * 0.5));
        var meanSpeed = Math.Max(0.0f, (float)trackCandidate.MeanSpeedPixelsPerSecond);
        var confidence = tracks.Length == 0
            ? 0.0f
            : (float)tracks.Average(static track => Math.Clamp(track.Confidence, 0.0, 1.0));
        return new AquariumFieldSupport(
            Center: new Vector3(0.0f, 0.0f, Math.Max(1, trackCandidate.CameraObservations.Count)),
            Radius: new Vector3(radiusPixels, radiusPixels, Math.Max(1.0f, trackCandidate.CameraObservations.Count * 0.5f)),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: Math.Max(radiusPixels, meanSpeed / 187.0f),
            ProjectedError: 1.0f - confidence,
            Curvature: 0.0f,
            TemporalUncertainty: 1.0f / 187.0f);
    }

    private AquariumFieldSupport SupportForStereoDepthCandidate(MimirStereoDepthFieldCandidate depthCandidate)
    {
        var width = Math.Max(1.0f, depthCandidate.Width);
        var height = Math.Max(1.0f, depthCandidate.Height);
        var depthRange = Math.Max(0.001f, (float)(depthCandidate.MaxDepthMeters - depthCandidate.MinDepthMeters));
        return new AquariumFieldSupport(
            Center: new Vector3(width * 0.5f, height * 0.5f, depthRange * 0.5f),
            Radius: new Vector3(width * 0.5f, height * 0.5f, depthRange * 0.5f),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: MathF.Max(width, height) * 0.5f,
            ProjectedError: 1.0f / Math.Max(1, depthCandidate.DisparityLevels),
            Curvature: 0.0f,
            TemporalUncertainty: 0.0f);
    }

    private static AquariumFieldSupport SupportForPointCloudCandidate(MimirPointCloudFieldCandidate pointCloudCandidate)
    {
        var maxDepth = Math.Max(pointCloudCandidate.MinDepthMeters, pointCloudCandidate.MaxDepthMeters);
        var halfWidthMeters = (float)Math.Max(0.01, maxDepth * pointCloudCandidate.Width / Math.Max(1.0, pointCloudCandidate.FocalLengthPixels) * 0.5);
        var halfHeightMeters = (float)Math.Max(0.01, maxDepth * pointCloudCandidate.Height / Math.Max(1.0, pointCloudCandidate.FocalLengthPixels) * 0.5);
        var depthRange = Math.Max(0.001f, (float)(pointCloudCandidate.MaxDepthMeters - pointCloudCandidate.MinDepthMeters));
        return new AquariumFieldSupport(
            Center: new Vector3(0.0f, 0.0f, (float)(pointCloudCandidate.MinDepthMeters + pointCloudCandidate.MaxDepthMeters) * 0.5f),
            Radius: new Vector3(halfWidthMeters, halfHeightMeters, depthRange * 0.5f),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: MathF.Max(MathF.Max(halfWidthMeters, halfHeightMeters), depthRange * 0.5f),
            ProjectedError: 1.0f / Math.Max(1, pointCloudCandidate.SampleStride),
            Curvature: 0.0f,
            TemporalUncertainty: 0.0f);
    }

    private static AquariumFieldResourceDeclaration DisparityResourceForStereoDepthCandidate(
        MimirStereoDepthFieldCandidate depthCandidate) =>
        new(
            ResourceKey: depthCandidate.DisparityResourceKey,
            Kind: AquariumFieldResourceKind.SurfacePage,
            Residency: AquariumFieldResourceResidency.GpuResident,
            Access: AquariumFieldShaderAccess.UnorderedAccess,
            Format: "R16Float",
            Width: Math.Max(1, depthCandidate.Width),
            Height: Math.Max(1, depthCandidate.Height),
            DepthOrCount: 1,
            StrideBytes: 2,
            ValidFromNs: depthCandidate.ObservedTimeNs,
            ValidUntilNs: depthCandidate.ObservedTimeNs,
            Version: checked((ulong)Math.Max(0L, depthCandidate.ObservedTimeNs)),
            NativeHandle: IntPtr.Zero,
            NativeHandleKind: "fensalir-stereo-depth-disparity");

    private static AquariumFieldResourceDeclaration ConfidenceResourceForStereoDepthCandidate(
        MimirStereoDepthFieldCandidate depthCandidate)
    {
        if (string.IsNullOrWhiteSpace(depthCandidate.ConfidenceResourceKey))
        {
            return default;
        }

        return new AquariumFieldResourceDeclaration(
            ResourceKey: depthCandidate.ConfidenceResourceKey,
            Kind: AquariumFieldResourceKind.Texture2D,
            Residency: AquariumFieldResourceResidency.GpuResident,
            Access: AquariumFieldShaderAccess.ShaderResource,
            Format: "R8_UNorm",
            Width: Math.Max(1, depthCandidate.Width),
            Height: Math.Max(1, depthCandidate.Height),
            DepthOrCount: 1,
            StrideBytes: 1,
            ValidFromNs: depthCandidate.ObservedTimeNs,
            ValidUntilNs: depthCandidate.ObservedTimeNs,
            Version: checked((ulong)Math.Max(0L, depthCandidate.ObservedTimeNs)),
            NativeHandle: IntPtr.Zero,
            NativeHandleKind: "fensalir-stereo-depth-confidence");
    }

    private static AquariumFieldResourceDeclaration PointCloudResourceForCandidate(
        MimirPointCloudFieldCandidate pointCloudCandidate)
    {
        var pointCount = Math.Max(1, pointCloudCandidate.MaxPointCount);
        var mesh = new AquariumFieldMeshResource(
            Vertices: new AquariumFieldMeshBuffer(
                BufferKey: $"{pointCloudCandidate.PointCloudResourceKey}:vertices",
                Count: pointCount,
                StrideBytes: AquariumFieldMeshResource.PositionNormalUvColorStrideBytes,
                NativeHandle: IntPtr.Zero,
                NativeHandleKind: "fensalir-generated-vertex-buffer"),
            Indices: new AquariumFieldMeshBuffer(
                BufferKey: $"{pointCloudCandidate.PointCloudResourceKey}:indices",
                Count: pointCount,
                StrideBytes: sizeof(uint),
                NativeHandle: IntPtr.Zero,
                NativeHandleKind: "fensalir-generated-index-buffer"),
            Topology: AquariumFieldMeshTopology.PointList,
            IndexFormat: AquariumFieldMeshIndexFormat.UInt32,
            Layout: AquariumFieldMeshLayout.PositionNormalUvColor,
            BoundsMin: new Vector3(-1.0f, -1.0f, (float)Math.Max(0.0, pointCloudCandidate.MinDepthMeters)),
            BoundsMax: new Vector3(1.0f, 1.0f, (float)Math.Max(pointCloudCandidate.MinDepthMeters + 0.001, pointCloudCandidate.MaxDepthMeters)),
            SubmeshCount: 1);

        return new AquariumFieldResourceDeclaration(
            ResourceKey: pointCloudCandidate.PointCloudResourceKey,
            Kind: AquariumFieldResourceKind.Mesh,
            Residency: AquariumFieldResourceResidency.GpuResident,
            Access: AquariumFieldShaderAccess.ShaderResource,
            Format: "FieldMesh",
            Width: pointCount,
            Height: 1,
            DepthOrCount: pointCount,
            StrideBytes: AquariumFieldMeshResource.PositionNormalUvColorStrideBytes,
            ValidFromNs: pointCloudCandidate.ObservedTimeNs,
            ValidUntilNs: pointCloudCandidate.ObservedTimeNs,
            Version: checked((ulong)Math.Max(0L, pointCloudCandidate.ObservedTimeNs)),
            NativeHandle: IntPtr.Zero,
            NativeHandleKind: "fensalir-leap-point-cloud",
            SourceUri: $"derived-from:{pointCloudCandidate.SourceDisparityResourceKey}",
            Mesh: mesh);
    }

    private static AquariumFieldStereoDepthLowering StereoDepthLoweringForCandidate(
        MimirStereoDepthFieldCandidate depthCandidate,
        string claimKey) =>
        new(
            LoweringKey: $"stereo-depth:{depthCandidate.CalibrationId}:{depthCandidate.CameraPairId}:{depthCandidate.CandidateKey}",
            ClaimKey: claimKey,
            ProfileKey: depthCandidate.ProfileId,
            CalibrationKey: depthCandidate.CalibrationId,
            CameraPairKey: depthCandidate.CameraPairId,
            LeftResourceKey: depthCandidate.LeftResourceKey,
            RightResourceKey: depthCandidate.RightResourceKey,
            DisparityResourceKey: depthCandidate.DisparityResourceKey,
            ConfidenceResourceKey: depthCandidate.ConfidenceResourceKey,
            Width: Math.Max(1, depthCandidate.Width),
            Height: Math.Max(1, depthCandidate.Height),
            MinDisparity: Math.Max(0, depthCandidate.MinDisparity),
            DisparityLevels: Math.Max(1, depthCandidate.DisparityLevels),
            AggregationPathCount: Math.Max(1, depthCandidate.AggregationPathCount),
            CensusRadius: Math.Max(1, depthCandidate.CensusRadius),
            SmoothnessPenaltySmall: (float)depthCandidate.SmoothnessPenaltySmall,
            SmoothnessPenaltyLarge: (float)depthCandidate.SmoothnessPenaltyLarge,
            MinDepthMeters: (float)depthCandidate.MinDepthMeters,
            MaxDepthMeters: (float)depthCandidate.MaxDepthMeters);

    private AquariumFieldSupport SupportForSurfaceIntent(MimirSurfaceIntent intent)
    {
        var age = Math.Max(0.0f, (float)intent.SupportPolicy.MaximumAgeSeconds);
        if (intent.Placement is { } placement)
        {
            var radius = new Vector3(
                Math.Max(options.DefaultSupportRadius, placement.Width * 0.5f),
                Math.Max(options.DefaultSupportRadius, placement.Height * 0.5f),
                Math.Max(options.DefaultSupportRadius, age));
            return new AquariumFieldSupport(
                Center: new Vector3(placement.CenterX, placement.CenterY, 0.0f),
                Radius: radius,
                LocalFrame: Matrix4x4.CreateRotationZ(placement.RotationRadians),
                ConservativeRadius: Math.Max(radius.X, Math.Max(radius.Y, radius.Z)),
                ProjectedError: 0.0f,
                Curvature: 0.0f,
                TemporalUncertainty: age);
        }

        var fallbackRadius = Math.Max(options.DefaultSupportRadius, age);
        return new AquariumFieldSupport(
            Center: Vector3.Zero,
            Radius: new Vector3(options.DefaultSupportRadius, options.DefaultSupportRadius, fallbackRadius),
            LocalFrame: Matrix4x4.Identity,
            ConservativeRadius: fallbackRadius,
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

    private static string DomainKeyForLedSplineCandidate(MimirLedSplineFieldCandidate candidate) =>
        $"mimir:led-spline:{candidate.CalibrationId}:{candidate.SplineId}:{candidate.CandidateKey}";

    private static string DomainKeyForFeatureTrackCandidate(MimirFeatureTrackFieldCandidate candidate) =>
        $"mimir:feature-tracks:{candidate.CalibrationId}:{candidate.CandidateKey}";

    private static string DomainKeyForStereoDepthCandidate(MimirStereoDepthFieldCandidate candidate) =>
        DomainKeyForStereoDepthCandidate(candidate.CalibrationId, candidate.CameraPairId, candidate.CandidateKey);

    private static string DomainKeyForStereoDepthCandidate(string calibrationId, string cameraPairId, string candidateKey) =>
        $"mimir:stereo-depth:{calibrationId}:{cameraPairId}:{candidateKey}";

    private static string DomainKeyForPointCloudCandidate(MimirPointCloudFieldCandidate candidate) =>
        $"mimir:point-cloud:{candidate.CalibrationId}:{candidate.CameraRigId}:{candidate.CandidateKey}";

    private static string DomainKeyForSurfaceIntent(string intentKey) => $"mimir:intent:{intentKey}";

    private static string SanitizeResourceSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }

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
