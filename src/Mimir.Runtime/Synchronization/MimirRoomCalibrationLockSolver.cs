namespace Mimir.Runtime.Synchronization;

public enum MimirRoomCalibrationLockStatus
{
    Empty,
    InsufficientWitnesses,
    Provisional,
    Locked,
}

public sealed record MimirRoomCalibrationLockThresholds(
    int MinimumLedCameraCount = 2,
    int MinimumSharedLedCount = 4,
    int MinimumPoseUpdateCount = 2,
    double MaximumMeanRayDistanceMeters = 0.035,
    int MinimumMoveControllerCount = 2,
    int MinimumStableMoveTrackCount = 2,
    int MinimumEyeSourceCount = 2,
    int MinimumStableEyeTrackCount = 80,
    double MinimumConfidence = 0.55);

public sealed record MimirRoomCalibrationLockFrame(
    string CalibrationId,
    MimirRoomCalibrationLockStatus Status,
    int LedCameraCount,
    int SharedLedCount,
    int PoseUpdateCount,
    double MeanRayDistanceMeters,
    int MoveControllerCount,
    int StableMoveTrackCount,
    int EyeSourceCount,
    int StableEyeTrackCount,
    double Confidence,
    IReadOnlyList<string> MissingWitnesses,
    bool HasLock);

public sealed class MimirRoomCalibrationLockSolver(MimirRoomCalibrationLockThresholds? thresholds = null)
{
    private readonly MimirRoomCalibrationLockThresholds thresholds = thresholds ?? new();

    public MimirRoomCalibrationLockFrame Evaluate(
        string calibrationId,
        MimirLedSplineFieldCandidate? ledSpline,
        MimirVisualCalibrationResidualFrame? ledResidual,
        MimirCameraRigCalibrationFrame? rigCalibration,
        IEnumerable<MimirFeatureTrackFieldCandidate> moveControllerCandidates,
        IEnumerable<MimirFeatureTrackFieldCandidate> eyeFeatureCandidates)
    {
        var ledCameraCount = ledSpline?.CameraObservations.Count ?? 0;
        var sharedLedCount = ledResidual?.PairResiduals.Count > 0
            ? ledResidual.PairResiduals.Max(static residual => residual.SharedLedCount)
            : 0;
        var poseUpdateCount = rigCalibration?.PoseUpdates.Count ?? 0;
        var meanRayDistance = rigCalibration?.MeanRayDistanceMeters ?? double.NaN;

        var moveCandidates = moveControllerCandidates.ToArray();
        var moveControllerCount = moveCandidates
            .SelectMany(static candidate => candidate.CameraObservations)
            .SelectMany(static observation => observation.Tracks)
            .Select(static track => track.TrackId)
            .Distinct()
            .Count();
        var stableMoveTrackCount = moveCandidates.Sum(static candidate => candidate.StableTrackCount);

        var eyeCandidates = eyeFeatureCandidates.ToArray();
        var eyeSourceCount = eyeCandidates
            .SelectMany(static candidate => candidate.CameraObservations)
            .Select(static observation => observation.SourceId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var stableEyeTrackCount = eyeCandidates.Sum(static candidate => candidate.StableTrackCount);

        var missing = new List<string>();
        if (ledCameraCount < thresholds.MinimumLedCameraCount ||
            sharedLedCount < thresholds.MinimumSharedLedCount)
        {
            missing.Add("led-spline-correspondence");
        }

        if (poseUpdateCount < thresholds.MinimumPoseUpdateCount ||
            !double.IsFinite(meanRayDistance) ||
            meanRayDistance > thresholds.MaximumMeanRayDistanceMeters)
        {
            missing.Add("camera-pose-update");
        }

        if (moveControllerCount < thresholds.MinimumMoveControllerCount ||
            stableMoveTrackCount < thresholds.MinimumStableMoveTrackCount)
        {
            missing.Add("move-controller-history");
        }

        if (eyeSourceCount < thresholds.MinimumEyeSourceCount ||
            stableEyeTrackCount < thresholds.MinimumStableEyeTrackCount)
        {
            missing.Add("ps3-eye-feature-field");
        }

        var confidence = Confidence(
            ledSpline,
            ledResidual,
            rigCalibration,
            moveCandidates,
            eyeCandidates,
            sharedLedCount,
            stableEyeTrackCount);
        var hasAllWitnesses = missing.Count == 0;
        var status = Status(hasAllWitnesses, confidence, ledCameraCount, moveCandidates.Length, eyeCandidates.Length);
        return new MimirRoomCalibrationLockFrame(
            calibrationId,
            status,
            ledCameraCount,
            sharedLedCount,
            poseUpdateCount,
            meanRayDistance,
            moveControllerCount,
            stableMoveTrackCount,
            eyeSourceCount,
            stableEyeTrackCount,
            confidence,
            missing,
            HasLock: status == MimirRoomCalibrationLockStatus.Locked);
    }

    private MimirRoomCalibrationLockStatus Status(
        bool hasAllWitnesses,
        double confidence,
        int ledCameraCount,
        int moveCandidateCount,
        int eyeCandidateCount)
    {
        if (ledCameraCount == 0 && moveCandidateCount == 0 && eyeCandidateCount == 0)
        {
            return MimirRoomCalibrationLockStatus.Empty;
        }

        if (!hasAllWitnesses)
        {
            return MimirRoomCalibrationLockStatus.InsufficientWitnesses;
        }

        return confidence >= thresholds.MinimumConfidence
            ? MimirRoomCalibrationLockStatus.Locked
            : MimirRoomCalibrationLockStatus.Provisional;
    }

    private static double Confidence(
        MimirLedSplineFieldCandidate? ledSpline,
        MimirVisualCalibrationResidualFrame? ledResidual,
        MimirCameraRigCalibrationFrame? rigCalibration,
        IReadOnlyList<MimirFeatureTrackFieldCandidate> moveCandidates,
        IReadOnlyList<MimirFeatureTrackFieldCandidate> eyeCandidates,
        int sharedLedCount,
        int stableEyeTrackCount)
    {
        var ledConfidence = ledSpline is null || ledResidual is null
            ? 0.0
            : Clamp01(ledSpline.Confidence) *
                Clamp01(ledResidual.Confidence) *
                Clamp01(sharedLedCount / 5.0);
        var poseConfidence = rigCalibration is null || !rigCalibration.HasPoseUpdate
            ? 0.0
            : Clamp01(rigCalibration.Confidence) *
                (double.IsFinite(rigCalibration.MeanRayDistanceMeters)
                    ? 1.0 / (1.0 + rigCalibration.MeanRayDistanceMeters * 40.0)
                    : 0.0);
        var moveConfidence = moveCandidates.Count == 0
            ? 0.0
            : Clamp01(moveCandidates.Average(static candidate => candidate.Confidence));
        var eyeConfidence = eyeCandidates.Count == 0
            ? 0.0
            : Clamp01(eyeCandidates.Average(static candidate => candidate.Confidence)) *
                Clamp01(stableEyeTrackCount / 160.0);
        return Clamp01(0.40 * ledConfidence + 0.30 * poseConfidence + 0.15 * moveConfidence + 0.15 * eyeConfidence);
    }

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
}
