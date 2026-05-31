namespace Mimir.Runtime.Synchronization;

public enum MimirVisualCalibrationResidualStatus
{
    Empty,
    InsufficientCorrespondence,
    ResidualOnly,
}

public sealed record MimirLedSplineCameraResidual(
    string SourceId,
    string ObservationKey,
    int PointCount,
    double MeanRadiusPixels,
    double Confidence);

public sealed record MimirLedSplinePairResidual(
    string LeftSourceId,
    string RightSourceId,
    int SharedLedCount,
    double MeanCurveParameterResidual,
    double MeanClipspaceDistance,
    double MeanRadiusRatio,
    long MeanTimeOffsetNs,
    double Confidence);

public sealed record MimirVisualCalibrationResidualFrame(
    string CalibrationId,
    string CandidateKey,
    string SplineId,
    MimirVisualCalibrationResidualStatus Status,
    IReadOnlyList<MimirLedSplineCameraResidual> CameraResiduals,
    IReadOnlyList<MimirLedSplinePairResidual> PairResiduals,
    double MeanCurveParameterResidual,
    double Confidence,
    bool HasPoseUpdate);

public sealed class MimirVisualCalibrationResidualSolver
{
    public MimirVisualCalibrationResidualFrame EvaluateLedSpline(MimirLedSplineFieldCandidate candidate)
    {
        if (candidate.CameraObservations.Count == 0)
        {
            return Empty(candidate);
        }

        var cameraResiduals = candidate.CameraObservations
            .Select(static observation => new MimirLedSplineCameraResidual(
                observation.SourceId,
                observation.ObservationKey,
                observation.Points.Count,
                observation.Points.Count == 0
                    ? 0.0
                    : observation.Points.Average(static point => Math.Max(0.0, point.RadiusPixels)),
                Clamp01(observation.Confidence)))
            .ToArray();

        if (!candidate.HasStableLedIndices || candidate.CameraObservations.Count < 2)
        {
            return new MimirVisualCalibrationResidualFrame(
                candidate.CalibrationId,
                candidate.CandidateKey,
                candidate.SplineId,
                MimirVisualCalibrationResidualStatus.InsufficientCorrespondence,
                cameraResiduals,
                [],
                double.NaN,
                0.0,
                HasPoseUpdate: false);
        }

        var pairResiduals = new List<MimirLedSplinePairResidual>();
        for (var leftIndex = 0; leftIndex < candidate.CameraObservations.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < candidate.CameraObservations.Count; rightIndex++)
            {
                var residual = PairResidual(candidate.CameraObservations[leftIndex], candidate.CameraObservations[rightIndex]);
                if (residual is not null)
                {
                    pairResiduals.Add(residual);
                }
            }
        }

        if (pairResiduals.Count == 0)
        {
            return new MimirVisualCalibrationResidualFrame(
                candidate.CalibrationId,
                candidate.CandidateKey,
                candidate.SplineId,
                MimirVisualCalibrationResidualStatus.InsufficientCorrespondence,
                cameraResiduals,
                [],
                double.NaN,
                0.0,
                HasPoseUpdate: false);
        }

        var meanResidual = pairResiduals.Average(static residual => residual.MeanCurveParameterResidual);
        var confidence = Clamp01(candidate.Confidence) *
            pairResiduals.Average(static residual => residual.Confidence) *
            (candidate.HasTemporalCode ? 1.0 : 0.5);
        return new MimirVisualCalibrationResidualFrame(
            candidate.CalibrationId,
            candidate.CandidateKey,
            candidate.SplineId,
            MimirVisualCalibrationResidualStatus.ResidualOnly,
            cameraResiduals,
            pairResiduals,
            meanResidual,
            confidence,
            HasPoseUpdate: false);
    }

    private static MimirVisualCalibrationResidualFrame Empty(MimirLedSplineFieldCandidate candidate) =>
        new(
            candidate.CalibrationId,
            candidate.CandidateKey,
            candidate.SplineId,
            MimirVisualCalibrationResidualStatus.Empty,
            [],
            [],
            double.NaN,
            0.0,
            HasPoseUpdate: false);

    private static MimirLedSplinePairResidual? PairResidual(
        MimirLedSplineCameraObservation left,
        MimirLedSplineCameraObservation right)
    {
        var leftByIndex = left.Points
            .Where(static point => point.LedIndex >= 0)
            .GroupBy(static point => point.LedIndex)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(point => point.Confidence).First());
        var rightByIndex = right.Points
            .Where(static point => point.LedIndex >= 0)
            .GroupBy(static point => point.LedIndex)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(point => point.Confidence).First());
        var shared = leftByIndex.Keys.Intersect(rightByIndex.Keys).Order().ToArray();
        if (shared.Length == 0)
        {
            return null;
        }

        var curveResidual = shared.Average(index => Math.Abs(leftByIndex[index].CurveT - rightByIndex[index].CurveT));
        var clipDistance = shared.Average(index =>
        {
            var dx = leftByIndex[index].ClipX - rightByIndex[index].ClipX;
            var dy = leftByIndex[index].ClipY - rightByIndex[index].ClipY;
            return Math.Sqrt(dx * dx + dy * dy);
        });
        var radiusRatio = shared.Average(index =>
        {
            var leftRadius = Math.Max(1.0e-6, leftByIndex[index].RadiusPixels);
            var rightRadius = Math.Max(1.0e-6, rightByIndex[index].RadiusPixels);
            return Math.Max(leftRadius, rightRadius) / Math.Min(leftRadius, rightRadius);
        });
        var confidence = shared.Average(index => Clamp01(leftByIndex[index].Confidence) * Clamp01(rightByIndex[index].Confidence));
        return new MimirLedSplinePairResidual(
            left.SourceId,
            right.SourceId,
            shared.Length,
            curveResidual,
            clipDistance,
            radiusRatio,
            right.ObservedTimeNs - left.ObservedTimeNs,
            confidence);
    }

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
}
