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
        var domains = new List<AquariumFieldDomain>();
        var claims = new List<AquariumFieldClaim>();
        var candidates = new List<AquariumFieldCandidate>();
        var packets = new List<AquariumFieldBackendPacket>();
        var seenDomains = new HashSet<string>(StringComparer.Ordinal);

        foreach (var window in windows)
        {
            AddDomain(domains, seenDomains, DomainForWindow(window));
        }

        foreach (var observation in observations)
        {
            var domain = DomainForObservation(observation);
            AddDomain(domains, seenDomains, domain);

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

            var claim = ClaimForSurfaceIntent(intent, domain.DomainKey);
            claims.Add(claim);

            var guide = AquariumFieldGuide.Valid(
                claim.Confidence,
                Math.Max(0.0f, (float)intent.SupportPolicy.MaximumAgeSeconds));
            candidates.Add(CandidateForClaim(claim, guide));
        }

        return new AquariumFieldEvidenceFrame
        {
            Domains = domains,
            Claims = claims,
            Candidates = candidates,
            BackendPackets = packets,
            AccumulationWindowSeconds = (float)options.AccumulationWindowSeconds,
            PresentationDelaySeconds = (float)options.PresentationDelaySeconds
        };
    }

    public AquariumGpuSensorFrame BuildGpuSensorFrame(IEnumerable<MimirRollingStreamBuffer> buffers)
    {
        var capacity = buffers.TryGetNonEnumeratedCount(out var count) ? count : 0;
        var textures = new List<AquariumExternalGpuTexture>(capacity);
        var cameras = new List<AquariumGpuSensorCamera>(capacity);
        foreach (var buffer in buffers)
        {
            if (buffer.Descriptor.Kind != MimirStreamKind.Video || buffer.Latest?.VideoFrame is not { } frame)
            {
                continue;
            }

            var firstTexture = textures.Count;
            if (frame.NativeHandle != 0)
            {
                textures.Add(new AquariumExternalGpuTexture(
                    default,
                    new IntPtr(unchecked((long)frame.NativeHandle)),
                    frame.Width,
                    frame.Height,
                    ToFensalirPixelFormat(frame.PixelFormat),
                    frame.DeviceTimestampNs,
                    SharedHandleName: frame.NativeHandleKind));
            }

            cameras.Add(new AquariumGpuSensorCamera(
                buffer.Descriptor.SourceId,
                ToFensalirSensorKind(buffer.Descriptor.SourceId, frame.PixelFormat),
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                Vector4.Zero,
                Vector4.Zero,
                Vector4.Zero,
                frame.Width,
                frame.Height,
                firstTexture,
                textures.Count - firstTexture,
                frame.DeviceTimestampNs));
        }

        return new AquariumGpuSensorFrame
        {
            Cameras = cameras,
            ExternalTextures = textures,
            AccumulationWindowSeconds = (float)options.AccumulationWindowSeconds,
            PresentationDelaySeconds = (float)options.PresentationDelaySeconds
        };
    }

    public AquariumAcousticFieldFrame BuildAcousticFieldFrame(IEnumerable<MimirAudioSynchronizationState> states)
    {
        var capacity = states.TryGetNonEnumeratedCount(out var count) ? count : 0;
        var orderedStates = new List<MimirAudioSynchronizationState>(capacity);
        foreach (var state in states)
        {
            orderedStates.Add(state);
        }

        orderedStates.Sort(static (left, right) =>
        {
            var confidence = right.Confidence.CompareTo(left.Confidence);
            return confidence != 0
                ? confidence
                : string.Compare(left.SourceId, right.SourceId, StringComparison.Ordinal);
        });

        var constraints = new List<AquariumAcousticConstraint>(orderedStates.Count);
        foreach (var state in orderedStates)
        {
            if (state.Confidence <= 0.0)
            {
                continue;
            }

            constraints.Add(new AquariumAcousticConstraint(
                $"{state.ReferenceSourceId}->{state.SourceId}",
                AquariumAcousticConstraintKind.SpeakerProbe,
                Vector3.Zero,
                Vector3.Zero,
                RadiusMeters: 0.10f,
                Confidence: (float)Math.Clamp(state.Confidence, 0.0, 1.0),
                TimestampNs: state.UpdatedAtNs));
        }

        var oracle = orderedStates.FirstOrDefault();
        return new AquariumAcousticFieldFrame
        {
            Constraints = constraints,
            TimingOracleNs = oracle?.UpdatedAtNs ?? 0,
            TimingConfidence = (float)Math.Clamp(oracle?.Confidence ?? 0.0, 0.0, 1.0),
            TimingUncertaintyMicroseconds = (float)(oracle == null
                ? options.DefaultTimingUncertaintyMicroseconds
                : Math.Max(0.1, (1.0 - Math.Clamp(oracle.Confidence, 0.0, 1.0)) * options.DefaultTimingUncertaintyMicroseconds)),
            AccumulationWindowSeconds = (float)options.AccumulationWindowSeconds,
            PresentationDelaySeconds = (float)options.PresentationDelaySeconds
        };
    }

    private static AquariumGpuSensorKind ToFensalirSensorKind(string sourceId, MimirVideoPixelFormat pixelFormat)
    {
        if (pixelFormat == MimirVideoPixelFormat.LeapStereoIr || sourceId.Contains("leap", StringComparison.OrdinalIgnoreCase))
        {
            return AquariumGpuSensorKind.LeapPackedMap;
        }

        if (sourceId.Contains("eye", StringComparison.OrdinalIgnoreCase))
        {
            return AquariumGpuSensorKind.HighRateTracker;
        }

        return AquariumGpuSensorKind.RgbCamera;
    }

    private static AquariumGpuSensorPixelFormat ToFensalirPixelFormat(MimirVideoPixelFormat pixelFormat) =>
        pixelFormat switch
        {
            MimirVideoPixelFormat.Gray8 or MimirVideoPixelFormat.R8 or MimirVideoPixelFormat.Bayer8 => AquariumGpuSensorPixelFormat.R8Unorm,
            MimirVideoPixelFormat.Rg8 => AquariumGpuSensorPixelFormat.Rg8Unorm,
            MimirVideoPixelFormat.Bgra8 => AquariumGpuSensorPixelFormat.Bgra8Unorm,
            MimirVideoPixelFormat.LeapStereoIr => AquariumGpuSensorPixelFormat.LeapPackedMap,
            _ => AquariumGpuSensorPixelFormat.Unknown
        };

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

    private AquariumFieldClaim ClaimForSurfaceIntent(MimirSurfaceIntent intent, string domainKey)
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
            PayloadHandle: intent.SupportPolicy.PolicyId,
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
        if (payload.NativeHandle == 0)
        {
            return "";
        }

        var kind = string.IsNullOrWhiteSpace(payload.NativeHandleKind)
            ? "native"
            : payload.NativeHandleKind;
        return $"{kind}:{payload.NativeHandle:x}";
    }

    private static string DomainKeyForWindow(string windowId) => $"mimir:window:{windowId}";

    private static string DomainKeyForObservation(string observationKey) => $"mimir:observation:{observationKey}";

    private static string DomainKeyForCalibrationConstraint(string constraintKey) => $"mimir:calibration:{constraintKey}";

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
