namespace Mimir.Runtime.Synchronization;

public enum MimirBridgeWindowStatus
{
    Empty,
    Live,
}

public enum MimirObservationModality
{
    Camera,
    Audio,
    Network,
    Timing,
    Response,
}

public enum MimirCalibrationEvidenceKind
{
    Unknown,
    Loopback,
    Bioacoustic,
    Passive,
    Visual,
    Network,
    ComplexContour,
    Chirplet,
}

public enum MimirSurfaceDomain
{
    Unknown,
    CameraImage,
    AudioWaveform,
    AudioSpectrum,
    Timing,
    AcousticResponse,
}

public enum MimirSurfaceIntentPurpose
{
    Debug,
    Calibration,
    Production,
}

public readonly record struct MimirBridgePayloadView(
    ulong NativeHandle,
    string NativeHandleKind,
    int ByteLength,
    string ResourceKey = "")
{
    public bool HasResource => NativeHandle != 0 || !string.IsNullOrWhiteSpace(ResourceKey);
}

public readonly record struct MimirBridgeSampleDescriptor(
    MimirStreamKind Kind,
    int ByteLength,
    int Width,
    int Height,
    MimirVideoPixelFormat PixelFormat,
    int StrideBytes,
    int SampleRate,
    int Channels,
    MimirAudioSampleFormat AudioSampleFormat,
    int FrameCount)
{
    public static MimirBridgeSampleDescriptor FromSample(MimirStreamSample sample)
    {
        var video = sample.VideoFrame;
        var audio = sample.AudioBlock;
        return new MimirBridgeSampleDescriptor(
            sample.Kind,
            sample.ByteLength,
            video?.Width ?? 0,
            video?.Height ?? 0,
            video?.PixelFormat ?? MimirVideoPixelFormat.Unknown,
            video?.StrideBytes ?? 0,
            audio?.SampleRate ?? 0,
            audio?.Channels ?? 0,
            audio?.SampleFormat ?? MimirAudioSampleFormat.Unknown,
            audio?.FrameCount ?? 0);
    }
}

public readonly record struct MimirRollingStreamWindow(
    string WindowId,
    TimeSpan Duration,
    string StreamId,
    MimirStreamKind SourceKind,
    MimirStreamOrigin Origin,
    MimirBridgeSampleDescriptor SampleDescriptor,
    MimirBridgePayloadView Payload,
    long DeviceTimestampNs,
    long CanonicalTimestampEstimateNs,
    ulong SequenceId,
    MimirBridgeWindowStatus Status,
    int SampleCount,
    long WindowStartNs,
    long EdgeNs);

public readonly record struct MimirObservationProvenance(
    MimirStreamOrigin Origin,
    string SourceLabel,
    ulong SequenceId,
    long ArrivalNs);

public readonly record struct MimirObservation(
    string ObservationKey,
    string WindowId,
    string StreamId,
    string SensorId,
    string CalibrationId,
    MimirObservationModality Modality,
    long ObservedTimeNs,
    long CanonicalTimeEstimateNs,
    long UncertaintyNs,
    MimirBridgePayloadView Payload,
    MimirObservationProvenance Provenance,
    double Confidence);

public readonly record struct MimirCalibrationBandConstraint(
    double FrequencyHz,
    double Magnitude,
    double PhaseRadians,
    double Confidence);

public sealed record MimirCalibrationConstraint(
    string ConstraintKey,
    string PathId,
    string SourceId,
    string ReceiverId,
    MimirCalibrationEvidenceKind EvidenceKind,
    double DelayEstimateMicroseconds,
    double DelayUncertaintyMicroseconds,
    double PhaseOrGroupDelayMicroseconds,
    IReadOnlyList<MimirCalibrationBandConstraint> FrequencyResponse,
    string UsableBandMask,
    double Confidence);

public readonly record struct MimirSurfaceAxes(
    string X,
    string Y,
    string Z,
    string Lane);

public readonly record struct MimirSurfaceSupportPolicy(
    string PolicyId,
    double MinimumConfidence,
    double MaximumAgeSeconds);

public readonly record struct MimirSurfaceMaterialIntent(
    string IntentId,
    string Role,
    double Confidence);

public readonly record struct MimirSurfaceUpdateBudget(
    double MaximumUpdateHz,
    int MaximumObservations,
    int MaximumPayloadBytes);

public sealed record MimirSurfaceIntent(
    string IntentKey,
    IReadOnlyList<string> SourceObservationKeys,
    MimirSurfaceDomain Domain,
    MimirSurfaceAxes Axes,
    MimirSurfaceSupportPolicy SupportPolicy,
    MimirSurfaceMaterialIntent MaterialGraph,
    MimirSurfaceUpdateBudget UpdateBudget,
    MimirSurfaceIntentPurpose Purpose);

public static class MimirFensalirBridgeMapper
{
    public static MimirRollingStreamWindow MapWindow(
        MimirRollingStreamBuffer buffer,
        long canonicalTimestampEstimateNs = 0)
    {
        var latest = buffer.Latest;
        var sampleDescriptor = latest.HasValue
            ? MimirBridgeSampleDescriptor.FromSample(latest.Value)
            : default;
        return new MimirRollingStreamWindow(
            buffer.Descriptor.BufferKey,
            buffer.Duration,
            buffer.Descriptor.SourceId,
            buffer.Descriptor.Kind,
            buffer.Descriptor.Origin,
            sampleDescriptor,
            latest.HasValue ? PayloadFromSample(latest.Value) : default,
            latest.HasValue ? DeviceTimestampNs(latest.Value) : 0,
            canonicalTimestampEstimateNs != 0 || !latest.HasValue
                ? canonicalTimestampEstimateNs
                : latest.Value.TimestampNs,
            latest?.Sequence ?? 0,
            latest.HasValue ? MimirBridgeWindowStatus.Live : MimirBridgeWindowStatus.Empty,
            buffer.Count,
            buffer.WindowStartNs,
            buffer.EdgeNs);
    }

    public static void MapWindows(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        ICollection<MimirRollingStreamWindow> destination,
        long canonicalTimestampEstimateNs = 0)
    {
        destination.Clear();
        foreach (var buffer in buffers)
        {
            destination.Add(MapWindow(buffer, canonicalTimestampEstimateNs));
        }
    }

    public static bool TryMapLatestObservation(
        MimirRollingStreamBuffer buffer,
        out MimirObservation observation,
        long canonicalTimestampEstimateNs = 0,
        long uncertaintyNs = 0,
        string sensorId = "",
        string calibrationId = "",
        double confidence = 1.0)
    {
        if (buffer.Latest is not { } latest)
        {
            observation = default;
            return false;
        }

        var window = MapWindow(buffer, canonicalTimestampEstimateNs);
        observation = MapObservation(window, latest, sensorId, calibrationId, uncertaintyNs, confidence);
        return true;
    }

    public static void MapLatestObservations(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        ICollection<MimirObservation> destination,
        long canonicalTimestampEstimateNs = 0,
        long uncertaintyNs = 0,
        string calibrationId = "",
        double confidence = 1.0)
    {
        destination.Clear();
        foreach (var buffer in buffers)
        {
            if (TryMapLatestObservation(
                buffer,
                out var observation,
                canonicalTimestampEstimateNs,
                uncertaintyNs,
                buffer.Descriptor.SourceId,
                calibrationId,
                confidence))
            {
                destination.Add(observation);
            }
        }
    }

    public static MimirObservation MapObservation(
        MimirRollingStreamWindow window,
        MimirStreamSample sample,
        string sensorId = "",
        string calibrationId = "",
        long uncertaintyNs = 0,
        double confidence = 1.0) =>
        new(
            $"{window.WindowId}:{sample.Sequence}",
            window.WindowId,
            window.StreamId,
            string.IsNullOrWhiteSpace(sensorId) ? window.StreamId : sensorId,
            calibrationId,
            ToObservationModality(sample.Kind, sample.Origin),
            sample.TimestampNs,
            window.CanonicalTimestampEstimateNs == 0 ? sample.TimestampNs : window.CanonicalTimestampEstimateNs,
            Math.Max(0, uncertaintyNs),
            PayloadFromSample(sample),
            new MimirObservationProvenance(
                sample.Origin,
                window.StreamId,
                sample.Sequence,
                sample.ArrivalNs),
            Math.Clamp(confidence, 0.0, 1.0));

    public static void MapCalibrationConstraints(
        IEnumerable<MimirAudioSynchronizationState> states,
        ICollection<MimirCalibrationConstraint> destination)
    {
        destination.Clear();
        foreach (var state in states)
        {
            destination.Add(MapCalibrationConstraint(state));
        }
    }

    public static MimirCalibrationConstraint MapCalibrationConstraint(MimirAudioSynchronizationState state) =>
        new(
            $"{state.ReferenceSourceId}->{state.SourceId}:sync",
            $"{state.ReferenceSourceId}->{state.SourceId}",
            state.ReferenceSourceId,
            state.SourceId,
            MimirCalibrationEvidenceKind.Loopback,
            state.DelayMicroseconds,
            DelayUncertaintyMicroseconds(state.Confidence),
            0.0,
            MapBandConstraints(state.BandResponses),
            "",
            Math.Clamp(state.Confidence, 0.0, 1.0));

    public static MimirCalibrationConstraint MapCalibrationConstraint(MimirAudioSynchronizationReport report) =>
        new(
            $"{report.ReferenceSourceId}->{report.SourceId}:{report.EvidenceKind}",
            $"{report.ReferenceSourceId}->{report.SourceId}",
            report.ReferenceSourceId,
            report.SourceId,
            ToCalibrationEvidenceKind(report.EvidenceKind),
            report.DelayMicroseconds,
            DelayUncertaintyMicroseconds(report.Confidence),
            0.0,
            MapBandConstraints(report.BandResponses),
            "",
            Math.Clamp(report.Confidence, 0.0, 1.0));

    public static MimirSurfaceIntent MapSurfaceIntent(
        string intentKey,
        IReadOnlyList<string> sourceObservationKeys,
        MimirSurfaceDomain domain,
        MimirSurfaceAxes axes,
        MimirSurfaceSupportPolicy supportPolicy,
        MimirSurfaceMaterialIntent materialGraph,
        MimirSurfaceUpdateBudget updateBudget,
        MimirSurfaceIntentPurpose purpose = MimirSurfaceIntentPurpose.Debug) =>
        new(
            intentKey,
            sourceObservationKeys,
            domain,
            axes,
            supportPolicy,
            materialGraph,
            updateBudget,
            purpose);

    public static MimirSurfaceIntent MapDefaultSurfaceIntent(
        MimirRollingStreamWindow window,
        MimirObservation observation,
        MimirSurfaceIntentPurpose purpose = MimirSurfaceIntentPurpose.Debug)
    {
        var domain = window.SourceKind == MimirStreamKind.Video
            ? MimirSurfaceDomain.CameraImage
            : MimirSurfaceDomain.AudioWaveform;
        return MapSurfaceIntent(
            $"{window.WindowId}:surface",
            [observation.ObservationKey],
            domain,
            DefaultAxes(domain),
            new MimirSurfaceSupportPolicy("latest-window", 0.0, window.Duration.TotalSeconds),
            new MimirSurfaceMaterialIntent("evidence", "source-evidence", observation.Confidence),
            new MimirSurfaceUpdateBudget(0.0, 1, window.Payload.ByteLength),
            purpose);
    }

    private static MimirBridgePayloadView PayloadFromSample(MimirStreamSample sample)
    {
        var nativeHandle = sample.VideoFrame?.NativeHandle ?? sample.AudioBlock?.NativeHandle ?? sample.PayloadHandle;
        var nativeHandleKind = sample.VideoFrame?.NativeHandleKind ?? sample.AudioBlock?.NativeHandleKind ?? "";
        return new MimirBridgePayloadView(
            nativeHandle,
            nativeHandleKind,
            sample.ByteLength,
            ResourceKeyForPayload(nativeHandle, nativeHandleKind));
    }

    public static string ResourceKeyForPayload(MimirBridgePayloadView payload) =>
        !string.IsNullOrWhiteSpace(payload.ResourceKey)
            ? payload.ResourceKey
            : ResourceKeyForPayload(payload.NativeHandle, payload.NativeHandleKind);

    private static string ResourceKeyForPayload(ulong nativeHandle, string nativeHandleKind)
    {
        if (nativeHandle == 0)
        {
            return "";
        }

        var kind = string.IsNullOrWhiteSpace(nativeHandleKind)
            ? "native"
            : nativeHandleKind.Trim().ToLowerInvariant();
        return $"mimir:resource:{kind}:{nativeHandle:x}";
    }

    private static long DeviceTimestampNs(MimirStreamSample sample) =>
        sample.VideoFrame?.DeviceTimestampNs ?? sample.AudioBlock?.DeviceTimestampNs ?? sample.TimestampNs;

    private static MimirObservationModality ToObservationModality(MimirStreamKind kind, MimirStreamOrigin origin)
    {
        if (origin == MimirStreamOrigin.Network)
        {
            return MimirObservationModality.Network;
        }

        return kind == MimirStreamKind.Video
            ? MimirObservationModality.Camera
            : MimirObservationModality.Audio;
    }

    private static MimirCalibrationEvidenceKind ToCalibrationEvidenceKind(string evidenceKind) =>
        evidenceKind switch
        {
            "bioacoustic" => MimirCalibrationEvidenceKind.Bioacoustic,
            "complex-contour" => MimirCalibrationEvidenceKind.ComplexContour,
            "passive" => MimirCalibrationEvidenceKind.Passive,
            "chirplet" => MimirCalibrationEvidenceKind.Chirplet,
            "loopback" => MimirCalibrationEvidenceKind.Loopback,
            _ => MimirCalibrationEvidenceKind.Unknown,
        };

    private static IReadOnlyList<MimirCalibrationBandConstraint> MapBandConstraints(
        IReadOnlyList<MimirChirpletBandResponse> bandResponses)
    {
        if (bandResponses.Count == 0)
        {
            return Array.Empty<MimirCalibrationBandConstraint>();
        }

        var constraints = new MimirCalibrationBandConstraint[bandResponses.Count];
        for (var index = 0; index < bandResponses.Count; index++)
        {
            var band = bandResponses[index];
            constraints[index] = new MimirCalibrationBandConstraint(
                band.CenterHz,
                band.Energy,
                band.PhaseRadians,
                Math.Clamp(band.Energy, 0.0, 1.0));
        }

        return constraints;
    }

    private static double DelayUncertaintyMicroseconds(double confidence) =>
        Math.Max(0.0, 1.0 - Math.Clamp(confidence, 0.0, 1.0)) * 1000.0;

    private static MimirSurfaceAxes DefaultAxes(MimirSurfaceDomain domain) =>
        domain switch
        {
            MimirSurfaceDomain.CameraImage => new MimirSurfaceAxes("image-x", "image-y", "time-age", "stream"),
            MimirSurfaceDomain.AudioSpectrum => new MimirSurfaceAxes("frequency", "amplitude", "time-age", "stream"),
            MimirSurfaceDomain.AudioWaveform => new MimirSurfaceAxes("sample-time", "amplitude", "time-age", "stream"),
            MimirSurfaceDomain.Timing => new MimirSurfaceAxes("source-time", "delay", "confidence", "path"),
            MimirSurfaceDomain.AcousticResponse => new MimirSurfaceAxes("frequency", "response", "delay", "path"),
            _ => new MimirSurfaceAxes("", "", "", ""),
        };
}
