using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public readonly record struct MimirLedPixelDetection(
    int LedIndex,
    double ImageX,
    double ImageY,
    double RadiusPixels,
    double Confidence,
    double PeakLuma = 1.0,
    double LocalContrast = 1.0,
    bool IsSaturated = false);

public sealed record MimirLedSplineCurveFit(
    string SourceId,
    string ObservationKey,
    IReadOnlyList<MimirLedSplineObservationPoint> Points,
    double CurveLengthPixels,
    double Confidence);

public sealed record MimirLedSplineQualityReport(
    string SourceId,
    string ObservationKey,
    int DetectedLedCount,
    double CurveCoverage,
    double SpacingCoherence,
    double Smoothness,
    double ExposureFitness,
    double SaturatedFraction,
    double Score,
    bool UsableForCalibration);

public sealed record MimirCameraExposureGainSetting(
    string SettingId,
    int Exposure,
    int Gain,
    string Notes);

public sealed record MimirLedSplineSweepResult(
    string SourceId,
    MimirCameraExposureGainSetting Setting,
    MimirLedSplineCurveFit Curve,
    MimirLedSplineQualityReport Quality);

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

public readonly record struct MimirLedSplineScenePoint(
    int LedIndex,
    Vector3 PositionMeters,
    double Confidence);

public sealed record MimirCameraPoseEstimate(
    string SourceId,
    Vector3 PositionMeters,
    Quaternion CameraToWorldRotation,
    double Confidence);

public sealed record MimirCameraPoseUpdate(
    string SourceId,
    Vector3 EstimatedPositionMeters,
    Vector3 DeltaMeters,
    int UsedPointCount,
    double MeanRayDistanceMeters,
    double Confidence);

public sealed record MimirCameraRigCalibrationFrame(
    string CalibrationId,
    string CandidateKey,
    string SplineId,
    IReadOnlyList<MimirCameraPoseUpdate> PoseUpdates,
    double MeanRayDistanceMeters,
    double Confidence,
    bool HasPoseUpdate);

public sealed class MimirLedSplineCurveSolver
{
    public MimirLedSplineCurveFit SolveCameraCurve(
        string sourceId,
        string observationKey,
        int width,
        int height,
        long observedTimeNs,
        IEnumerable<MimirLedPixelDetection> detections)
    {
        var ordered = OrderDetections(detections).ToArray();
        if (ordered.Length == 0)
        {
            return new MimirLedSplineCurveFit(sourceId, observationKey, [], 0.0, 0.0);
        }

        var distances = new double[ordered.Length];
        var length = 0.0;
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var dx = current.ImageX - previous.ImageX;
            var dy = current.ImageY - previous.ImageY;
            length += Math.Sqrt(dx * dx + dy * dy);
            distances[index] = length;
        }

        var points = new MimirLedSplineObservationPoint[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            var detection = ordered[index];
            var curveT = length <= 1.0e-9 ? 0.0 : distances[index] / length;
            points[index] = new MimirLedSplineObservationPoint(
                detection.LedIndex,
                curveT,
                detection.ImageX,
                detection.ImageY,
                PixelToClipX(detection.ImageX, width),
                PixelToClipY(detection.ImageY, height),
                detection.RadiusPixels,
                detection.Confidence);
        }

        var confidence = ordered.Average(static detection => Clamp01(detection.Confidence));
        return new MimirLedSplineCurveFit(sourceId, observationKey, points, length, confidence);
    }

    public MimirLedSplineCameraObservation ToCameraObservation(
        MimirLedSplineCurveFit fit,
        int width,
        int height,
        long observedTimeNs) =>
        new(
            fit.ObservationKey,
            fit.SourceId,
            width,
            height,
            observedTimeNs,
            fit.Points,
            fit.Confidence);

    private static IReadOnlyList<MimirLedPixelDetection> OrderDetections(IEnumerable<MimirLedPixelDetection> detections)
    {
        var usable = detections
            .Where(static detection => detection.Confidence > 0.0)
            .ToArray();
        if (usable.Length == 0)
        {
            return [];
        }

        if (usable.All(static detection => detection.LedIndex >= 0))
        {
            return usable
                .OrderBy(static detection => detection.LedIndex)
                .ThenByDescending(static detection => detection.Confidence)
                .GroupBy(static detection => detection.LedIndex)
                .Select(static group => group.First())
                .ToArray();
        }

        var remaining = usable.ToList();
        var current = remaining
            .OrderBy(static detection => detection.ImageY)
            .ThenBy(static detection => detection.ImageX)
            .First();
        var ordered = new List<MimirLedPixelDetection>(usable.Length) { current };
        remaining.Remove(current);
        while (remaining.Count > 0)
        {
            var next = remaining
                .OrderBy(detection => SquaredDistance(current, detection))
                .ThenByDescending(static detection => detection.Confidence)
                .First();
            ordered.Add(next);
            remaining.Remove(next);
            current = next;
        }

        return ordered;
    }

    private static double SquaredDistance(MimirLedPixelDetection left, MimirLedPixelDetection right)
    {
        var dx = left.ImageX - right.ImageX;
        var dy = left.ImageY - right.ImageY;
        return dx * dx + dy * dy;
    }

    private static double PixelToClipX(double imageX, int width) =>
        Math.Clamp(imageX / Math.Max(1.0, width) * 2.0 - 1.0, -1.0, 1.0);

    private static double PixelToClipY(double imageY, int height) =>
        Math.Clamp(1.0 - imageY / Math.Max(1.0, height) * 2.0, -1.0, 1.0);

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
}

public sealed class MimirLedSplineQualityScorer
{
    public MimirLedSplineQualityReport Score(
        MimirLedSplineCurveFit curve,
        int expectedLedCount,
        double saturatedFraction = 0.0,
        double exposureFitness = 1.0)
    {
        var points = curve.Points
            .OrderBy(static point => point.CurveT)
            .ToArray();
        if (points.Length == 0 || expectedLedCount <= 0)
        {
            return new MimirLedSplineQualityReport(
                curve.SourceId,
                curve.ObservationKey,
                points.Length,
                0.0,
                0.0,
                0.0,
                0.0,
                Clamp01(saturatedFraction),
                0.0,
                UsableForCalibration: false);
        }

        var expected = Math.Max(1, expectedLedCount);
        var detected = points.Length;
        var coverage = Clamp01(Math.Min(detected / (double)expected, expected / (double)detected));
        var spacing = SpacingCoherence(points);
        var smoothness = Smoothness(points);
        var pointConfidence = points.Average(static point => Clamp01(point.Confidence));
        var saturationPenalty = 1.0 - Clamp01(saturatedFraction);
        var exposure = Clamp01(exposureFitness);
        var score = coverage *
            (0.28 * spacing + 0.22 * smoothness + 0.25 * pointConfidence + 0.25 * exposure) *
            saturationPenalty;
        return new MimirLedSplineQualityReport(
            curve.SourceId,
            curve.ObservationKey,
            points.Length,
            coverage,
            spacing,
            smoothness,
            exposure,
            Clamp01(saturatedFraction),
            Clamp01(score),
            UsableForCalibration: points.Length >= 3 && coverage >= 0.60 && score >= 0.55);
    }

    private static double SpacingCoherence(IReadOnlyList<MimirLedSplineObservationPoint> points)
    {
        if (points.Count < 3)
        {
            return points.Count >= 2 ? 0.75 : 0.0;
        }

        var distances = new double[points.Count - 1];
        for (var index = 1; index < points.Count; index++)
        {
            var dx = points[index].ImageX - points[index - 1].ImageX;
            var dy = points[index].ImageY - points[index - 1].ImageY;
            distances[index - 1] = Math.Sqrt(dx * dx + dy * dy);
        }

        var mean = distances.Average();
        if (mean <= 1.0e-9)
        {
            return 0.0;
        }

        var variance = distances.Average(distance =>
        {
            var delta = distance - mean;
            return delta * delta;
        });
        var coefficient = Math.Sqrt(variance) / mean;
        return 1.0 / (1.0 + coefficient * 3.0);
    }

    private static double Smoothness(IReadOnlyList<MimirLedSplineObservationPoint> points)
    {
        if (points.Count < 3)
        {
            return points.Count >= 2 ? 0.75 : 0.0;
        }

        var turnSum = 0.0;
        var turns = 0;
        for (var index = 1; index < points.Count - 1; index++)
        {
            var ax = points[index].ImageX - points[index - 1].ImageX;
            var ay = points[index].ImageY - points[index - 1].ImageY;
            var bx = points[index + 1].ImageX - points[index].ImageX;
            var by = points[index + 1].ImageY - points[index].ImageY;
            var aLength = Math.Sqrt(ax * ax + ay * ay);
            var bLength = Math.Sqrt(bx * bx + by * by);
            if (aLength <= 1.0e-9 || bLength <= 1.0e-9)
            {
                continue;
            }

            var dot = Math.Clamp((ax * bx + ay * by) / (aLength * bLength), -1.0, 1.0);
            turnSum += Math.Acos(dot);
            turns++;
        }

        if (turns == 0)
        {
            return 0.0;
        }

        var meanTurn = turnSum / turns;
        return 1.0 / (1.0 + meanTurn);
    }

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
}

public sealed class MimirLedSplineSweepSelector
{
    public MimirLedSplineSweepResult? SelectBest(IEnumerable<MimirLedSplineSweepResult> results) =>
        results
            .Where(static result => result.Quality.UsableForCalibration)
            .OrderByDescending(static result => result.Quality.Score)
            .ThenBy(static result => result.Quality.SaturatedFraction)
            .FirstOrDefault();
}

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

public sealed class MimirCameraRigCalibrationSolver
{
    public MimirCameraRigCalibrationFrame FitCameraPositionsFromLedSpline(
        MimirLedSplineFieldCandidate candidate,
        IEnumerable<MimirLedSplineScenePoint> scenePoints,
        IEnumerable<MimirCameraPoseEstimate> currentPoses,
        double maximumDeltaMeters = 0.25)
    {
        var sceneByLed = scenePoints
            .Where(static point => point.LedIndex >= 0 && point.Confidence > 0.0)
            .GroupBy(static point => point.LedIndex)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(point => point.Confidence).First());
        var poseBySource = currentPoses.ToDictionary(static pose => pose.SourceId, StringComparer.Ordinal);
        var updates = new List<MimirCameraPoseUpdate>();

        foreach (var observation in candidate.CameraObservations)
        {
            if (!poseBySource.TryGetValue(observation.SourceId, out var pose) ||
                pose.Confidence <= 0.0)
            {
                continue;
            }

            var rays = observation.Points
                .Where(point => sceneByLed.ContainsKey(point.LedIndex) && point.Confidence > 0.0)
                .Select(point => new RayPoint(
                    Scene: sceneByLed[point.LedIndex].PositionMeters,
                    Direction: RayDirection(point, pose.CameraToWorldRotation),
                    Confidence: Math.Min(point.Confidence, sceneByLed[point.LedIndex].Confidence)))
                .ToArray();
            if (rays.Length < 3 || !TryEstimateCameraPosition(rays, out var estimatedPosition))
            {
                continue;
            }

            var delta = estimatedPosition - pose.PositionMeters;
            var clampedDelta = ClampLength(delta, (float)Math.Max(0.0, maximumDeltaMeters));
            var nextPosition = pose.PositionMeters + clampedDelta;
            var meanDistance = rays.Average(ray => DistancePointToRay(ray.Scene, nextPosition, ray.Direction));
            var confidence = Clamp01(candidate.Confidence) *
                Clamp01(pose.Confidence) *
                rays.Average(static ray => Clamp01(ray.Confidence)) *
                (1.0 / (1.0 + meanDistance));
            updates.Add(new MimirCameraPoseUpdate(
                observation.SourceId,
                nextPosition,
                clampedDelta,
                rays.Length,
                meanDistance,
                confidence));
        }

        var meanRayDistance = updates.Count == 0
            ? double.NaN
            : updates.Average(static update => update.MeanRayDistanceMeters);
        var frameConfidence = updates.Count == 0
            ? 0.0
            : updates.Average(static update => update.Confidence);
        return new MimirCameraRigCalibrationFrame(
            candidate.CalibrationId,
            candidate.CandidateKey,
            candidate.SplineId,
            updates,
            meanRayDistance,
            frameConfidence,
            HasPoseUpdate: updates.Count > 0);
    }

    private static Vector3 RayDirection(MimirLedSplineObservationPoint point, Quaternion cameraToWorldRotation)
    {
        var local = Vector3.Normalize(new Vector3((float)point.ClipX, (float)point.ClipY, 1.0f));
        return Vector3.Normalize(Vector3.Transform(local, cameraToWorldRotation));
    }

    private static bool TryEstimateCameraPosition(IReadOnlyList<RayPoint> rays, out Vector3 position)
    {
        var a00 = 0.0;
        var a01 = 0.0;
        var a02 = 0.0;
        var a11 = 0.0;
        var a12 = 0.0;
        var a22 = 0.0;
        var b0 = 0.0;
        var b1 = 0.0;
        var b2 = 0.0;

        foreach (var ray in rays)
        {
            var d = Vector3.Normalize(ray.Direction);
            var weight = Math.Max(1.0e-6, ray.Confidence);
            var m00 = 1.0 - d.X * d.X;
            var m01 = 0.0 - d.X * d.Y;
            var m02 = 0.0 - d.X * d.Z;
            var m11 = 1.0 - d.Y * d.Y;
            var m12 = 0.0 - d.Y * d.Z;
            var m22 = 1.0 - d.Z * d.Z;

            a00 += weight * m00;
            a01 += weight * m01;
            a02 += weight * m02;
            a11 += weight * m11;
            a12 += weight * m12;
            a22 += weight * m22;
            b0 += weight * (m00 * ray.Scene.X + m01 * ray.Scene.Y + m02 * ray.Scene.Z);
            b1 += weight * (m01 * ray.Scene.X + m11 * ray.Scene.Y + m12 * ray.Scene.Z);
            b2 += weight * (m02 * ray.Scene.X + m12 * ray.Scene.Y + m22 * ray.Scene.Z);
        }

        return TrySolveSymmetric3x3(a00, a01, a02, a11, a12, a22, b0, b1, b2, out position);
    }

    private static bool TrySolveSymmetric3x3(
        double a00,
        double a01,
        double a02,
        double a11,
        double a12,
        double a22,
        double b0,
        double b1,
        double b2,
        out Vector3 solution)
    {
        var det =
            a00 * (a11 * a22 - a12 * a12) -
            a01 * (a01 * a22 - a12 * a02) +
            a02 * (a01 * a12 - a11 * a02);
        if (Math.Abs(det) < 1.0e-9)
        {
            solution = default;
            return false;
        }

        var x =
            b0 * (a11 * a22 - a12 * a12) -
            a01 * (b1 * a22 - a12 * b2) +
            a02 * (b1 * a12 - a11 * b2);
        var y =
            a00 * (b1 * a22 - a12 * b2) -
            b0 * (a01 * a22 - a12 * a02) +
            a02 * (a01 * b2 - b1 * a02);
        var z =
            a00 * (a11 * b2 - b1 * a12) -
            a01 * (a01 * b2 - b1 * a02) +
            b0 * (a01 * a12 - a11 * a02);
        solution = new Vector3((float)(x / det), (float)(y / det), (float)(z / det));
        return true;
    }

    private static double DistancePointToRay(Vector3 point, Vector3 origin, Vector3 direction)
    {
        var delta = point - origin;
        var projection = Vector3.Dot(delta, direction);
        var nearest = origin + direction * projection;
        return Vector3.Distance(point, nearest);
    }

    private static Vector3 ClampLength(Vector3 value, float maximumLength)
    {
        if (maximumLength <= 0.0f)
        {
            return Vector3.Zero;
        }

        var length = value.Length();
        return length <= maximumLength || length <= 1.0e-9f
            ? value
            : value / length * maximumLength;
    }

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

    private readonly record struct RayPoint(Vector3 Scene, Vector3 Direction, double Confidence);
}
