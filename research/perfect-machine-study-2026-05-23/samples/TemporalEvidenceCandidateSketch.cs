// Sketch only. Purpose: show how Mimir evidence can be lowered as candidates
// without becoming a second stable-track database beside Fensalir.

using System;

namespace Mimir.PerfectMachineStudy;

public enum MimirEvidenceKindSketch
{
    Unknown = 0,
    VisualFeature = 1,
    ChirpAnchor = 2,
    PassiveAudioAnchor = 3,
    AcousticSource = 4
}

public readonly record struct MimirEvidenceCandidateSketch(
    MimirEvidenceKindSketch Kind,
    ulong StableSourceKey,
    double CanonicalTimeSeconds,
    float X,
    float Y,
    float Z,
    float Radius,
    float Confidence,
    float Residual,
    uint PayloadOffset,
    uint PayloadLength);

public readonly record struct FensalirReservoirCandidateSketch(
    ulong StableSourceKey,
    double CanonicalTimeSeconds,
    float X,
    float Y,
    float Z,
    float Radius,
    float Weight,
    uint Domain,
    uint PayloadOffset,
    uint PayloadLength);

public static class TemporalEvidenceCandidateLoweringSketch
{
    public static int Lower(
        ReadOnlySpan<MimirEvidenceCandidateSketch> input,
        Span<FensalirReservoirCandidateSketch> output)
    {
        var count = 0;
        for (var i = 0; i < input.Length && count < output.Length; i++)
        {
            ref readonly var candidate = ref input[i];
            if (candidate.Confidence <= 0.0f || candidate.Radius <= 0.0f)
            {
                continue;
            }

            var residualPenalty = 1.0f / (1.0f + MathF.Max(0.0f, candidate.Residual));
            var weight = candidate.Confidence * residualPenalty;
            if (weight <= 0.0f)
            {
                continue;
            }

            output[count++] = new FensalirReservoirCandidateSketch(
                candidate.StableSourceKey,
                candidate.CanonicalTimeSeconds,
                candidate.X,
                candidate.Y,
                candidate.Z,
                candidate.Radius,
                weight,
                (uint)candidate.Kind,
                candidate.PayloadOffset,
                candidate.PayloadLength);
        }

        return count;
    }
}

