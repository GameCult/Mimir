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

public readonly record struct MimirFeatureTrackScenePoint(
    int TrackId,
    Vector3 PositionMeters,
    double Confidence);

public sealed record MimirCameraPoseEstimate(
    string SourceId,
    Vector3 PositionMeters,
    Quaternion CameraToWorldRotation,
    double Confidence);

public sealed record MimirCameraFrustumEstimate(
    string SourceId,
    Vector3 PositionMeters,
    Quaternion CameraToWorldRotation,
    double HorizontalTanHalfFov,
    double VerticalTanHalfFov,
    double Confidence)
{
    public static MimirCameraFrustumEstimate FromPose(
        MimirCameraPoseEstimate pose,
        double horizontalTanHalfFov = 1.0,
        double verticalTanHalfFov = 1.0) =>
        new(
            pose.SourceId,
            pose.PositionMeters,
            pose.CameraToWorldRotation,
            horizontalTanHalfFov,
            verticalTanHalfFov,
            pose.Confidence);
}

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

public sealed record MimirCameraFrustumSolveUpdate(
    string SourceId,
    Vector3 EstimatedPositionMeters,
    Quaternion EstimatedCameraToWorldRotation,
    double HorizontalTanHalfFov,
    double VerticalTanHalfFov,
    Vector3 DeltaMeters,
    double RotationDeltaDegrees,
    int UsedPointCount,
    double MeanReprojectionErrorClip,
    double Confidence);

public sealed record MimirCameraFrustumSolveFrame(
    string CalibrationId,
    string CandidateKey,
    string MarkerSetId,
    IReadOnlyList<MimirCameraFrustumSolveUpdate> FrustumUpdates,
    double MeanReprojectionErrorClip,
    double Confidence,
    bool HasFrustumUpdate);

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

public sealed class MimirCameraFrustumCalibrationSolver
{
    private const int ParameterCount = 8;

    public MimirCameraFrustumSolveFrame FitFrustumsFromLedSpline(
        MimirLedSplineFieldCandidate candidate,
        IEnumerable<MimirLedSplineScenePoint> scenePoints,
        IEnumerable<MimirCameraFrustumEstimate> currentFrustums,
        double maximumDeltaMeters = 0.50,
        double maximumRotationDeltaDegrees = 30.0,
        double minimumTanHalfFov = 0.20,
        double maximumTanHalfFov = 2.50)
    {
        var sceneByLed = scenePoints
            .Where(static point => point.LedIndex >= 0 && point.Confidence > 0.0)
            .GroupBy(static point => point.LedIndex)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(point => point.Confidence).First());
        var observations = candidate.CameraObservations
            .Select(observation => new FrustumObservation(
                observation.SourceId,
                observation.Points
                .Where(point => sceneByLed.ContainsKey(point.LedIndex) && point.Confidence > 0.0)
                .Select(point =>
                {
                    var scene = sceneByLed[point.LedIndex];
                    return new FrustumCorrespondence(
                        scene.PositionMeters,
                        point.ClipX,
                        point.ClipY,
                        Math.Min(point.Confidence, scene.Confidence));
                })
                .ToArray()))
            .ToArray();
        return FitFrustums(
            candidate.CalibrationId,
            candidate.CandidateKey,
            candidate.SplineId,
            candidate.Confidence,
            observations,
            currentFrustums,
            maximumDeltaMeters,
            maximumRotationDeltaDegrees,
            minimumTanHalfFov,
            maximumTanHalfFov);
    }

    public MimirCameraFrustumSolveFrame FitFrustumsFromFeatureTracks(
        MimirFeatureTrackFieldCandidate candidate,
        IEnumerable<MimirFeatureTrackScenePoint> scenePoints,
        IEnumerable<MimirCameraFrustumEstimate> currentFrustums,
        string markerSetId = "feature-tracks",
        double maximumDeltaMeters = 0.50,
        double maximumRotationDeltaDegrees = 30.0,
        double minimumTanHalfFov = 0.20,
        double maximumTanHalfFov = 2.50)
    {
        var sceneByTrack = scenePoints
            .Where(static point => point.TrackId >= 0 && point.Confidence > 0.0)
            .GroupBy(static point => point.TrackId)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(point => point.Confidence).First());
        var observations = candidate.CameraObservations
            .Select(observation => new FrustumObservation(
                observation.SourceId,
                observation.Tracks
                    .Where(track => sceneByTrack.ContainsKey(track.TrackId) && track.Confidence > 0.0)
                    .Select(track =>
                    {
                        var scene = sceneByTrack[track.TrackId];
                        return new FrustumCorrespondence(
                            scene.PositionMeters,
                            track.ClipX,
                            track.ClipY,
                            Math.Min(track.Confidence, scene.Confidence));
                    })
                    .ToArray()))
            .ToArray();
        return FitFrustums(
            candidate.CalibrationId,
            candidate.CandidateKey,
            markerSetId,
            candidate.Confidence,
            observations,
            currentFrustums,
            maximumDeltaMeters,
            maximumRotationDeltaDegrees,
            minimumTanHalfFov,
            maximumTanHalfFov);
    }

    private static MimirCameraFrustumSolveFrame FitFrustums(
        string calibrationId,
        string candidateKey,
        string markerSetId,
        double candidateConfidence,
        IReadOnlyList<FrustumObservation> observations,
        IEnumerable<MimirCameraFrustumEstimate> currentFrustums,
        double maximumDeltaMeters,
        double maximumRotationDeltaDegrees,
        double minimumTanHalfFov,
        double maximumTanHalfFov)
    {
        var frustumBySource = currentFrustums.ToDictionary(static frustum => frustum.SourceId, StringComparer.Ordinal);
        var updates = new List<MimirCameraFrustumSolveUpdate>();

        foreach (var observation in observations)
        {
            if (!frustumBySource.TryGetValue(observation.SourceId, out var seed) ||
                seed.Confidence <= 0.0 ||
                observation.Correspondences.Count < 4)
            {
                continue;
            }

            var correspondences = observation.Correspondences;
            if (!TryFitFrustum(
                correspondences,
                seed,
                minimumTanHalfFov,
                maximumTanHalfFov,
                out var solved,
                out _))
            {
                continue;
            }

            var delta = solved.PositionMeters - seed.PositionMeters;
            var clampedDelta = ClampLength(delta, (float)Math.Max(0.0, maximumDeltaMeters));
            var rotationDeltaDegrees = RotationDeltaDegrees(seed.CameraToWorldRotation, solved.CameraToWorldRotation);
            var finalRotation = solved.CameraToWorldRotation;
            if (rotationDeltaDegrees > maximumRotationDeltaDegrees && rotationDeltaDegrees > 1.0e-9)
            {
                var amount = (float)(maximumRotationDeltaDegrees / rotationDeltaDegrees);
                finalRotation = Quaternion.Normalize(Quaternion.Slerp(seed.CameraToWorldRotation, solved.CameraToWorldRotation, amount));
                rotationDeltaDegrees = maximumRotationDeltaDegrees;
            }

            var finalEstimate = solved with
            {
                PositionMeters = seed.PositionMeters + clampedDelta,
                CameraToWorldRotation = finalRotation,
                HorizontalTanHalfFov = Clamp(solved.HorizontalTanHalfFov, minimumTanHalfFov, maximumTanHalfFov),
                VerticalTanHalfFov = Clamp(solved.VerticalTanHalfFov, minimumTanHalfFov, maximumTanHalfFov),
            };
            var finalError = MeanReprojectionError(correspondences, finalEstimate);
            var confidence = Clamp01(candidateConfidence) *
                Clamp01(seed.Confidence) *
                correspondences.Average(static point => Clamp01(point.Confidence)) *
                (1.0 / (1.0 + finalError * 4.0));
            updates.Add(new MimirCameraFrustumSolveUpdate(
                observation.SourceId,
                finalEstimate.PositionMeters,
                finalEstimate.CameraToWorldRotation,
                finalEstimate.HorizontalTanHalfFov,
                finalEstimate.VerticalTanHalfFov,
                clampedDelta,
                rotationDeltaDegrees,
                correspondences.Count,
                finalError,
                confidence));
        }

        var meanFrameError = updates.Count == 0
            ? double.NaN
            : updates.Average(static update => update.MeanReprojectionErrorClip);
        var frameConfidence = updates.Count == 0
            ? 0.0
            : updates.Average(static update => update.Confidence);
        return new MimirCameraFrustumSolveFrame(
            calibrationId,
            candidateKey,
            markerSetId,
            updates,
            meanFrameError,
            frameConfidence,
            HasFrustumUpdate: updates.Count > 0);
    }

    private static bool TryFitFrustum(
        IReadOnlyList<FrustumCorrespondence> correspondences,
        MimirCameraFrustumEstimate seed,
        double minimumTanHalfFov,
        double maximumTanHalfFov,
        out MimirCameraFrustumEstimate solved,
        out double meanError)
    {
        var parameters = new[]
        {
            (double)seed.PositionMeters.X,
            (double)seed.PositionMeters.Y,
            (double)seed.PositionMeters.Z,
            0.0,
            0.0,
            0.0,
            Math.Log(Clamp(seed.HorizontalTanHalfFov, minimumTanHalfFov, maximumTanHalfFov)),
            Math.Log(Clamp(seed.VerticalTanHalfFov, minimumTanHalfFov, maximumTanHalfFov)),
        };
        var lambda = 1.0e-3;
        var residuals = Residuals(correspondences, seed, parameters, minimumTanHalfFov, maximumTanHalfFov);
        var bestCost = Cost(residuals);

        for (var iteration = 0; iteration < 24; iteration++)
        {
            var jacobian = Jacobian(correspondences, seed, parameters, residuals, minimumTanHalfFov, maximumTanHalfFov);
            var normal = new double[ParameterCount, ParameterCount];
            var rhs = new double[ParameterCount];
            for (var residualIndex = 0; residualIndex < residuals.Length; residualIndex++)
            {
                for (var column = 0; column < ParameterCount; column++)
                {
                    var j = jacobian[residualIndex, column];
                    rhs[column] -= j * residuals[residualIndex];
                    for (var row = 0; row < ParameterCount; row++)
                    {
                        normal[column, row] += j * jacobian[residualIndex, row];
                    }
                }
            }

            for (var index = 0; index < ParameterCount; index++)
            {
                normal[index, index] += lambda * (normal[index, index] + 1.0);
            }

            if (!TrySolveLinearSystem(normal, rhs, out var step))
            {
                lambda *= 10.0;
                continue;
            }

            var candidate = parameters.ToArray();
            for (var index = 0; index < ParameterCount; index++)
            {
                candidate[index] += step[index];
            }

            var candidateResiduals = Residuals(correspondences, seed, candidate, minimumTanHalfFov, maximumTanHalfFov);
            var candidateCost = Cost(candidateResiduals);
            if (candidateCost < bestCost)
            {
                parameters = candidate;
                residuals = candidateResiduals;
                bestCost = candidateCost;
                lambda = Math.Max(1.0e-9, lambda * 0.35);
            }
            else
            {
                lambda *= 8.0;
            }
        }

        solved = BuildEstimate(seed, parameters, minimumTanHalfFov, maximumTanHalfFov);
        meanError = MeanReprojectionError(correspondences, solved);
        return double.IsFinite(meanError);
    }

    private static double[,] Jacobian(
        IReadOnlyList<FrustumCorrespondence> correspondences,
        MimirCameraFrustumEstimate seed,
        IReadOnlyList<double> parameters,
        IReadOnlyList<double> residuals,
        double minimumTanHalfFov,
        double maximumTanHalfFov)
    {
        var jacobian = new double[residuals.Count, ParameterCount];
        var epsilons = new[] { 1.0e-4, 1.0e-4, 1.0e-4, 1.0e-5, 1.0e-5, 1.0e-5, 1.0e-5, 1.0e-5 };
        for (var column = 0; column < ParameterCount; column++)
        {
            var shifted = parameters.ToArray();
            shifted[column] += epsilons[column];
            var shiftedResiduals = Residuals(correspondences, seed, shifted, minimumTanHalfFov, maximumTanHalfFov);
            for (var residualIndex = 0; residualIndex < residuals.Count; residualIndex++)
            {
                jacobian[residualIndex, column] = (shiftedResiduals[residualIndex] - residuals[residualIndex]) / epsilons[column];
            }
        }

        return jacobian;
    }

    private static double[] Residuals(
        IReadOnlyList<FrustumCorrespondence> correspondences,
        MimirCameraFrustumEstimate seed,
        IReadOnlyList<double> parameters,
        double minimumTanHalfFov,
        double maximumTanHalfFov)
    {
        var estimate = BuildEstimate(seed, parameters, minimumTanHalfFov, maximumTanHalfFov);
        var residuals = new double[correspondences.Count * 2];
        for (var index = 0; index < correspondences.Count; index++)
        {
            var correspondence = correspondences[index];
            var weight = Math.Sqrt(Math.Max(1.0e-6, correspondence.Confidence));
            if (!TryProject(correspondence.Scene, estimate, out var clipX, out var clipY))
            {
                residuals[index * 2] = 8.0 * weight;
                residuals[index * 2 + 1] = 8.0 * weight;
                continue;
            }

            residuals[index * 2] = (clipX - correspondence.ClipX) * weight;
            residuals[index * 2 + 1] = (clipY - correspondence.ClipY) * weight;
        }

        return residuals;
    }

    private static MimirCameraFrustumEstimate BuildEstimate(
        MimirCameraFrustumEstimate seed,
        IReadOnlyList<double> parameters,
        double minimumTanHalfFov,
        double maximumTanHalfFov)
    {
        var deltaRotation = Quaternion.CreateFromYawPitchRoll(
            (float)parameters[3],
            (float)parameters[4],
            (float)parameters[5]);
        return seed with
        {
            PositionMeters = new Vector3((float)parameters[0], (float)parameters[1], (float)parameters[2]),
            CameraToWorldRotation = Quaternion.Normalize(deltaRotation * seed.CameraToWorldRotation),
            HorizontalTanHalfFov = Clamp(Math.Exp(parameters[6]), minimumTanHalfFov, maximumTanHalfFov),
            VerticalTanHalfFov = Clamp(Math.Exp(parameters[7]), minimumTanHalfFov, maximumTanHalfFov),
        };
    }

    private static double MeanReprojectionError(
        IReadOnlyList<FrustumCorrespondence> correspondences,
        MimirCameraFrustumEstimate estimate)
    {
        var sum = 0.0;
        var weightSum = 0.0;
        foreach (var correspondence in correspondences)
        {
            if (!TryProject(correspondence.Scene, estimate, out var clipX, out var clipY))
            {
                continue;
            }

            var dx = clipX - correspondence.ClipX;
            var dy = clipY - correspondence.ClipY;
            var weight = Math.Max(1.0e-6, correspondence.Confidence);
            sum += Math.Sqrt(dx * dx + dy * dy) * weight;
            weightSum += weight;
        }

        return weightSum <= 0.0 ? double.NaN : sum / weightSum;
    }

    private static bool TryProject(
        Vector3 scene,
        MimirCameraFrustumEstimate estimate,
        out double clipX,
        out double clipY)
    {
        var worldToCamera = Quaternion.Inverse(estimate.CameraToWorldRotation);
        var camera = Vector3.Transform(scene - estimate.PositionMeters, worldToCamera);
        if (camera.Z <= 1.0e-5f)
        {
            clipX = 0.0;
            clipY = 0.0;
            return false;
        }

        clipX = camera.X / (camera.Z * Math.Max(1.0e-6, estimate.HorizontalTanHalfFov));
        clipY = camera.Y / (camera.Z * Math.Max(1.0e-6, estimate.VerticalTanHalfFov));
        return double.IsFinite(clipX) && double.IsFinite(clipY);
    }

    private static double Cost(IReadOnlyList<double> residuals) =>
        residuals.Count == 0 ? double.PositiveInfinity : residuals.Average(static residual => residual * residual);

    private static bool TrySolveLinearSystem(double[,] matrix, double[] rhs, out double[] solution)
    {
        var size = rhs.Length;
        var a = new double[size, size + 1];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                a[row, column] = matrix[row, column];
            }

            a[row, size] = rhs[row];
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var bestRow = pivot;
            var best = Math.Abs(a[pivot, pivot]);
            for (var row = pivot + 1; row < size; row++)
            {
                var value = Math.Abs(a[row, pivot]);
                if (value > best)
                {
                    best = value;
                    bestRow = row;
                }
            }

            if (best < 1.0e-12)
            {
                solution = [];
                return false;
            }

            if (bestRow != pivot)
            {
                for (var column = pivot; column <= size; column++)
                {
                    (a[pivot, column], a[bestRow, column]) = (a[bestRow, column], a[pivot, column]);
                }
            }

            var pivotValue = a[pivot, pivot];
            for (var column = pivot; column <= size; column++)
            {
                a[pivot, column] /= pivotValue;
            }

            for (var row = 0; row < size; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = a[row, pivot];
                for (var column = pivot; column <= size; column++)
                {
                    a[row, column] -= factor * a[pivot, column];
                }
            }
        }

        solution = new double[size];
        for (var row = 0; row < size; row++)
        {
            solution[row] = a[row, size];
        }

        return solution.All(double.IsFinite);
    }

    private static double RotationDeltaDegrees(Quaternion left, Quaternion right)
    {
        var dot = Math.Abs(Quaternion.Dot(Quaternion.Normalize(left), Quaternion.Normalize(right)));
        dot = Math.Clamp(dot, 0.0f, 1.0f);
        return Math.Acos(dot) * 2.0 * 180.0 / Math.PI;
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

    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

    private sealed record FrustumObservation(string SourceId, IReadOnlyList<FrustumCorrespondence> Correspondences);

    private readonly record struct FrustumCorrespondence(Vector3 Scene, double ClipX, double ClipY, double Confidence);
}
