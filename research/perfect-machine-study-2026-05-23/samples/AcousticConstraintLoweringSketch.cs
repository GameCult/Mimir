// Sketch only. Purpose: keep acoustic raw buffers in Mimir/native DSP while
// sending Fensalir compact constraints for spatial fusion and diagnostics.

using System;
using System.Collections.Generic;

namespace Mimir.PerfectMachineStudy;

public readonly record struct MimirDecodedAnchorSketch(
    string SourceId,
    int EventIndex,
    double SourceSampleTimeSeconds,
    double CanonicalTimeSeconds,
    double DelaySeconds,
    double FrequencyHz,
    float Confidence,
    float PhaseRadians,
    float GroupDelaySeconds);

public readonly record struct MimirMicGeometrySketch(
    string SourceId,
    float X,
    float Y,
    float Z);

public sealed class AcousticConstraintLoweringSketch
{
    private readonly List<AquariumAcousticConstraintSketch> _constraints = new(128);

    public AquariumAcousticFieldFrameSketch Lower(
        ReadOnlySpan<MimirDecodedAnchorSketch> anchors,
        ReadOnlySpan<MimirMicGeometrySketch> geometry,
        double canonicalNowSeconds)
    {
        _constraints.Clear();

        for (var i = 0; i < anchors.Length; i++)
        {
            ref readonly var anchor = ref anchors[i];
            if (anchor.Confidence < 0.4f)
            {
                continue;
            }

            var mic = FindGeometry(geometry, anchor.SourceId);
            if (mic.SourceId is null)
            {
                continue;
            }

            _constraints.Add(new AquariumAcousticConstraintSketch(
                anchor.SourceId,
                anchor.EventIndex,
                canonicalNowSeconds,
                anchor.CanonicalTimeSeconds,
                anchor.DelaySeconds,
                anchor.FrequencyHz,
                mic.X,
                mic.Y,
                mic.Z,
                anchor.Confidence,
                anchor.PhaseRadians,
                anchor.GroupDelaySeconds));
        }

        // Production version should avoid this copy and hand Fensalir a stable
        // span/packet buffer whose lifetime is owned by the frame boundary.
        return new AquariumAcousticFieldFrameSketch(canonicalNowSeconds, _constraints.ToArray());
    }

    private static MimirMicGeometrySketch FindGeometry(
        ReadOnlySpan<MimirMicGeometrySketch> geometry,
        string sourceId)
    {
        for (var i = 0; i < geometry.Length; i++)
        {
            if (StringComparer.Ordinal.Equals(geometry[i].SourceId, sourceId))
            {
                return geometry[i];
            }
        }

        return default;
    }
}

public readonly record struct AquariumAcousticFieldFrameSketch(
    double CanonicalTimeSeconds,
    AquariumAcousticConstraintSketch[] Constraints);

public readonly record struct AquariumAcousticConstraintSketch(
    string SourceId,
    int EventIndex,
    double FrameCanonicalTimeSeconds,
    double EventCanonicalTimeSeconds,
    double DelaySeconds,
    double FrequencyHz,
    float MicX,
    float MicY,
    float MicZ,
    float Confidence,
    float PhaseRadians,
    float GroupDelaySeconds);

