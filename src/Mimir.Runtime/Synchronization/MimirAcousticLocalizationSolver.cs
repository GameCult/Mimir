using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirMicrophonePose(
    string SourceId,
    Vector3 PositionMeters);

public sealed record MimirPairDelayObservation(
    string MicA,
    string MicB,
    double DelaySeconds,
    double Confidence);

public sealed record MimirAcousticSourceCandidate(
    Vector3 PositionMeters,
    double Score,
    int SupportingPairs);

public sealed class MimirSrpPhatGridSolver(
    double speedOfSoundMetersPerSecond = 343.0,
    double halfScoreErrorMicroseconds = 100.0)
{
    public MimirAcousticSourceCandidate? FindBestCandidate(
        IReadOnlyList<MimirMicrophonePose> microphones,
        IReadOnlyList<Vector3> candidatePoints,
        IReadOnlyList<MimirPairDelayObservation> observations)
    {
        if (microphones.Count < 2 || candidatePoints.Count == 0 || observations.Count == 0)
        {
            return null;
        }

        var micById = microphones.ToDictionary(mic => mic.SourceId, StringComparer.Ordinal);
        MimirAcousticSourceCandidate? best = null;
        foreach (var point in candidatePoints)
        {
            var weightedScore = 0.0;
            var weight = 0.0;
            var supportingPairs = 0;
            foreach (var observation in observations)
            {
                if (observation.Confidence <= 0.0 ||
                    !micById.TryGetValue(observation.MicA, out var micA) ||
                    !micById.TryGetValue(observation.MicB, out var micB))
                {
                    continue;
                }

                var predicted = (DistanceMeters(micB.PositionMeters, point) - DistanceMeters(micA.PositionMeters, point)) /
                    speedOfSoundMetersPerSecond;
                var errorUs = Math.Abs(predicted - observation.DelaySeconds) * 1_000_000.0;
                var localScore = 1.0 / (1.0 + errorUs / halfScoreErrorMicroseconds);
                weightedScore += localScore * observation.Confidence;
                weight += observation.Confidence;
                supportingPairs++;
            }

            if (weight <= 0.0)
            {
                continue;
            }

            var score = weightedScore / weight;
            if (best == null || score > best.Score)
            {
                best = new MimirAcousticSourceCandidate(point, score, supportingPairs);
            }
        }

        return best;
    }

    public static IReadOnlyList<Vector3> BuildGrid(Vector3 min, Vector3 max, double spacingMeters)
    {
        if (spacingMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(spacingMeters), "Grid spacing must be positive.");
        }

        var points = new List<Vector3>();
        for (var z = min.Z; z <= max.Z + 1.0e-6f; z += (float)spacingMeters)
        {
            for (var y = min.Y; y <= max.Y + 1.0e-6f; y += (float)spacingMeters)
            {
                for (var x = min.X; x <= max.X + 1.0e-6f; x += (float)spacingMeters)
                {
                    points.Add(new Vector3(x, y, z));
                }
            }
        }

        return points;
    }

    private static double DistanceMeters(Vector3 a, Vector3 b)
    {
        var delta = a - b;
        return Math.Sqrt(delta.LengthSquared());
    }
}
