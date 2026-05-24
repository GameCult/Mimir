using System.Numerics;
using Aquarium.Engine.Render;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirFensalirLoweringOptions(
    double AccumulationWindowSeconds = 5.0,
    double PresentationDelaySeconds = 5.0,
    double DefaultTimingUncertaintyMicroseconds = 1000.0);

public sealed class MimirFensalirFieldLowering(MimirFensalirLoweringOptions? options = null)
{
    private readonly MimirFensalirLoweringOptions options = options ?? new();

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
}
